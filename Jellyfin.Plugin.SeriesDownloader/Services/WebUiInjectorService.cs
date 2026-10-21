using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesDownloader.Services;

/// <summary>
/// Manages Web UI script injection both via disk (when writable) and via in-memory HTTP middleware (for read-only Docker containers).
/// </summary>
public sealed class WebUiInjectorService : IHostedService
{
    private const string ScriptMarker = "SeriesDownloader/ClientScript";
    private const string ScriptTag = "<script plugin=\"SeriesDownloader\" src=\"../SeriesDownloader/ClientScript\" defer></script>";

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<WebUiInjectorService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebUiInjectorService"/> class.
    /// </summary>
    public WebUiInjectorService(IApplicationPaths appPaths, ILogger<WebUiInjectorService> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var shouldInject = Plugin.Instance?.Configuration.InjectClientScript ?? true;
        ApplyInjectionState(shouldInject, updateConfig: false);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Applies or removes the client script injection (both in-memory middleware and on-disk index.html if writable).
    /// </summary>
    /// <param name="inject">True to enable injection; false to disable it.</param>
    /// <param name="updateConfig">True to persist the setting in PluginConfiguration.</param>
    /// <returns>A tuple indicating success and a human-readable status message.</returns>
    public (bool Success, string Message) ApplyInjectionState(bool inject, bool updateConfig = true)
    {
        if (updateConfig && Plugin.Instance is not null)
        {
            Plugin.Instance.Configuration.InjectClientScript = inject;
            Plugin.Instance.SaveConfiguration();
        }

        var indexPath = Path.Combine(_appPaths.WebPath ?? string.Empty, "index.html");
        if (string.IsNullOrWhiteSpace(_appPaths.WebPath) || !File.Exists(indexPath))
        {
            return (true, inject
                ? "In-memory Web UI injection enabled."
                : "Web UI injection disabled.");
        }

        try
        {
            var html = File.ReadAllText(indexPath);
            var alreadyPresent = html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase);

            if (inject)
            {
                if (!alreadyPresent)
                {
                    var closeBodyIdx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (closeBodyIdx < 0)
                    {
                        html += Environment.NewLine + ScriptTag;
                    }
                    else
                    {
                        html = html.Insert(closeBodyIdx, ScriptTag + Environment.NewLine);
                    }

                    File.WriteAllText(indexPath, html);
                }

                _logger.LogInformation("SeriesDownloader client script active in {IndexPath}", indexPath);
                return (true, $"Web UI button injection is ACTIVE ({indexPath}). Hard-refresh your browser (Ctrl+F5) to load it.");
            }
            else
            {
                if (alreadyPresent)
                {
                    var updated = html.Replace(ScriptTag + Environment.NewLine, string.Empty, StringComparison.OrdinalIgnoreCase)
                                      .Replace(ScriptTag, string.Empty, StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(indexPath, updated);
                }

                return (true, "Disabled Web UI button injection.");
            }
        }
        catch (Exception ex)
        {
            // Expected in Docker where /jellyfin/jellyfin-web/index.html is owned by root and read-only.
            // Our IndexHtmlInjectionMiddleware serves the injected index.html dynamically from memory!
            _logger.LogInformation(
                "index.html at {IndexPath} is read-only ({Error}); using in-memory IndexHtmlInjectionMiddleware instead.",
                indexPath,
                ex.Message);

            return (true, inject
                ? "In-Memory Web UI Injection is ACTIVE (Read-Only Docker Mode). Hard-refresh your browser (Ctrl+F5) so Jellyfin loads the injected index.html from memory!"
                : "Disabled in-memory Web UI button injection.");
        }
    }

    /// <summary>
    /// Checks whether the script injection is active (either on disk or via in-memory middleware).
    /// </summary>
    public (bool IsInjected, string Mode, string IndexPath) GetInjectionStatus()
    {
        var indexPath = Path.Combine(_appPaths.WebPath ?? string.Empty, "index.html");
        var middlewareEnabled = Plugin.Instance?.Configuration.InjectClientScript ?? true;
        var onDisk = false;

        if (!string.IsNullOrEmpty(indexPath) && File.Exists(indexPath))
        {
            try
            {
                var html = File.ReadAllText(indexPath);
                onDisk = html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Ignore read errors
            }
        }

        var isInjected = onDisk || middlewareEnabled;
        var mode = onDisk
            ? "Disk + In-Memory Middleware"
            : middlewareEnabled
                ? "In-Memory Middleware (Docker Read-Only Mode)"
                : "Disabled";

        return (isInjected, mode, indexPath);
    }
}
