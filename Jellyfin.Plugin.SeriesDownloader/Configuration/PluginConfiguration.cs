using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeriesDownloader.Configuration;

/// <summary>
/// Configuration settings for the Series Downloader plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        InjectClientScript = true;
        TempTranscodeDirectory = string.Empty;
        MaxConcurrentTranscodes = 1;
        DefaultVideoCodec = "libx264";
        DefaultAudioCodec = "aac";
        DefaultContainer = "mp4";
        DefaultPreset = "veryfast";
        JobRetentionMinutes = 120;
        AllowAnyAuthenticatedUser = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should automatically inject its client script tag into Jellyfin Web's index.html.
    /// </summary>
    public bool InjectClientScript { get; set; }

    /// <summary>
    /// Gets or sets a custom directory for temporary transcoded files. Leave empty to use Jellyfin's default temp directory.
    /// </summary>
    public string TempTranscodeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent FFmpeg transcode processes.
    /// </summary>
    public int MaxConcurrentTranscodes { get; set; }

    /// <summary>
    /// Gets or sets the default FFmpeg video codec (e.g. libx264, libx265, h264_nvenc, h264_qsv, h264_vaapi).
    /// </summary>
    public string DefaultVideoCodec { get; set; }

    /// <summary>
    /// Gets or sets the default FFmpeg audio codec (e.g. aac, libmp3lame, libopus, copy).
    /// </summary>
    public string DefaultAudioCodec { get; set; }

    /// <summary>
    /// Gets or sets the default output container format (mp4 or mkv).
    /// </summary>
    public string DefaultContainer { get; set; }

    /// <summary>
    /// Gets or sets the FFmpeg encoding preset (ultrafast, superfast, veryfast, faster, fast, medium).
    /// </summary>
    public string DefaultPreset { get; set; }

    /// <summary>
    /// Gets or sets how many minutes completed download jobs are kept before automatic cleanup.
    /// </summary>
    public int JobRetentionMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether any authenticated user can use the downloader (if false, checks user's EnableContentDownloading policy).
    /// </summary>
    public bool AllowAnyAuthenticatedUser { get; set; }
}
