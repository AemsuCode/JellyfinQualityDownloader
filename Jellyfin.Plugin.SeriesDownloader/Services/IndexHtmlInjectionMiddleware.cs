using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesDownloader.Services;

/// <summary>
/// Registers <see cref="IndexHtmlInjectionMiddleware"/> at the start of the ASP.NET Core HTTP pipeline
/// so that read-only Docker containers (where /jellyfin/jellyfin-web/index.html is not writable)
/// still receive the injected Series Downloader client script dynamically in-memory.
/// </summary>
public sealed class WebUiInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<IndexHtmlInjectionMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Intercepts requests for Jellyfin Web's index.html and injects the Series Downloader client script tag in-memory.
/// </summary>
public sealed class IndexHtmlInjectionMiddleware
{
    private const string ScriptMarker = "SeriesDownloader/ClientScript";
    private const string ScriptTag = "<script plugin=\"SeriesDownloader\" src=\"../SeriesDownloader/ClientScript\" defer></script>";

    private readonly RequestDelegate _next;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<IndexHtmlInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexHtmlInjectionMiddleware"/> class.
    /// </summary>
    public IndexHtmlInjectionMiddleware(
        RequestDelegate next,
        IApplicationPaths appPaths,
        ILogger<IndexHtmlInjectionMiddleware> logger)
    {
        _next = next;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the middleware on each HTTP request.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var injectEnabled = Plugin.Instance?.Configuration.InjectClientScript ?? true;
        if (injectEnabled && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isIndexRequest = path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

            if (isIndexRequest && !string.IsNullOrWhiteSpace(_appPaths.WebPath))
            {
                var indexPath = Path.Combine(_appPaths.WebPath, "index.html");
                if (File.Exists(indexPath))
                {
                    try
                    {
                        var html = await File.ReadAllTextAsync(indexPath, context.RequestAborted).ConfigureAwait(false);
                        if (!html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase))
                        {
                            var closeBodyIdx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                            if (closeBodyIdx >= 0)
                            {
                                html = html.Insert(closeBodyIdx, ScriptTag + Environment.NewLine);
                            }
                            else
                            {
                                html += Environment.NewLine + ScriptTag;
                            }
                        }

                        var bytes = Encoding.UTF8.GetBytes(html);
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        context.Response.ContentType = "text/html; charset=utf-8";
                        context.Response.ContentLength = bytes.Length;
                        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                        context.Response.Headers.Pragma = "no-cache";
                        context.Response.Headers.Expires = "0";

                        if (HttpMethods.IsGet(context.Request.Method))
                        {
                            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "In-memory index.html injection fallback to static files.");
                    }
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
