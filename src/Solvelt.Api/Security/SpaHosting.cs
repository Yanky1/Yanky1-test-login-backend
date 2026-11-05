using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;

namespace Solvelt.Api.Security;

/// <summary>
/// Serves the built frontend from <c>wwwroot</c> next to the API, so the SPA and the API are one site (the refresh cookie is SameSite=Strict).
/// Active only when <c>wwwroot/index.html</c> exists: in dev and tests nothing changes.
/// </summary>
public sealed record SpaSite(string Root, IFileProvider Files);

public static class SpaHosting
{
    /// <summary>The frontend README's Content-Security-Policy for pages (the API keeps its own strict policy).</summary>
    public const string PageCsp = "default-src 'self'; script-src 'self' https://challenges.cloudflare.com; frame-src https://challenges.cloudflare.com; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    public const string AssetsPath = "/assets";
    private const string AssetCache = "public, max-age=31536000, immutable";

    public static SpaSite? Detect(string? webRoot)
    {
        if (string.IsNullOrWhiteSpace(webRoot) || !File.Exists(Path.Combine(webRoot, "index.html"))) return null;
        return new(webRoot, new PhysicalFileProvider(webRoot));
    }

    /// <summary>Paths that belong to the API (and its health check), never to the SPA.</summary>
    public static bool IsApiPath(PathString p) => p.StartsWithSegments("/api") || p.StartsWithSegments("/health");

    public static void UseSpa(this WebApplication app, SpaSite spa)
    {
        app.UseWhen(c => !IsApiPath(c.Request.Path), b =>
        {
            var files = new FileServerOptions { FileProvider = spa.Files, EnableDefaultFiles = true, EnableDirectoryBrowsing = false };
            files.StaticFileOptions.OnPrepareResponse = ctx =>
            {
                // Vite fingerprints everything under /assets: cache it for a year. Pages keep the no-store set by the header middleware.
                if (ctx.Context.Request.Path.StartsWithSegments(AssetsPath)) ctx.Context.Response.Headers.CacheControl = AssetCache;
            };
            b.UseFileServer(files);
        });
    }

    /// <summary>Client-side routes (GET/HEAD, no file extension) get index.html. Anything under /api stays a 404; a missing file stays a 404.</summary>
    public static void MapSpaFallback(this WebApplication app, SpaSite spa)
    {
        app.MapFallback("{*path:nonfile}", async (HttpContext c) =>
        {
            if (IsApiPath(c.Request.Path) || !(HttpMethods.IsGet(c.Request.Method) || HttpMethods.IsHead(c.Request.Method)))
            {
                c.Response.StatusCode = StatusCodes.Status404NotFound;   // empty: the status-code pages middleware writes the problem+json body
                return;
            }
            c.Response.ContentType = "text/html; charset=utf-8";
            if (HttpMethods.IsHead(c.Request.Method)) return;
            await c.Response.SendFileAsync(spa.Files.GetFileInfo("index.html"));
        });
    }
}
