using System.Globalization;
using Jellyfin.Plugin.SeriesDownloader.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SeriesDownloader;

/// <summary>
/// Main plugin entry point for Jellyfin Series Downloader.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Series Quality Downloader";

    /// <inheritdoc />
    public override string Description => "Download entire TV series, seasons, or individual episodes in any resolution, bitrate, or codec (packaged as a .zip archive or single video file).";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b8e57364-9a4d-4d87-8c4a-39f1e981d7a2");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the application paths.
    /// </summary>
    public IApplicationPaths AppPaths => ApplicationPaths;

    /// <summary>
    /// Gets the plugin thumbnail image stream.
    /// </summary>
    /// <returns>Stream containing the PNG plugin icon.</returns>
    public Stream? GetThumbImage()
    {
        var type = GetType();
        return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
    }

    /// <summary>
    /// Gets the plugin thumbnail image format.
    /// </summary>
    public ImageFormat ThumbImageFormat => ImageFormat.Png;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }
}
