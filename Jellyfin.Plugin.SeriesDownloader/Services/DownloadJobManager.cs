using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SeriesDownloader.Configuration;
using Jellyfin.Plugin.SeriesDownloader.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesDownloader.Services;

/// <summary>
/// Manages background transcode and ZIP packaging jobs for Series Downloader.
/// </summary>
public sealed class DownloadJobManager : IHostedService, IDisposable
{
    private static readonly HashSet<string> AllowedVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "libx264", "libx265", "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_vaapi", "hevc_vaapi", "h264_amf", "hevc_amf", "copy"
    };

    private static readonly HashSet<string> AllowedAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "libmp3lame", "libopus", "ac3", "copy"
    };

    private static readonly HashSet<string> AllowedPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow"
    };

    private static readonly Regex SafeLangRegex = new("^[a-zA-Z0-9_-]{1,16}$", RegexOptions.Compiled);
    private static readonly Regex SafeHexIdRegex = new("^[a-fA-F0-9]{12,64}$", RegexOptions.Compiled);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<DownloadJobManager> _logger;
    private readonly ConcurrentDictionary<string, DownloadJobEntry> _jobs = new();
    private readonly ConcurrentDictionary<string, StreamTicketEntry> _streamTickets = new();
    private Timer? _cleanupTimer;
    private SemaphoreSlim _concurrencyLimiter = new(1, 1);
    private int _configuredConcurrency = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadJobManager"/> class.
    /// </summary>
    public DownloadJobManager(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IApplicationPaths appPaths,
        ILogger<DownloadJobManager> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer = new Timer(CleanupExpiredJobs, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        foreach (var entry in _jobs.Values)
        {
            try
            {
                entry.Cts.Cancel();
            }
            catch
            {
                // Ignore cancellation exceptions on shutdown
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the series/season/episode tree for a given item ID.
    /// </summary>
    /// <param name="itemId">The Series, Season, Episode, or Video ID.</param>
    /// <returns>A structured <see cref="SeriesTreeDto"/> or null if not found.</returns>
    public SeriesTreeDto? GetItemTree(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var tree = new SeriesTreeDto
        {
            ItemId = item.Id,
            Title = item.Name ?? "Media"
        };

        var audioLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subtitleLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (item is Series series)
        {
            tree.ItemType = "Series";
            tree.SeriesName = series.Name ?? "Series";
            tree.Title = series.Name ?? "Series";

            var allEpisodes = GetDownloadableEpisodes(series.Id, series);
            PopulateSeasonsFromEpisodes(tree, allEpisodes, null, audioLanguages, subtitleLanguages);
        }
        else if (item is Season season)
        {
            tree.ItemType = "Season";
            var parentSeries = TryGetSeries(season);
            tree.SeriesName = parentSeries?.Name ?? season.SeriesName ?? season.Name ?? "Series";
            tree.Title = $"{tree.SeriesName} - {season.Name}";

            var allEpisodes = parentSeries is not null
                ? GetDownloadableEpisodes(parentSeries.Id, parentSeries)
                : GetDownloadableEpisodes(season.Id, season);

            PopulateSeasonsFromEpisodes(
                tree,
                allEpisodes,
                e => e.SeasonId == season.Id || (e.ParentIndexNumber.HasValue && e.ParentIndexNumber == season.IndexNumber),
                audioLanguages,
                subtitleLanguages);
        }
        else if (item is Episode episode)
        {
            tree.ItemType = "Episode";
            var parentSeries = TryGetSeries(episode);
            tree.SeriesName = parentSeries?.Name ?? episode.SeriesName ?? "Series";
            var sNum = episode.ParentIndexNumber ?? 1;
            var eNum = episode.IndexNumber ?? 1;
            tree.Title = $"{tree.SeriesName} S{sNum:D2}E{eNum:D2} - {episode.Name}";

            var allEpisodes = parentSeries is not null
                ? GetDownloadableEpisodes(parentSeries.Id, parentSeries)
                : new List<Episode> { episode };

            PopulateSeasonsFromEpisodes(
                tree,
                allEpisodes,
                e => e.Id == episode.Id,
                audioLanguages,
                subtitleLanguages);
        }
        else if (item is Video video && !string.IsNullOrEmpty(video.Path) && File.Exists(video.Path))
        {
            tree.ItemType = item.GetType().Name;
            tree.SeriesName = video.Name ?? "Video";
            tree.Title = video.Name ?? "Video";

            var node = CreateEpisodeNode(video, 1, 1, true, audioLanguages, subtitleLanguages);
            tree.Seasons.Add(new SeasonNodeDto
            {
                SeasonId = video.Id,
                SeasonNumber = 1,
                SeasonName = "Media",
                Episodes = new List<EpisodeNodeDto> { node }
            });
        }
        else
        {
            return null;
        }

        tree.AvailableAudioLanguages = audioLanguages.OrderBy(x => x).ToList();
        tree.AvailableSubtitleLanguages = subtitleLanguages.OrderBy(x => x).ToList();

        var (detectedEncoder, detectedSummary, _) = DetectHardwareAcceleration();
        tree.DetectedVideoEncoder = detectedEncoder;
        tree.DetectedHardwareSummary = detectedSummary;
        return tree;
    }

    /// <summary>
    /// Detects Jellyfin's configured hardware acceleration from encoding.xml and plugin configuration.
    /// </summary>
    public (string DefaultEncoder, string Summary, string VaapiDevice) DetectHardwareAcceleration()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var vaapiDevice = "/dev/dri/renderD128";
        var hwType = "none";

        try
        {
            var encodingXmlPath = Path.Combine(_appPaths.ConfigurationDirectoryPath, "encoding.xml");
            if (File.Exists(encodingXmlPath))
            {
                var doc = XDocument.Load(encodingXmlPath);
                hwType = doc.Root?.Element("HardwareAccelerationType")?.Value?.Trim()?.ToLowerInvariant() ?? "none";
                var dev = doc.Root?.Element("VaapiDevice")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(dev))
                {
                    vaapiDevice = dev;
                }
            }
        }
        catch
        {
            // Ignore XML read errors
        }

        // If user explicitly chose a hardware encoder in Plugin Settings, prefer that
        if (!string.IsNullOrWhiteSpace(config.DefaultVideoCodec) &&
            !string.Equals(config.DefaultVideoCodec, "libx264", StringComparison.OrdinalIgnoreCase))
        {
            return (config.DefaultVideoCodec, $"Plugin Default: {config.DefaultVideoCodec}", vaapiDevice);
        }

        return hwType switch
        {
            "nvenc" => ("h264_nvenc", "NVIDIA NVENC (CUDA Hardware Decode + Encode)", vaapiDevice),
            "qsv" => ("h264_qsv", "Intel QuickSync QSV (Hardware Decode + Encode)", vaapiDevice),
            "vaapi" => ("h264_vaapi", $"VAAPI ({vaapiDevice} Hardware Decode + Encode)", vaapiDevice),
            "amf" => ("h264_amf", "AMD AMF (Hardware Decode + Encode)", vaapiDevice),
            _ => File.Exists("/dev/nvidia0")
                ? ("h264_nvenc", "NVIDIA GPU Detected (/dev/nvidia0 — NVENC Available)", vaapiDevice)
                : File.Exists(vaapiDevice)
                    ? ("h264_vaapi", $"GPU Render Node Detected ({vaapiDevice} — VAAPI/QSV Available)", vaapiDevice)
                    : (config.DefaultVideoCodec, "CPU Software Encoding (libx264)", vaapiDevice)
        };
    }

    /// <summary>
    /// Lists all TV series in the library using version-agnostic reflection.
    /// </summary>
    public List<object> GetAllSeries()
    {
        var seriesItems = SafeQueryItems(null, BaseItemKind.Series, null)
            .OfType<Series>()
            .OrderBy(s => s.SortName ?? s.Name)
            .Select(s => (object)new
            {
                Id = s.Id,
                Name = s.Name ?? "Unknown Series",
                ProductionYear = s.ProductionYear
            })
            .ToList();

        return seriesItems;
    }

    private List<Episode> GetDownloadableEpisodes(Guid ancestorId, Folder? fallbackFolder)
    {
        var episodes = SafeQueryItems(ancestorId, BaseItemKind.Episode, fallbackFolder)
            .OfType<Episode>()
            .Where(e => !e.IsVirtualItem && !string.IsNullOrEmpty(e.Path) && File.Exists(e.Path))
            .OrderBy(e => e.ParentIndexNumber ?? 9999)
            .ThenBy(e => e.IndexNumber ?? 9999)
            .ThenBy(e => e.Name)
            .ToList();

        return episodes;
    }

    private IEnumerable<BaseItem> SafeQueryItems(Guid? ancestorId, BaseItemKind? itemKind, Folder? fallbackFolder)
    {
        try
        {
            var query = new InternalItemsQuery();
            var qType = typeof(InternalItemsQuery);

            qType.GetProperty("Recursive")?.SetValue(query, true);
            qType.GetProperty("IsVirtualItem")?.SetValue(query, false);

            if (ancestorId.HasValue && ancestorId.Value != Guid.Empty)
            {
                qType.GetProperty("AncestorIds")?.SetValue(query, new[] { ancestorId.Value });
            }

            if (itemKind.HasValue)
            {
                qType.GetProperty("IncludeItemTypes")?.SetValue(query, new[] { itemKind.Value });
            }

            var getItemListMethod = _libraryManager.GetType().GetMethod("GetItemList", new[] { typeof(InternalItemsQuery) })
                ?? typeof(ILibraryManager).GetMethod("GetItemList", new[] { typeof(InternalItemsQuery) });

            if (getItemListMethod?.Invoke(_libraryManager, new object[] { query }) is IEnumerable<BaseItem> result)
            {
                var list = result.ToList();
                if (list.Count > 0 || fallbackFolder is null)
                {
                    return list;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SafeQueryItems GetItemList fallback for {AncestorId}", ancestorId);
        }

        if (fallbackFolder is not null)
        {
            try
            {
                var getRecMethod = fallbackFolder.GetType().GetMethod("GetRecursiveChildren", Type.EmptyTypes)
                    ?? typeof(Folder).GetMethod("GetRecursiveChildren", Type.EmptyTypes);
                if (getRecMethod?.Invoke(fallbackFolder, null) is IEnumerable<BaseItem> children)
                {
                    return children.ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetRecursiveChildren failed for {AncestorId}", ancestorId);
            }
        }

        return Array.Empty<BaseItem>();
    }

    private static Series? TryGetSeries(Season season)
    {
        try
        {
            return season.Series;
        }
        catch
        {
            return null;
        }
    }

    private static Series? TryGetSeries(Episode episode)
    {
        try
        {
            return episode.Series;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates and queues a new download/transcode job.
    /// </summary>
    public DownloadJobStatusDto CreateJob(Guid userId, StartDownloadJobRequest request)
    {
        if (request.EpisodeIds is null || request.EpisodeIds.Count == 0)
        {
            throw new ArgumentException("At least one episode or video must be selected.");
        }

        if (request.EpisodeIds.Count > 500)
        {
            throw new ArgumentException("A maximum of 500 episodes can be queued in a single job.");
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        ValidateAndNormalizeRequest(request, config);

        var items = new List<Video>();
        foreach (var id in request.EpisodeIds.Distinct())
        {
            if (_libraryManager.GetItemById(id) is Video video && !string.IsNullOrEmpty(video.Path) && File.Exists(video.Path))
            {
                items.Add(video);
            }
        }

        if (items.Count == 0)
        {
            throw new InvalidOperationException("None of the selected episodes have accessible media files on disk.");
        }

        items = items
            .OrderBy(v => (v as Episode)?.ParentIndexNumber ?? 1)
            .ThenBy(v => (v as Episode)?.IndexNumber ?? 1)
            .ThenBy(v => v.Name)
            .ToList();

        UpdateConcurrencyIfNeeded(Math.Clamp(config.MaxConcurrentTranscodes, 1, 8));

        var jobId = RandomNumberGenerator.GetHexString(16, lowercase: true);
        var downloadToken = RandomNumberGenerator.GetHexString(64, lowercase: true);
        var baseWorkDir = GetBaseTempDirectory(config);
        var jobWorkDir = Path.Combine(baseWorkDir, jobId);
        Directory.CreateDirectory(jobWorkDir);

        var title = !string.IsNullOrWhiteSpace(request.Title)
            ? request.Title.Trim()
            : DetermineDefaultJobTitle(items);
        if (title.Length > 140)
        {
            title = title[..140];
        }

        var qualitySummary = BuildQualitySummary(request, config);
        var container = NormalizeContainer(request.Container ?? config.DefaultContainer);
        var isZip = items.Count > 1;

        var safeTitle = SanitizeFileName(title);
        var qualityTag = BuildQualityFileTag(request);
        var outputFileName = isZip
            ? $"{safeTitle} [{qualityTag}].zip"
            : $"{FormatSingleVideoFileName(items[0], qualityTag)}.{(request.IsOriginalQuality ? GetOriginalExtension(items[0].Path) : container)}";

        var status = new DownloadJobStatusDto
        {
            JobId = jobId,
            UserId = userId,
            Title = title,
            QualitySummary = qualitySummary,
            State = DownloadJobState.Queued,
            ProgressPercent = 0,
            CompletedEpisodes = 0,
            TotalEpisodes = items.Count,
            CreatedAtUtc = DateTime.UtcNow,
            OutputFileName = outputFileName,
            IsZipArchive = isZip,
            DownloadToken = downloadToken,
            Episodes = items.Select(v => new EpisodeJobItemStatusDto
            {
                EpisodeId = v.Id,
                Label = FormatEpisodeLabel(v),
                ProgressPercent = 0,
                Status = "Queued"
            }).ToList()
        };

        var entry = new DownloadJobEntry(jobId, status, request, items, jobWorkDir);
        _jobs[jobId] = entry;

        _ = Task.Run(() => ExecuteJobAsync(entry));
        return status;
    }

    private static void ValidateAndNormalizeRequest(StartDownloadJobRequest req, PluginConfiguration config)
    {
        var vCodec = string.IsNullOrWhiteSpace(req.VideoCodec) ? config.DefaultVideoCodec : req.VideoCodec.Trim().ToLowerInvariant();
        if (!AllowedVideoCodecs.Contains(vCodec))
        {
            throw new ArgumentException($"Unsupported video encoder '{req.VideoCodec}'.");
        }

        req.VideoCodec = vCodec;

        var aCodec = string.IsNullOrWhiteSpace(req.AudioCodec) ? config.DefaultAudioCodec : req.AudioCodec.Trim().ToLowerInvariant();
        if (!AllowedAudioCodecs.Contains(aCodec))
        {
            throw new ArgumentException($"Unsupported audio encoder '{req.AudioCodec}'.");
        }

        req.AudioCodec = aCodec;

        var preset = string.IsNullOrWhiteSpace(req.Preset) ? config.DefaultPreset : req.Preset.Trim().ToLowerInvariant();
        if (!AllowedPresets.Contains(preset))
        {
            preset = "veryfast";
        }

        req.Preset = preset;
        req.Container = NormalizeContainer(req.Container ?? config.DefaultContainer);

        if (req.MaxWidth.HasValue)
        {
            req.MaxWidth = Math.Clamp(req.MaxWidth.Value, 128, 7680);
        }

        if (req.MaxHeight.HasValue)
        {
            req.MaxHeight = Math.Clamp(req.MaxHeight.Value, 128, 4320);
        }

        if (req.VideoBitrateKbps.HasValue && req.VideoBitrateKbps.Value > 0)
        {
            req.VideoBitrateKbps = Math.Clamp(req.VideoBitrateKbps.Value, 100, 200000);
        }
        else
        {
            req.VideoBitrateKbps = null;
        }

        if (req.AudioBitrateKbps.HasValue && req.AudioBitrateKbps.Value > 0)
        {
            req.AudioBitrateKbps = Math.Clamp(req.AudioBitrateKbps.Value, 32, 1536);
        }

        if (req.Crf.HasValue)
        {
            req.Crf = Math.Clamp(req.Crf.Value, 0, 51);
        }

        if (req.AudioChannels.HasValue)
        {
            req.AudioChannels = Math.Clamp(req.AudioChannels.Value, 1, 8);
        }

        if (!string.IsNullOrWhiteSpace(req.PreferredAudioLanguage) && !SafeLangRegex.IsMatch(req.PreferredAudioLanguage.Trim()))
        {
            req.PreferredAudioLanguage = "default";
        }

        if (!string.IsNullOrWhiteSpace(req.PreferredSubtitleLanguage) && !SafeLangRegex.IsMatch(req.PreferredSubtitleLanguage.Trim()))
        {
            req.PreferredSubtitleLanguage = "all";
        }

        req.SubtitleMode = string.Equals(req.SubtitleMode?.Trim(), "embed", StringComparison.OrdinalIgnoreCase) ? "embed" : "none";
    }

    /// <summary>
    /// Creates a short-lived cryptographic ticket for streaming an original quality ZIP archive.
    /// </summary>
    public string CreateStreamTicket(Guid userId, CreateStreamTicketRequest request)
    {
        if (request.EpisodeIds is null || request.EpisodeIds.Count == 0 || request.EpisodeIds.Count > 500)
        {
            throw new ArgumentException("Between 1 and 500 episodes must be selected.");
        }

        var ticketId = RandomNumberGenerator.GetHexString(64, lowercase: true);
        var safeTitle = SanitizeFileName(string.IsNullOrWhiteSpace(request.Title) ? "Series_Original" : request.Title);
        _streamTickets[ticketId] = new StreamTicketEntry(
            ticketId,
            userId,
            request.EpisodeIds.Distinct().ToList(),
            safeTitle,
            DateTime.UtcNow.AddMinutes(15));

        return ticketId;
    }

    /// <summary>
    /// Validates and retrieves a stream ticket.
    /// </summary>
    public (List<Guid> EpisodeIds, string SafeTitle)? TryGetStreamTicket(string ticketId)
    {
        if (string.IsNullOrWhiteSpace(ticketId) || !SafeHexIdRegex.IsMatch(ticketId))
        {
            return null;
        }

        if (!_streamTickets.TryGetValue(ticketId, out var ticket))
        {
            return null;
        }

        if (DateTime.UtcNow > ticket.ExpiresAtUtc)
        {
            _streamTickets.TryRemove(ticketId, out _);
            return null;
        }

        return (ticket.EpisodeIds, ticket.SafeTitle);
    }

    /// <summary>
    /// Gets the status of a specific job.
    /// </summary>
    public DownloadJobStatusDto? GetJobStatus(string jobId, Guid? userId = null)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !SafeHexIdRegex.IsMatch(jobId))
        {
            return null;
        }

        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            return null;
        }

        if (userId.HasValue && userId.Value != Guid.Empty && entry.Status.UserId != Guid.Empty && entry.Status.UserId != userId.Value)
        {
            return null;
        }

        return entry.Status;
    }

    /// <summary>
    /// Gets all jobs (filtered by user ID if provided).
    /// </summary>
    public List<DownloadJobStatusDto> GetJobs(Guid? userId = null)
    {
        return _jobs.Values
            .Where(e => !userId.HasValue || userId.Value == Guid.Empty || e.Status.UserId == userId.Value || e.Status.UserId == Guid.Empty)
            .Select(e => e.Status)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToList();
    }

    /// <summary>
    /// Gets the final file path and download filename for a completed job after verifying its cryptographic download token.
    /// </summary>
    public (string FilePath, string DownloadFileName, string ContentType)? GetCompletedJobFile(string jobId, string? downloadToken)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !SafeHexIdRegex.IsMatch(jobId) || string.IsNullOrWhiteSpace(downloadToken))
        {
            return null;
        }

        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            return null;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(entry.Status.DownloadToken);
        var actualBytes = Encoding.UTF8.GetBytes(downloadToken.Trim());
        if (expectedBytes.Length == 0 || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            return null;
        }

        if (entry.Status.State != DownloadJobState.Completed || string.IsNullOrEmpty(entry.FinalOutputPath) || !File.Exists(entry.FinalOutputPath))
        {
            return null;
        }

        var contentType = entry.Status.IsZipArchive
            ? "application/zip"
            : entry.Status.OutputFileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                ? "video/x-matroska"
                : "video/mp4";

        return (entry.FinalOutputPath, entry.Status.OutputFileName, contentType);
    }

    /// <summary>
    /// Cancels and optionally removes a job.
    /// </summary>
    public bool CancelOrDeleteJob(string jobId, bool removeRecord = false, Guid? userId = null)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !SafeHexIdRegex.IsMatch(jobId))
        {
            return false;
        }

        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            return false;
        }

        if (userId.HasValue && userId.Value != Guid.Empty && entry.Status.UserId != Guid.Empty && entry.Status.UserId != userId.Value)
        {
            return false;
        }

        try
        {
            entry.Cts.Cancel();
            entry.ActiveProcess?.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore process kill errors
        }

        if (entry.Status.State is DownloadJobState.Queued or DownloadJobState.Transcoding or DownloadJobState.Packaging)
        {
            entry.Status.State = DownloadJobState.Cancelled;
            entry.Status.CurrentEpisodeName = "Cancelled";
        }

        if (removeRecord)
        {
            _jobs.TryRemove(jobId, out _);
            TryDeleteDirectory(entry.WorkDir);
        }

        return true;
    }

    /// <summary>
    /// Streams original media files directly into a ZIP archive on the HTTP response stream without creating temp files on disk.
    /// </summary>
    public async Task StreamOriginalEpisodesZipAsync(
        IEnumerable<Guid> episodeIds,
        Stream responseStream,
        CancellationToken cancellationToken)
    {
        var items = episodeIds
            .Distinct()
            .Select(id => _libraryManager.GetItemById(id) as Video)
            .Where(v => v is not null && !string.IsNullOrEmpty(v.Path) && File.Exists(v.Path))
            .Cast<Video>()
            .OrderBy(v => (v as Episode)?.ParentIndexNumber ?? 1)
            .ThenBy(v => (v as Episode)?.IndexNumber ?? 1)
            .ToList();

        using var archive = new ZipArchive(responseStream, ZipArchiveMode.Create, leaveOpen: true);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var video in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ext = GetOriginalExtension(video.Path);
            var entryPath = BuildArchiveEntryPath(video, "Original", ext, usedNames);
            var zipEntry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);

            await using var entryStream = zipEntry.Open();
            await using var sourceStream = new FileStream(
                video.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1024 * 128,
                useAsync: true);

            await sourceStream.CopyToAsync(entryStream, 1024 * 128, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteJobAsync(DownloadJobEntry entry)
    {
        var ct = entry.Cts.Token;
        var status = entry.Status;
        var req = entry.Request;
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        try
        {
            // Fast path: Single file in original quality requires no transcoding or copying
            if (req.IsOriginalQuality && entry.Items.Count == 1)
            {
                var sourceVideo = entry.Items[0];
                entry.FinalOutputPath = sourceVideo.Path;
                var fi = new FileInfo(sourceVideo.Path);
                status.Episodes[0].ProgressPercent = 100;
                status.Episodes[0].Status = "Completed";
                status.CompletedEpisodes = 1;
                status.ProgressPercent = 100;
                status.OutputFileSizeBytes = fi.Exists ? fi.Length : null;
                status.CompletedAtUtc = DateTime.UtcNow;
                status.State = DownloadJobState.Completed;
                return;
            }

            await _concurrencyLimiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                status.State = DownloadJobState.Transcoding;
                var producedFiles = new List<(Video Item, string FilePath, string Extension)>();
                var container = NormalizeContainer(req.Container ?? config.DefaultContainer);
                var qualityTag = BuildQualityFileTag(req);

                for (var i = 0; i < entry.Items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var video = entry.Items[i];
                    var epStatus = status.Episodes[i];
                    epStatus.Status = req.IsOriginalQuality ? "Preparing" : "Transcoding";
                    status.CurrentEpisodeName = epStatus.Label;

                    if (req.IsOriginalQuality)
                    {
                        var ext = GetOriginalExtension(video.Path);
                        producedFiles.Add((video, video.Path, ext));
                        epStatus.ProgressPercent = 100;
                        epStatus.Status = "Completed";
                        status.CompletedEpisodes = i + 1;
                        UpdateOverallProgress(status, isPackagingPhase: false, packagingFraction: 0);
                    }
                    else
                    {
                        var outFileName = $"{i + 1:D3}_{SanitizeFileName(FormatSingleVideoFileName(video, qualityTag))}.{container}";
                        var outFilePath = Path.Combine(entry.WorkDir, outFileName);

                        await TranscodeSingleVideoAsync(entry, video, epStatus, outFilePath, container, config, ct).ConfigureAwait(false);

                        producedFiles.Add((video, outFilePath, container));
                        epStatus.ProgressPercent = 100;
                        epStatus.Status = "Completed";
                        status.CompletedEpisodes = i + 1;
                        UpdateOverallProgress(status, isPackagingPhase: false, packagingFraction: 0);
                    }
                }

                ct.ThrowIfCancellationRequested();

                if (producedFiles.Count == 1)
                {
                    entry.FinalOutputPath = producedFiles[0].FilePath;
                }
                else
                {
                    status.State = DownloadJobState.Packaging;
                    status.CurrentEpisodeName = "Packaging ZIP archive...";
                    var zipPath = Path.Combine(entry.WorkDir, SanitizeFileName(status.OutputFileName));
                    if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipPath += ".zip";
                    }

                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    await using (var zipFileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
                    using (var archive = new ZipArchive(zipFileStream, ZipArchiveMode.Create, leaveOpen: false))
                    {
                        for (var i = 0; i < producedFiles.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var (video, filePath, ext) = producedFiles[i];
                            var entryName = BuildArchiveEntryPath(video, qualityTag, ext, usedNames);

                            // Video files are already compressed; NoCompression packages multi-GB seasons in seconds with minimal CPU
                            var zipEntry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                            await using (var entryStream = zipEntry.Open())
                            await using (var srcStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 128, useAsync: true))
                            {
                                await srcStream.CopyToAsync(entryStream, 1024 * 128, ct).ConfigureAwait(false);
                            }

                            if (!req.IsOriginalQuality && filePath.StartsWith(entry.WorkDir, StringComparison.OrdinalIgnoreCase))
                            {
                                TryDeleteFile(filePath);
                            }

                            var packFrac = (double)(i + 1) / producedFiles.Count;
                            UpdateOverallProgress(status, isPackagingPhase: true, packagingFraction: packFrac);
                        }
                    }

                    entry.FinalOutputPath = zipPath;
                }

                var finalFi = new FileInfo(entry.FinalOutputPath);
                status.OutputFileSizeBytes = finalFi.Exists ? finalFi.Length : null;
                status.ProgressPercent = 100;
                status.CurrentEpisodeName = "Ready for download";
                status.CompletedAtUtc = DateTime.UtcNow;
                status.State = DownloadJobState.Completed;
                _logger.LogInformation("SeriesDownloader job {JobId} ({Title}) completed successfully: {Path}", entry.JobId, status.Title, entry.FinalOutputPath);
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
        }
        catch (OperationCanceledException)
        {
            status.State = DownloadJobState.Cancelled;
            status.CurrentEpisodeName = "Cancelled";
            TryDeleteDirectory(entry.WorkDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SeriesDownloader job {JobId} failed", entry.JobId);
            status.State = DownloadJobState.Failed;
            status.ErrorMessage = ex.Message;
            status.CurrentEpisodeName = "Failed";
            TryDeleteDirectory(entry.WorkDir);
        }
    }

    private async Task TranscodeSingleVideoAsync(
        DownloadJobEntry entry,
        Video video,
        EpisodeJobItemStatusDto epStatus,
        string outputFilePath,
        string container,
        PluginConfiguration config,
        CancellationToken ct)
    {
        var ffmpegPath = ResolveFfmpegPath();
        var (_, _, vaapiDevice) = DetectHardwareAcceleration();

        // Try full hardware decode + encode pipeline first if hardware decoding is enabled
        if (entry.Request.EnableHardwareDecoding)
        {
            var (hwPsi, hwDesc) = BuildFfmpegProcessStartInfo(
                ffmpegPath,
                video,
                outputFilePath,
                container,
                config,
                entry.Request,
                vaapiDevice,
                useHardwareDecode: true,
                forceSoftwareEncoderFallback: false);

            entry.Status.HardwareAccelInfo = hwDesc;

            var (exitCode, stderr) = await RunFfmpegProcessAsync(entry, video, epStatus, hwPsi, ct).ConfigureAwait(false);
            if (exitCode == 0)
            {
                return;
            }

            _logger.LogWarning(
                "Hardware decode pipeline ({HwDesc}) exited with code {Code} for {Video}; retrying with software decode fallback. Stderr: {Stderr}",
                hwDesc,
                exitCode,
                video.Name,
                stderr);
            TryDeleteFile(outputFilePath);
        }

        // Fallback 1: Software decode + configured encoder (with hwupload if VAAPI)
        var (swDecodePsi, swDecodeDesc) = BuildFfmpegProcessStartInfo(
            ffmpegPath,
            video,
            outputFilePath,
            container,
            config,
            entry.Request,
            vaapiDevice,
            useHardwareDecode: false,
            forceSoftwareEncoderFallback: false);

        entry.Status.HardwareAccelInfo = swDecodeDesc;

        var (fallbackCode, fallbackStderr) = await RunFfmpegProcessAsync(entry, video, epStatus, swDecodePsi, ct).ConfigureAwait(false);
        if (fallbackCode == 0)
        {
            return;
        }

        // Fallback 2: If a hardware encoder itself failed (e.g. GPU not passed to container), fall back to CPU libx264
        var vCodec = string.IsNullOrWhiteSpace(entry.Request.VideoCodec) ? config.DefaultVideoCodec : entry.Request.VideoCodec.Trim();
        if (!vCodec.Contains("x264", StringComparison.OrdinalIgnoreCase) && !vCodec.Contains("x265", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Hardware encoder {Codec} failed; falling back to libx264 CPU.", vCodec);
            TryDeleteFile(outputFilePath);

            var (cpuPsi, cpuDesc) = BuildFfmpegProcessStartInfo(
                ffmpegPath,
                video,
                outputFilePath,
                container,
                config,
                entry.Request,
                vaapiDevice,
                useHardwareDecode: false,
                forceSoftwareEncoderFallback: true);

            entry.Status.HardwareAccelInfo = cpuDesc;

            var (cpuCode, cpuStderr) = await RunFfmpegProcessAsync(entry, video, epStatus, cpuPsi, ct).ConfigureAwait(false);
            if (cpuCode == 0)
            {
                return;
            }

            throw new InvalidOperationException($"FFmpeg exited with code {cpuCode}: {cpuStderr}");
        }

        throw new InvalidOperationException($"FFmpeg exited with code {fallbackCode}: {fallbackStderr}");
    }

    private static (ProcessStartInfo Psi, string PipelineDescription) BuildFfmpegProcessStartInfo(
        string ffmpegPath,
        Video video,
        string outputFilePath,
        string container,
        PluginConfiguration config,
        StartDownloadJobRequest req,
        string vaapiDevice,
        bool useHardwareDecode,
        bool forceSoftwareEncoderFallback)
    {
        var streams = SafeGetMediaStreams(video);
        var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audioStreams = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        var subtitleStreams = streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();

        var srcW = videoStream?.Width ?? (video.Width > 0 ? video.Width : 1920);
        var srcH = videoStream?.Height ?? (video.Height > 0 ? video.Height : 1080);
        var (calcW, calcH) = ComputeTargetDimensions(srcW, srcH, req.MaxWidth, req.MaxHeight);

        var vCodec = forceSoftwareEncoderFallback
            ? "libx264"
            : (string.IsNullOrWhiteSpace(req.VideoCodec) ? config.DefaultVideoCodec : req.VideoCodec.Trim());

        var isNvenc = vCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
        var isVaapi = vCodec.Contains("vaapi", StringComparison.OrdinalIgnoreCase);
        var isQsv = vCodec.Contains("qsv", StringComparison.OrdinalIgnoreCase);
        var isAmf = vCodec.Contains("amf", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");

        var decodeDesc = "CPU Decode";

        // Hardware decode flags MUST appear before -i <input>
        if (useHardwareDecode)
        {
            if (isNvenc)
            {
                psi.ArgumentList.Add("-hwaccel");
                psi.ArgumentList.Add("cuda");
                decodeDesc = "GPU Decode (CUDA NVDEC)";
            }
            else if (isVaapi)
            {
                psi.ArgumentList.Add("-hwaccel");
                psi.ArgumentList.Add("vaapi");
                psi.ArgumentList.Add("-hwaccel_device");
                psi.ArgumentList.Add(vaapiDevice);
                psi.ArgumentList.Add("-hwaccel_output_format");
                psi.ArgumentList.Add("vaapi");
                decodeDesc = $"GPU Decode (VAAPI {vaapiDevice})";
            }
            else if (isQsv)
            {
                if (File.Exists(vaapiDevice))
                {
                    psi.ArgumentList.Add("-init_hw_device");
                    psi.ArgumentList.Add($"vaapi=va:{vaapiDevice}");
                    psi.ArgumentList.Add("-init_hw_device");
                    psi.ArgumentList.Add("qsv=qs@va");
                    psi.ArgumentList.Add("-filter_hw_device");
                    psi.ArgumentList.Add("qs");
                }

                psi.ArgumentList.Add("-hwaccel");
                psi.ArgumentList.Add("qsv");
                psi.ArgumentList.Add("-hwaccel_output_format");
                psi.ArgumentList.Add("qsv");

                var inCodec = videoStream?.Codec?.ToLowerInvariant() ?? string.Empty;
                var qsvDecoder = inCodec switch
                {
                    "h264" or "avc" => "h264_qsv",
                    "hevc" or "h265" => "hevc_qsv",
                    "vp9" => "vp9_qsv",
                    "av1" => "av1_qsv",
                    "mpeg2video" => "mpeg2_qsv",
                    "vc1" => "vc1_qsv",
                    _ => null
                };

                if (qsvDecoder is not null)
                {
                    psi.ArgumentList.Add("-c:v");
                    psi.ArgumentList.Add(qsvDecoder);
                }

                decodeDesc = $"GPU Decode ({qsvDecoder ?? "Intel QSV"})";
            }
            else
            {
                psi.ArgumentList.Add("-hwaccel");
                psi.ArgumentList.Add("auto");
                decodeDesc = "HW Decode (auto)";
            }
        }
        else if (isVaapi)
        {
            psi.ArgumentList.Add("-vaapi_device");
            psi.ArgumentList.Add(vaapiDevice);
        }
        else if (isQsv && File.Exists(vaapiDevice))
        {
            psi.ArgumentList.Add("-init_hw_device");
            psi.ArgumentList.Add($"vaapi=va:{vaapiDevice}");
            psi.ArgumentList.Add("-init_hw_device");
            psi.ArgumentList.Add("qsv=qs@va");
            psi.ArgumentList.Add("-filter_hw_device");
            psi.ArgumentList.Add("qs");
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(video.Path);

        // 1. Video mapping & encoding
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");

        var canCopyVideo = string.Equals(vCodec, "copy", StringComparison.OrdinalIgnoreCase)
            && !req.MaxWidth.HasValue
            && !req.MaxHeight.HasValue
            && !req.VideoBitrateKbps.HasValue
            && !req.Crf.HasValue
            && !string.Equals(req.SubtitleMode, "burn", StringComparison.OrdinalIgnoreCase);

        if (canCopyVideo)
        {
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("copy");
            decodeDesc = "Direct Stream Copy (No Re-encode)";
        }
        else
        {
            if (string.Equals(vCodec, "copy", StringComparison.OrdinalIgnoreCase))
            {
                vCodec = config.DefaultVideoCodec;
            }

            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add(vCodec);

            var targetW = req.MaxWidth ?? -2;
            var targetH = req.MaxHeight ?? -2;

            if (isQsv && useHardwareDecode)
            {
                // Keep surfaces in Intel GPU memory, scale and convert 10-bit/8-bit to nv12 via vpp_qsv
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add($"vpp_qsv=w={calcW}:h={calcH}:format=nv12");
            }
            else if (isQsv && !useHardwareDecode && File.Exists(vaapiDevice))
            {
                // Upload decoded frames to Intel QSV and scale on GPU via vpp_qsv
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add($"format=nv12,hwupload=extra_hw_frames=64,vpp_qsv=w={calcW}:h={calcH}:format=nv12");
            }
            else if (isVaapi && useHardwareDecode)
            {
                // Keep surfaces in GPU memory and scale via scale_vaapi
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add($"scale_vaapi=w={calcW}:h={calcH}:format=nv12");
            }
            else
            {
                var vfParts = new List<string>();
                if (req.MaxWidth.HasValue || req.MaxHeight.HasValue)
                {
                    if (targetW > 0 && targetH > 0)
                    {
                        vfParts.Add($"scale='min({targetW},iw)':'min({targetH},ih)':force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2");
                    }
                    else if (targetH > 0)
                    {
                        vfParts.Add($"scale=-2:'min({targetH},ih)'");
                    }
                    else if (targetW > 0)
                    {
                        vfParts.Add($"scale='min({targetW},iw)':-2");
                    }
                }
                else
                {
                    vfParts.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
                }

                if (isVaapi && !useHardwareDecode)
                {
                    vfParts.Add("format=nv12,hwupload");
                }

                if (vfParts.Count > 0)
                {
                    psi.ArgumentList.Add("-vf");
                    psi.ArgumentList.Add(string.Join(",", vfParts));
                }
            }

            var preset = string.IsNullOrWhiteSpace(req.Preset) ? config.DefaultPreset : req.Preset.Trim();
            if (vCodec.Contains("x264", StringComparison.OrdinalIgnoreCase) || vCodec.Contains("x265", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add(preset);
                psi.ArgumentList.Add("-pix_fmt");
                psi.ArgumentList.Add("yuv420p");
            }
            else if (isNvenc)
            {
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("p4");
                psi.ArgumentList.Add("-pix_fmt");
                psi.ArgumentList.Add("yuv420p");
            }
            else if (isQsv)
            {
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("veryfast");
            }

            if (req.VideoBitrateKbps.HasValue && req.VideoBitrateKbps.Value > 0)
            {
                var br = req.VideoBitrateKbps.Value;
                var maxRate = (int)(br * 1.4);
                var bufSize = br * 2;
                psi.ArgumentList.Add("-b:v");
                psi.ArgumentList.Add($"{br}k");
                psi.ArgumentList.Add("-maxrate");
                psi.ArgumentList.Add($"{maxRate}k");
                psi.ArgumentList.Add("-bufsize");
                psi.ArgumentList.Add($"{bufSize}k");
            }
            else
            {
                var crfVal = (req.Crf.HasValue && req.Crf.Value >= 0 && req.Crf.Value <= 51) ? req.Crf.Value : 22;
                if (isVaapi)
                {
                    psi.ArgumentList.Add("-qp");
                    psi.ArgumentList.Add(crfVal.ToString(CultureInfo.InvariantCulture));
                }
                else if (isNvenc)
                {
                    psi.ArgumentList.Add("-cq");
                    psi.ArgumentList.Add(crfVal.ToString(CultureInfo.InvariantCulture));
                }
                else if (isQsv)
                {
                    psi.ArgumentList.Add("-global_quality");
                    psi.ArgumentList.Add(crfVal.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    psi.ArgumentList.Add("-crf");
                    psi.ArgumentList.Add(crfVal.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        // 2. Audio mapping & encoding
        if (audioStreams.Count > 0)
        {
            var prefAudio = req.PreferredAudioLanguage?.Trim();
            if (string.Equals(prefAudio, "all", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a?");
            }
            else if (!string.IsNullOrEmpty(prefAudio) && !string.Equals(prefAudio, "default", StringComparison.OrdinalIgnoreCase))
            {
                var matchingAudio = audioStreams.FirstOrDefault(a =>
                    string.Equals(a.Language, prefAudio, StringComparison.OrdinalIgnoreCase) ||
                    (a.DisplayTitle?.Contains(prefAudio, StringComparison.OrdinalIgnoreCase) ?? false));

                if (matchingAudio is not null)
                {
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add($"0:{matchingAudio.Index}");
                }
                else
                {
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:a:0?");
                }
            }
            else
            {
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a:0?");
            }

            var aCodec = string.IsNullOrWhiteSpace(req.AudioCodec) ? config.DefaultAudioCodec : req.AudioCodec.Trim();
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add(aCodec);

            if (!string.Equals(aCodec, "copy", StringComparison.OrdinalIgnoreCase))
            {
                var aBitrate = req.AudioBitrateKbps.HasValue && req.AudioBitrateKbps.Value > 0
                    ? req.AudioBitrateKbps.Value
                    : 192;
                psi.ArgumentList.Add("-b:a");
                psi.ArgumentList.Add($"{aBitrate}k");

                var aChannels = req.AudioChannels ?? 2;
                if (aChannels > 0)
                {
                    psi.ArgumentList.Add("-ac");
                    psi.ArgumentList.Add(aChannels.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        // 3. Subtitle mapping (soft embed)
        if (string.Equals(req.SubtitleMode, "embed", StringComparison.OrdinalIgnoreCase) && subtitleStreams.Count > 0)
        {
            var isMkv = string.Equals(container, "mkv", StringComparison.OrdinalIgnoreCase);
            var eligibleSubs = isMkv
                ? subtitleStreams
                : subtitleStreams.Where(s => s.IsTextSubtitleStream).ToList();

            if (eligibleSubs.Count > 0)
            {
                var prefSub = req.PreferredSubtitleLanguage?.Trim();
                if (!string.IsNullOrEmpty(prefSub) && !string.Equals(prefSub, "all", StringComparison.OrdinalIgnoreCase))
                {
                    var matchSub = eligibleSubs.FirstOrDefault(s =>
                        string.Equals(s.Language, prefSub, StringComparison.OrdinalIgnoreCase) ||
                        (s.DisplayTitle?.Contains(prefSub, StringComparison.OrdinalIgnoreCase) ?? false));

                    if (matchSub is not null)
                    {
                        psi.ArgumentList.Add("-map");
                        psi.ArgumentList.Add($"0:{matchSub.Index}");
                    }
                }
                else
                {
                    foreach (var sub in eligibleSubs)
                    {
                        psi.ArgumentList.Add("-map");
                        psi.ArgumentList.Add($"0:{sub.Index}");
                    }
                }

                psi.ArgumentList.Add("-c:s");
                psi.ArgumentList.Add(isMkv ? "copy" : "mov_text");
            }
        }

        if (string.Equals(container, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
        }

        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add(outputFilePath);

        var encodeType = (isNvenc || isVaapi || isQsv || isAmf) ? $"GPU Encode ({vCodec})" : $"CPU Encode ({vCodec})";
        var fullDesc = canCopyVideo ? decodeDesc : $"{decodeDesc} → {encodeType}";
        return (psi, fullDesc);
    }

    private static (int Width, int Height) ComputeTargetDimensions(int srcW, int srcH, int? maxW, int? maxH)
    {
        if (srcW <= 0)
        {
            srcW = 1920;
        }

        if (srcH <= 0)
        {
            srcH = 1080;
        }

        double ratio = 1.0;
        if (maxW.HasValue && maxW.Value > 0 && maxH.HasValue && maxH.Value > 0)
        {
            ratio = Math.Min(1.0, Math.Min((double)maxW.Value / srcW, (double)maxH.Value / srcH));
        }
        else if (maxH.HasValue && maxH.Value > 0)
        {
            ratio = Math.Min(1.0, (double)maxH.Value / srcH);
        }
        else if (maxW.HasValue && maxW.Value > 0)
        {
            ratio = Math.Min(1.0, (double)maxW.Value / srcW);
        }

        var outW = Math.Max(2, ((int)Math.Round(srcW * ratio) / 2) * 2);
        var outH = Math.Max(2, ((int)Math.Round(srcH * ratio) / 2) * 2);
        return (outW, outH);
    }

    private static async Task<(int ExitCode, string Stderr)> RunFfmpegProcessAsync(
        DownloadJobEntry entry,
        Video video,
        EpisodeJobItemStatusDto epStatus,
        ProcessStartInfo psi,
        CancellationToken ct)
    {
        var totalDurationUs = (video.RunTimeTicks ?? 0) / 10.0;
        var stderrBuffer = new StringBuilder();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        entry.ActiveProcess = process;

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && stderrBuffer.Length < 16384)
            {
                stderrBuffer.AppendLine(e.Data);
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            if (e.Data.StartsWith("fps=", StringComparison.Ordinal))
            {
                var fpsStr = e.Data[4..].Trim();
                if (double.TryParse(fpsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var fpsVal))
                {
                    entry.Status.TranscodeFps = Math.Round(fpsVal, 1);
                }
            }
            else if (e.Data.StartsWith("speed=", StringComparison.Ordinal))
            {
                var speedStr = e.Data[6..].Trim();
                if (!string.IsNullOrWhiteSpace(speedStr) && !string.Equals(speedStr, "N/A", StringComparison.OrdinalIgnoreCase))
                {
                    entry.Status.TranscodeSpeed = speedStr;
                }
            }
            else if (totalDurationUs > 0 &&
                (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                 e.Data.StartsWith("out_time_ms=", StringComparison.Ordinal)))
            {
                var valStr = e.Data[(e.Data.IndexOf('=') + 1)..].Trim();
                if (long.TryParse(valStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentUs) && currentUs >= 0)
                {
                    var pct = Math.Clamp((currentUs / totalDurationUs) * 100.0, 0.0, 99.5);
                    epStatus.ProgressPercent = Math.Round(pct, 1);
                    UpdateOverallProgress(entry.Status, isPackagingPhase: false, packagingFraction: 0);
                }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start FFmpeg transcoding process.");
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            entry.ActiveProcess = null;
        }

        return (process.ExitCode, stderrBuffer.ToString());
    }

    private string ResolveFfmpegPath()
    {
        try
        {
            var prop = _mediaEncoder.GetType().GetProperty("EncoderPath")
                ?? typeof(IMediaEncoder).GetProperty("EncoderPath");
            var path = prop?.GetValue(_mediaEncoder) as string;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }
        catch
        {
            // Ignore reflection errors
        }

        if (File.Exists("/usr/lib/jellyfin-ffmpeg/ffmpeg"))
        {
            return "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        }

        return "ffmpeg";
    }

    private static IReadOnlyList<MediaStream> SafeGetMediaStreams(Video video)
    {
        try
        {
            var method = video.GetType().GetMethod("GetMediaStreams", Type.EmptyTypes)
                ?? typeof(BaseItem).GetMethod("GetMediaStreams", Type.EmptyTypes);
            if (method?.Invoke(video, null) is IEnumerable<MediaStream> streams)
            {
                return streams.ToList();
            }
        }
        catch
        {
            // Ignore stream lookup errors
        }

        return Array.Empty<MediaStream>();
    }

    private static void UpdateOverallProgress(DownloadJobStatusDto status, bool isPackagingPhase, double packagingFraction)
    {
        if (status.TotalEpisodes <= 0)
        {
            return;
        }

        var sumEpisodePct = status.Episodes.Sum(e => e.ProgressPercent);
        var transcodeRatio = (sumEpisodePct / (status.TotalEpisodes * 100.0));

        if (!status.IsZipArchive)
        {
            status.ProgressPercent = Math.Round(Math.Clamp(transcodeRatio * 100.0, 0.0, 99.9), 1);
        }
        else
        {
            var overall = isPackagingPhase
                ? 90.0 + (packagingFraction * 10.0)
                : transcodeRatio * 90.0;
            status.ProgressPercent = Math.Round(Math.Clamp(overall, 0.0, 99.9), 1);
        }
    }

    private static void PopulateSeasonsFromEpisodes(
        SeriesTreeDto tree,
        List<Episode> allEpisodes,
        Func<Episode, bool>? defaultSelectionPredicate,
        HashSet<string> audioLanguages,
        HashSet<string> subtitleLanguages)
    {
        var seasonGroups = allEpisodes
            .GroupBy(e => e.ParentIndexNumber ?? 0)
            .OrderBy(g => g.Key == 0 ? 9999 : g.Key);

        foreach (var group in seasonGroups)
        {
            var firstEp = group.First();
            string? resolvedSeasonName = firstEp.SeasonName;
            if (string.IsNullOrWhiteSpace(resolvedSeasonName))
            {
                try
                {
                    resolvedSeasonName = firstEp.Season?.Name;
                }
                catch
                {
                    // Ignore season lookup errors
                }
            }

            var seasonName = !string.IsNullOrWhiteSpace(resolvedSeasonName)
                ? resolvedSeasonName
                : (group.Key == 0 ? "Specials" : $"Season {group.Key}");

            var seasonNode = new SeasonNodeDto
            {
                SeasonId = firstEp.SeasonId != Guid.Empty ? firstEp.SeasonId : firstEp.Id,
                SeasonNumber = group.Key,
                SeasonName = seasonName
            };

            foreach (var ep in group)
            {
                var selected = defaultSelectionPredicate?.Invoke(ep) ?? true;
                var epNode = CreateEpisodeNode(
                    ep,
                    ep.ParentIndexNumber ?? group.Key,
                    ep.IndexNumber,
                    selected,
                    audioLanguages,
                    subtitleLanguages);
                seasonNode.Episodes.Add(epNode);
            }

            if (seasonNode.Episodes.Count > 0)
            {
                tree.Seasons.Add(seasonNode);
            }
        }
    }

    private static EpisodeNodeDto CreateEpisodeNode(
        Video video,
        int? seasonNumber,
        int? episodeNumber,
        bool selectedByDefault,
        HashSet<string> audioLanguages,
        HashSet<string> subtitleLanguages)
    {
        var streams = SafeGetMediaStreams(video);
        var vStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

        foreach (var aStream in streams.Where(s => s.Type == MediaStreamType.Audio))
        {
            if (!string.IsNullOrWhiteSpace(aStream.Language))
            {
                audioLanguages.Add(aStream.Language);
            }
        }

        foreach (var sStream in streams.Where(s => s.Type == MediaStreamType.Subtitle))
        {
            if (!string.IsNullOrWhiteSpace(sStream.Language))
            {
                subtitleLanguages.Add(sStream.Language);
            }
        }

        long? fileSize = null;
        if (!string.IsNullOrEmpty(video.Path))
        {
            try
            {
                var fi = new FileInfo(video.Path);
                if (fi.Exists)
                {
                    fileSize = fi.Length;
                }
            }
            catch
            {
                // Ignore file access errors
            }
        }

        return new EpisodeNodeDto
        {
            EpisodeId = video.Id,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            Name = video.Name ?? "Episode",
            RunTimeTicks = video.RunTimeTicks,
            FileSizeBytes = fileSize,
            Width = vStream?.Width ?? video.Width,
            Height = vStream?.Height ?? video.Height,
            VideoCodec = vStream?.Codec ?? string.Empty,
            Container = GetOriginalExtension(video.Path),
            SelectedByDefault = selectedByDefault
        };
    }

    private static string DetermineDefaultJobTitle(List<Video> items)
    {
        if (items.Count == 1)
        {
            if (items[0] is Episode singleEp)
            {
                var sName = singleEp.SeriesName ?? TryGetSeries(singleEp)?.Name ?? "Series";
                var sNum = singleEp.ParentIndexNumber ?? 1;
                var eNum = singleEp.IndexNumber ?? 1;
                return $"{sName} - S{sNum:D2}E{eNum:D2} - {singleEp.Name}";
            }

            return items[0].Name ?? "Video";
        }

        var episodes = items.OfType<Episode>().ToList();
        if (episodes.Count > 0)
        {
            var seriesName = episodes[0].SeriesName ?? TryGetSeries(episodes[0])?.Name ?? "Series";
            var distinctSeasons = episodes.Select(e => e.ParentIndexNumber ?? 1).Distinct().ToList();
            if (distinctSeasons.Count == 1)
            {
                return $"{seriesName} - Season {distinctSeasons[0]:D2}";
            }

            return $"{seriesName} ({episodes.Count} Episodes)";
        }

        return $"Media Pack ({items.Count} Files)";
    }

    private static string FormatEpisodeLabel(Video video)
    {
        if (video is Episode ep)
        {
            var s = ep.ParentIndexNumber ?? 1;
            var e = ep.IndexNumber ?? 1;
            return $"S{s:D2}E{e:D2} - {ep.Name}";
        }

        return video.Name ?? "Video";
    }

    private static string FormatSingleVideoFileName(Video video, string qualityTag)
    {
        if (video is Episode ep)
        {
            var seriesName = ep.SeriesName ?? TryGetSeries(ep)?.Name ?? "Series";
            var s = ep.ParentIndexNumber ?? 1;
            var e = ep.IndexNumber ?? 1;
            return SanitizeFileName($"{seriesName} - S{s:D2}E{e:D2} - {ep.Name} [{qualityTag}]");
        }

        return SanitizeFileName($"{video.Name} [{qualityTag}]");
    }

    private static string BuildArchiveEntryPath(Video video, string qualityTag, string extension, HashSet<string> usedNames)
    {
        var folder = "Episodes";
        if (video is Episode ep)
        {
            var s = ep.ParentIndexNumber ?? 1;
            folder = s == 0 ? "Specials" : $"Season {s:D2}";
        }

        var baseName = FormatSingleVideoFileName(video, qualityTag);
        var relPath = $"{folder}/{baseName}.{extension}";
        var counter = 2;
        while (!usedNames.Add(relPath))
        {
            relPath = $"{folder}/{baseName} ({counter}).{extension}";
            counter++;
        }

        return relPath;
    }

    private static string BuildQualitySummary(StartDownloadJobRequest req, PluginConfiguration config)
    {
        if (req.IsOriginalQuality)
        {
            return "Original Quality (Direct Stream / Copy)";
        }

        var res = req.MaxHeight.HasValue
            ? $"{req.MaxHeight.Value}p"
            : req.MaxWidth.HasValue
                ? $"{req.MaxWidth.Value}w"
                : "Original Res";

        var rate = req.VideoBitrateKbps.HasValue && req.VideoBitrateKbps.Value > 0
            ? req.VideoBitrateKbps.Value >= 1000
                ? $"{(req.VideoBitrateKbps.Value / 1000.0):0.#} Mbps"
                : $"{req.VideoBitrateKbps.Value} kbps"
            : req.Crf.HasValue
                ? $"CRF {req.Crf.Value}"
                : "CRF 22";

        var vCodec = string.IsNullOrWhiteSpace(req.VideoCodec) ? config.DefaultVideoCodec : req.VideoCodec;
        var container = NormalizeContainer(req.Container ?? config.DefaultContainer).ToUpperInvariant();
        return $"{res} ({rate}, {vCodec}, {container})";
    }

    private static string BuildQualityFileTag(StartDownloadJobRequest req)
    {
        if (req.IsOriginalQuality)
        {
            return "Original";
        }

        var parts = new List<string>();
        if (req.MaxHeight.HasValue)
        {
            parts.Add($"{req.MaxHeight.Value}p");
        }
        else if (req.MaxWidth.HasValue)
        {
            parts.Add($"{req.MaxWidth.Value}w");
        }

        if (req.VideoBitrateKbps.HasValue && req.VideoBitrateKbps.Value > 0)
        {
            parts.Add(req.VideoBitrateKbps.Value >= 1000
                ? $"{(req.VideoBitrateKbps.Value / 1000.0):0.#}Mbps"
                : $"{req.VideoBitrateKbps.Value}kbps");
        }
        else if (req.Crf.HasValue)
        {
            parts.Add($"CRF{req.Crf.Value}");
        }

        return parts.Count > 0 ? string.Join("-", parts) : "Transcoded";
    }

    private static string NormalizeContainer(string? container)
    {
        if (string.Equals(container?.Trim().TrimStart('.'), "mkv", StringComparison.OrdinalIgnoreCase))
        {
            return "mkv";
        }

        return "mp4";
    }

    private static string GetOriginalExtension(string? filePath)
    {
        var ext = Path.GetExtension(filePath)?.TrimStart('.');
        return string.IsNullOrWhiteSpace(ext) ? "mkv" : ext.ToLowerInvariant();
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Download";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsControl(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || invalid.Contains(ch))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var cleaned = sb.ToString().Replace("..", "_", StringComparison.Ordinal).Trim(' ', '.', '_');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "Download";
        }

        return cleaned.Length > 140 ? cleaned[..140] : cleaned;
    }

    private string GetBaseTempDirectory(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.TempTranscodeDirectory))
        {
            return config.TempTranscodeDirectory;
        }

        return Path.Combine(_appPaths.TempDirectory, "SeriesDownloader");
    }

    private void UpdateConcurrencyIfNeeded(int desired)
    {
        if (desired == _configuredConcurrency)
        {
            return;
        }

        _configuredConcurrency = desired;
        _concurrencyLimiter = new SemaphoreSlim(desired, desired);
    }

    private void CleanupExpiredJobs(object? state)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var retention = TimeSpan.FromMinutes(Math.Max(5, config.JobRetentionMinutes));
        var cutoff = DateTime.UtcNow - retention;
        var now = DateTime.UtcNow;

        foreach (var t in _streamTickets)
        {
            if (now > t.Value.ExpiresAtUtc)
            {
                _streamTickets.TryRemove(t.Key, out _);
            }
        }

        foreach (var kvp in _jobs)
        {
            var s = kvp.Value.Status;
            if (s.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled)
            {
                var finishedTime = s.CompletedAtUtc ?? s.CreatedAtUtc;
                if (finishedTime < cutoff)
                {
                    if (_jobs.TryRemove(kvp.Key, out var removed))
                    {
                        TryDeleteDirectory(removed.WorkDir);
                    }
                }
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _concurrencyLimiter.Dispose();
    }

    private sealed record StreamTicketEntry(
        string TicketId,
        Guid UserId,
        List<Guid> EpisodeIds,
        string SafeTitle,
        DateTime ExpiresAtUtc);

    private sealed class DownloadJobEntry
    {
        public DownloadJobEntry(
            string jobId,
            DownloadJobStatusDto status,
            StartDownloadJobRequest request,
            List<Video> items,
            string workDir)
        {
            JobId = jobId;
            Status = status;
            Request = request;
            Items = items;
            WorkDir = workDir;
        }

        public string JobId { get; }

        public DownloadJobStatusDto Status { get; }

        public StartDownloadJobRequest Request { get; }

        public List<Video> Items { get; }

        public string WorkDir { get; }

        public string? FinalOutputPath { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        public Process? ActiveProcess { get; set; }
    }
}
