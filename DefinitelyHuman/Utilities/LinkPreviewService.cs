using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace DefinitelyHuman.Utilities;

/// <summary>
/// Fetches and caches OpenGraph link previews for URLs that appear in chat messages.
/// Fetch is fire-and-forget: <see cref="GetPreview"/> returns immediately (null if not yet
/// loaded) and <see cref="PreviewReady"/> fires when the data arrives so the UI can refresh.
/// Ported from Halloy's preview.rs (OG meta-tag parsing).
/// </summary>
public sealed partial class LinkPreviewService : IDisposable
{
    public record LinkPreview(string Url, string? Title, string? Description, string? ImageUrl);

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.Compiled)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?is)<meta\b[^>]*?>", RegexOptions.Compiled)]
    private static partial Regex MetaTagPattern();

    [GeneratedRegex(@"(?is)\b([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.Compiled)]
    private static partial Regex MetaAttrPattern();

    // null = loading/failed (no preview to show); non-null = loaded.
    private readonly ConcurrentDictionary<string, LinkPreview?> _cache = new();
    private readonly HttpClient _http;
    private readonly ILogger<LinkPreviewService> _logger;
    private readonly SemaphoreSlim _concurrency = new(3);

    /// <summary>Raised when a preview finishes loading so the dashboard can refresh.</summary>
    public event Action? PreviewReady;

    public LinkPreviewService(ILogger<LinkPreviewService> logger)
    {
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        });
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WhatsApp", "2"));
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>Finds all URLs in a text string.</summary>
    public static MatchCollection ExtractUrls(string text) => UrlPattern().Matches(text);

    /// <summary>
    /// Cleans common trailing punctuation that's part of the surrounding sentence, not the URL.
    /// </summary>
    public static string CleanUrl(string raw)
    {
        var span = raw.AsSpan();
        while (span.Length > 0 && ".,;:!?)]'\"".Contains(span[^1]))
            span = span[..^1];
        return span.ToString();
    }

    /// <summary>
    /// Returns a cached preview, or null if still loading/unavailable. On first call for a URL,
    /// kicks off a background fetch; <see cref="PreviewReady"/> fires when it completes.
    /// </summary>
    public LinkPreview? GetPreview(string url)
    {
        if (_cache.TryGetValue(url, out var preview))
            return preview;

        _cache.TryAdd(url, null);
        _ = FetchAsync(url);
        return null;
    }

    private async Task FetchAsync(string url)
    {
        _logger.LogDebug("Fetching link preview for {Url}", url);
        
        await _concurrency.WaitAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Preview fetch failed for {Url}: {StatusCode}", url, response.StatusCode);
                return;
            }

            string contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            // If the URL points directly to an image, store a minimal preview.
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var imagePreview = new LinkPreview(url, null, null, url);
                _cache[url] = imagePreview;
                _logger.LogDebug("Preview loaded for {Url}: image", url);
                PreviewReady?.Invoke();
                return;
            }

            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Preview skipped for {Url}: content-type {ContentType}", url, contentType);
                return;
            }

            // Read up to 64 KB — enough for the <head> where OG tags live.
            var buffer = new byte[64_000];
            int read;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length >= buffer.Length) break;
            }

            string html = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            var og = ParseOpenGraph(html);

            if (og.Title is null && og.ImageUrl is null)
            {
                _logger.LogWarning("Preview skipped for {Url}: no OG data", url);
                return; // no useful OG data
            }

            // Resolve relative og:image URLs against the page's base URL.
            string? resolvedImage = og.ImageUrl;
            if (resolvedImage is not null && Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
            {
                if (Uri.TryCreate(baseUri, resolvedImage, out var absolute))
                    resolvedImage = absolute.AbsoluteUri;
            }

            var p = new LinkPreview(url, og.Title, og.Description, resolvedImage);
            _cache[url] = p;
            
            _logger.LogDebug("Preview loaded for {Url}: title={Title}, image={HasImage}", url, p.Title, p.ImageUrl is not null);
            PreviewReady?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch link preview for {Url}", url);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private record OgData(string? Title, string? Description, string? ImageUrl, string? SiteName);

    /// <summary>
    /// Parses OpenGraph meta tags from raw HTML, the same approach as Halloy's parse_meta_tag_properties.
    /// </summary>
    private static OgData ParseOpenGraph(string html)
    {
        string? title = null, description = null, imageUrl = null, siteName = null;

        foreach (Match metaMatch in MetaTagPattern().Matches(html))
        {
            string? property = null, content = null;

            foreach (Match attrMatch in MetaAttrPattern().Matches(metaMatch.Value))
            {
                string key = attrMatch.Groups[1].Value.Trim().ToLowerInvariant();
                string value = attrMatch.Groups[2].Value
                    .Trim('\'', '"')
                    .Trim();
                value = System.Net.WebUtility.HtmlDecode(value);

                switch (key)
                {
                    case "property": property = value; break;
                    case "name" when property is null: property = value; break;
                    case "content": content = value; break;
                }
            }

            if (property is null || content is null)
                continue;

            switch (property.Trim().ToLowerInvariant())
            {
                case "og:title" when title is null: title = content; break;
                case "og:description" when description is null: description = content; break;
                case "og:image" or "og:image:url" or "og:image:secure_url" when imageUrl is null: imageUrl = content; break;
                case "og:site_name" when siteName is null: siteName = content; break;
            }
        }

        return new OgData(title, description, imageUrl, siteName);
    }

    public void Dispose()
    {
        _http.Dispose();
        _concurrency.Dispose();
    }
}
