using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SeriesDownloader.Models;

/// <summary>
/// Represents the hierarchy of a series, season, or episode for the download modal.
/// </summary>
public class SeriesTreeDto
{
    /// <summary>
    /// Gets or sets the requested root item ID.
    /// </summary>
    [JsonPropertyName("ItemId")]
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item type (Series, Season, Episode, Movie).
    /// </summary>
    [JsonPropertyName("ItemType")]
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    [JsonPropertyName("SeriesName")]
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display title of the root item.
    /// </summary>
    [JsonPropertyName("Title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the seasons belonging to the series.
    /// </summary>
    [JsonPropertyName("Seasons")]
    public List<SeasonNodeDto> Seasons { get; set; } = new();

    /// <summary>
    /// Gets or sets the distinct audio languages available across episodes.
    /// </summary>
    [JsonPropertyName("AvailableAudioLanguages")]
    public List<string> AvailableAudioLanguages { get; set; } = new();

    /// <summary>
    /// Gets or sets the distinct subtitle languages available across episodes.
    /// </summary>
    [JsonPropertyName("AvailableSubtitleLanguages")]
    public List<string> AvailableSubtitleLanguages { get; set; } = new();

    /// <summary>
    /// Gets or sets the auto-detected default video encoder from Jellyfin's hardware acceleration config.
    /// </summary>
    [JsonPropertyName("DetectedVideoEncoder")]
    public string DetectedVideoEncoder { get; set; } = "libx264";

    /// <summary>
    /// Gets or sets a summary of the server's detected hardware acceleration state.
    /// </summary>
    [JsonPropertyName("DetectedHardwareSummary")]
    public string DetectedHardwareSummary { get; set; } = "CPU Software Encoding";
}

/// <summary>
/// Represents a season and its episodes.
/// </summary>
public class SeasonNodeDto
{
    /// <summary>
    /// Gets or sets the season item ID.
    /// </summary>
    [JsonPropertyName("SeasonId")]
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    [JsonPropertyName("SeasonNumber")]
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the season name.
    /// </summary>
    [JsonPropertyName("SeasonName")]
    public string SeasonName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the episodes in this season.
    /// </summary>
    [JsonPropertyName("Episodes")]
    public List<EpisodeNodeDto> Episodes { get; set; } = new();
}

/// <summary>
/// Represents a single episode and its source media details.
/// </summary>
public class EpisodeNodeDto
{
    /// <summary>
    /// Gets or sets the episode item ID.
    /// </summary>
    [JsonPropertyName("EpisodeId")]
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    [JsonPropertyName("SeasonNumber")]
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    [JsonPropertyName("EpisodeNumber")]
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode title.
    /// </summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the runtime in ticks.
    /// </summary>
    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the original file size in bytes.
    /// </summary>
    [JsonPropertyName("FileSizeBytes")]
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the source video width.
    /// </summary>
    [JsonPropertyName("Width")]
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the source video height.
    /// </summary>
    [JsonPropertyName("Height")]
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets the source video codec.
    /// </summary>
    [JsonPropertyName("VideoCodec")]
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source container extension.
    /// </summary>
    [JsonPropertyName("Container")]
    public string Container { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this episode should be initially selected in the UI.
    /// </summary>
    [JsonPropertyName("SelectedByDefault")]
    public bool SelectedByDefault { get; set; } = true;
}

/// <summary>
/// Request payload to start a new transcode/download job.
/// </summary>
public class StartDownloadJobRequest
{
    /// <summary>
    /// Gets or sets the list of episode (or video) IDs to include in the download.
    /// </summary>
    [JsonPropertyName("EpisodeIds")]
    public List<Guid> EpisodeIds { get; set; } = new();

    /// <summary>
    /// Gets or sets a friendly title for the archive or download job.
    /// </summary>
    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to keep the original files without transcoding.
    /// </summary>
    [JsonPropertyName("IsOriginalQuality")]
    public bool IsOriginalQuality { get; set; }

    /// <summary>
    /// Gets or sets the target maximum video width (e.g. 3840, 1920, 1280, 854, 640). Null keeps original resolution.
    /// </summary>
    [JsonPropertyName("MaxWidth")]
    public int? MaxWidth { get; set; }

    /// <summary>
    /// Gets or sets the target maximum video height (e.g. 2160, 1080, 720, 480, 360). Null keeps original resolution.
    /// </summary>
    [JsonPropertyName("MaxHeight")]
    public int? MaxHeight { get; set; }

    /// <summary>
    /// Gets or sets the target video bitrate in kbps (e.g. 8000 for 8 Mbps). Optional if CRF is used.
    /// </summary>
    [JsonPropertyName("VideoBitrateKbps")]
    public int? VideoBitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets the Constant Rate Factor (CRF, 0-51) for quality-targeted encoding when bitrate is not fixed.
    /// </summary>
    [JsonPropertyName("Crf")]
    public int? Crf { get; set; }

    /// <summary>
    /// Gets or sets the target audio bitrate in kbps (e.g. 192, 128, 320).
    /// </summary>
    [JsonPropertyName("AudioBitrateKbps")]
    public int? AudioBitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets the target audio channel count (e.g. 2 for stereo, 6 for 5.1, or null for original).
    /// </summary>
    [JsonPropertyName("AudioChannels")]
    public int? AudioChannels { get; set; }

    /// <summary>
    /// Gets or sets the target video codec (libx264, libx265, h264_nvenc, hevc_nvenc, h264_qsv, hevc_qsv, h264_vaapi, hevc_vaapi, copy).
    /// </summary>
    [JsonPropertyName("VideoCodec")]
    public string? VideoCodec { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether hardware decoding (-hwaccel) should be used when available.
    /// </summary>
    [JsonPropertyName("EnableHardwareDecoding")]
    public bool EnableHardwareDecoding { get; set; } = true;

    /// <summary>
    /// Gets or sets the target audio codec (aac, libmp3lame, libopus, ac3, copy).
    /// </summary>
    [JsonPropertyName("AudioCodec")]
    public string? AudioCodec { get; set; }

    /// <summary>
    /// Gets or sets the target container format (mp4 or mkv).
    /// </summary>
    [JsonPropertyName("Container")]
    public string? Container { get; set; }

    /// <summary>
    /// Gets or sets the FFmpeg encoder preset (ultrafast, superfast, veryfast, faster, fast, medium).
    /// </summary>
    [JsonPropertyName("Preset")]
    public string? Preset { get; set; }

    /// <summary>
    /// Gets or sets the preferred audio language code (e.g. eng, jpn, all, or default).
    /// </summary>
    [JsonPropertyName("PreferredAudioLanguage")]
    public string? PreferredAudioLanguage { get; set; }

    /// <summary>
    /// Gets or sets the subtitle handling mode: "none", "embed" (soft subs), or "burn" (hardcoded).
    /// </summary>
    [JsonPropertyName("SubtitleMode")]
    public string? SubtitleMode { get; set; }

    /// <summary>
    /// Gets or sets the preferred subtitle language code (e.g. eng, all).
    /// </summary>
    [JsonPropertyName("PreferredSubtitleLanguage")]
    public string? PreferredSubtitleLanguage { get; set; }
}

/// <summary>
/// Enumeration of download job states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadJobState
{
    /// <summary>Job is waiting in queue.</summary>
    Queued,

    /// <summary>Job is actively transcoding episodes.</summary>
    Transcoding,

    /// <summary>Job is packaging files into a ZIP archive.</summary>
    Packaging,

    /// <summary>Job has finished and the file is ready for download.</summary>
    Completed,

    /// <summary>Job failed with an error.</summary>
    Failed,

    /// <summary>Job was cancelled by a user.</summary>
    Cancelled
}

/// <summary>
/// Represents the status of an active or completed download job.
/// </summary>
public class DownloadJobStatusDto
{
    /// <summary>
    /// Gets or sets the unique job ID.
    /// </summary>
    [JsonPropertyName("JobId")]
    public string JobId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user ID who initiated the job.
    /// </summary>
    [JsonPropertyName("UserId")]
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the display title of the job.
    /// </summary>
    [JsonPropertyName("Title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable quality summary (e.g., "1080p (8 Mbps, H.264/MP4)").
    /// </summary>
    [JsonPropertyName("QualitySummary")]
    public string QualitySummary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current job state.
    /// </summary>
    [JsonPropertyName("State")]
    public DownloadJobState State { get; set; }

    /// <summary>
    /// Gets or sets the overall progress percentage (0 to 100).
    /// </summary>
    [JsonPropertyName("ProgressPercent")]
    public double ProgressPercent { get; set; }

    /// <summary>
    /// Gets or sets the name of the episode currently being processed.
    /// </summary>
    [JsonPropertyName("CurrentEpisodeName")]
    public string CurrentEpisodeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of completed episodes.
    /// </summary>
    [JsonPropertyName("CompletedEpisodes")]
    public int CompletedEpisodes { get; set; }

    /// <summary>
    /// Gets or sets the total number of episodes in the job.
    /// </summary>
    [JsonPropertyName("TotalEpisodes")]
    public int TotalEpisodes { get; set; }

    /// <summary>
    /// Gets or sets the UTC creation timestamp.
    /// </summary>
    [JsonPropertyName("CreatedAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the UTC completion timestamp.
    /// </summary>
    [JsonPropertyName("CompletedAtUtc")]
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the error message if the job failed.
    /// </summary>
    [JsonPropertyName("ErrorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the final output file name (.zip, .mp4, .mkv).
    /// </summary>
    [JsonPropertyName("OutputFileName")]
    public string OutputFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the final output file size in bytes once completed.
    /// </summary>
    [JsonPropertyName("OutputFileSizeBytes")]
    public long? OutputFileSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the output is a ZIP archive.
    /// </summary>
    [JsonPropertyName("IsZipArchive")]
    public bool IsZipArchive { get; set; }

    /// <summary>
    /// Gets or sets per-episode progress items.
    /// </summary>
    [JsonPropertyName("Episodes")]
    public List<EpisodeJobItemStatusDto> Episodes { get; set; } = new();

    /// <summary>
    /// Gets or sets the live FFmpeg encoding speed multiplier (e.g. "5.4x").
    /// </summary>
    [JsonPropertyName("TranscodeSpeed")]
    public string TranscodeSpeed { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the live FFmpeg encoding frames per second.
    /// </summary>
    [JsonPropertyName("TranscodeFps")]
    public double TranscodeFps { get; set; }

    /// <summary>
    /// Gets or sets a summary of the active decoder and encoder pipeline (e.g. "HW Decode (cuda) + HW Encode (h264_nvenc)").
    /// </summary>
    [JsonPropertyName("HardwareAccelInfo")]
    public string HardwareAccelInfo { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the active FFmpeg command line for diagnostics.
    /// </summary>
    [JsonPropertyName("FfmpegCommand")]
    public string FfmpegCommand { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cryptographic token required to download the finished file.
    /// </summary>
    [JsonPropertyName("DownloadToken")]
    public string DownloadToken { get; set; } = string.Empty;
}

/// <summary>
/// Request payload to create an authenticated one-time ticket for streaming an original quality ZIP archive.
/// </summary>
public class CreateStreamTicketRequest
{
    /// <summary>
    /// Gets or sets the episode IDs to include in the streamed ZIP.
    /// </summary>
    [JsonPropertyName("EpisodeIds")]
    public List<Guid> EpisodeIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the archive base title.
    /// </summary>
    [JsonPropertyName("Title")]
    public string? Title { get; set; }
}

/// <summary>
/// Represents the status of a single episode within a download job.
/// </summary>
public class EpisodeJobItemStatusDto
{
    /// <summary>
    /// Gets or sets the episode ID.
    /// </summary>
    [JsonPropertyName("EpisodeId")]
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the formatted episode label (e.g., "S01E01 - Pilot").
    /// </summary>
    [JsonPropertyName("Label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the progress percentage (0 to 100) for this episode.
    /// </summary>
    [JsonPropertyName("ProgressPercent")]
    public double ProgressPercent { get; set; }

    /// <summary>
    /// Gets or sets the status string (Queued, Transcoding, Done, Failed).
    /// </summary>
    [JsonPropertyName("Status")]
    public string Status { get; set; } = "Queued";
}
