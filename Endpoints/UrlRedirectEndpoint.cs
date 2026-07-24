using System.Security.Cryptography;
using System.Text;
using Shortly.Application.DTOs;
using Shortly.Application.Interfaces;

namespace Shortly.Endpoints;

public static class UrlRedirectEndpoint
{
    public static void MapUrlRedirect(this WebApplication app)
    {
        app.MapGet("/{shortUrl}", async (string shortUrl, HttpContext httpContext, ILinkService linkService) =>
        {
            // Content negotiation for errors (RFC 9457, formerly RFC 7807): instead of an ad-hoc
            // plain-text body, error responses use application/problem+json -- a standard shape
            // (type/title/status/detail/instance) that any HTTP client/library can parse uniformly,
            // instead of having to special-case this API's error format.
            if (!IsValidShortUrl(shortUrl))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid short URL",
                    detail: $"'{shortUrl}' is not a well-formed short URL (expected 1-{MaxShortUrlLength} base62 characters).");
            }

            try
            {
                var link = await linkService.GetLink(shortUrl);

                // ETag/Last-Modified to condition the GET request (RFC 9110 §13).
                // The validator is calculated using only ShortUrl + Url + CreatedAt, excluding Clicks.
                // (otherwise, it would change with each request and the cache would be useless).
                var etag = ComputeETag(link);
                var lastModified = TrimToSeconds(link.CreatedAt);

                httpContext.Response.Headers.ETag = etag;
                httpContext.Response.Headers.LastModified = lastModified.ToString("R");

                await linkService.IncrementClicks(link.Id);

                if (IsNotModified(httpContext.Request, etag, lastModified))
                {
                    // 304 doesn't include a body: the client already has a valid copy,
                    // this saves us from resending Location/payload.
                    httpContext.Response.Headers.CacheControl = "private, must-revalidate";
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                // Conditional redirect status codes (RFC 9110 §15.4): 301/302/307/308 all send the
                // browser elsewhere, but they mean different things to clients and caches --
                // - 301 Moved Permanently: "this mapping won't change, stop asking" -- browsers/proxies
                //   may cache it indefinitely as a heuristic, even past what Cache-Control says on
                //   older clients. Used once a link has proven itself (>100 clicks): it's earned the
                //   long-lived treatment.
                // - 307 Temporary Redirect: "this is where it points *right now*", explicitly
                //   preserves the request method/body (unlike 302 on some legacy clients) and is
                //   never cached as a location change. Used for brand-new, unclicked links (<24h old)
                //   that haven't proven they're staying at this destination.
                // - 302 Found: semantically-neutral fallback for links that are neither confirmed-
                //   stable nor freshly-created -- no strong signal either way.
                // - 308 (not used here): the "308" of 301 -- permanent + method-preserving. Not
                //   needed since this endpoint is GET-only; no method to preserve.
                var isStable = link.Clicks > 100;
                var isNew = link.Clicks == 0 && DateTimeOffset.UtcNow - link.CreatedAt < TimeSpan.FromHours(24);

                if (isStable)
                {
                    // A 301 can be cached "forever" by non-compliant clients/proxies regardless of
                    // Cache-Control, which is riskier than the 304 case above: if this link later
                    // changes state (e.g. gets deleted), such a client would never re-check the ETag.
                    // An explicit short max-age bounds that blast radius for compliant clients without
                    // fully losing the "long-lived" signal 301 is meant to carry.
                    httpContext.Response.Headers.CacheControl = "public, max-age=300, must-revalidate";
                    return Results.Redirect(link.Url, permanent: true);
                }

                httpContext.Response.Headers.CacheControl = "private, must-revalidate";
                return isNew
                    ? Results.Redirect(link.Url, permanent: false, preserveMethod: true) // 307
                    : Results.Redirect(link.Url); // 302 fallback
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Short URL not found",
                    detail: ex.Message);
            }
        })
        .RequireCors("ApiCors"); // Only cross-origin caller: a JS client resolving/previewing a short link via fetch.
    }

    // Matches Link.ShortUrl's [MaxLength(32)]. Not a fixed length: generated short codes are 12
    // base62 chars (LinkService.GenerateShortUrl), but seeded/hand-picked ones aren't (e.g. "aspnet",
    // "github") -- only the character set is a real invariant, so that's all we validate here.
    private const int MaxShortUrlLength = 32;
    private const string Base62Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static bool IsValidShortUrl(string shortUrl) =>
        shortUrl.Length is > 0 and <= MaxShortUrlLength && shortUrl.All(Base62Alphabet.Contains);

    private static string ComputeETag(LinkResponse link)
    {
        var stableState = $"{link.ShortUrl}:{link.Url}:{link.CreatedAt:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableState));
        return $"\"{Convert.ToHexString(hash)[..16].ToLowerInvariant()}\"";
    }

    private static DateTimeOffset TrimToSeconds(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Offset);

    private static bool IsNotModified(HttpRequest request, string etag, DateTimeOffset lastModified)
    {
        // If-None-Match takes precedence over If-Modified-Since if both are present
        // (the ETag is exact, the date is only accurate to the second).
        var ifNoneMatch = request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count > 0)
        {
            return ifNoneMatch.Any(value => value == "*" || value == etag);
        }

        var ifModifiedSinceHeader = request.Headers.IfModifiedSince;
        if (ifModifiedSinceHeader.Count > 0 &&
            DateTimeOffset.TryParse(ifModifiedSinceHeader.ToString(), out var ifModifiedSince))
        {
            return lastModified <= ifModifiedSince;
        }

        return false;
    }
}