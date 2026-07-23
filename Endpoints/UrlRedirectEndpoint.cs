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
            try
            {
                var link = await linkService.GetLink(shortUrl);

                // ETag/Last-Modified to condition the GET request (RFC 9110 §13).
                // The validator is calculated using only ShortUrl + Url + CreatedAt, excluding Clicks.
                // (otherwise, it would change with each request and the cache would be useless).
                var etag = ComputeETag(link);
                var lastModified = TrimToSeconds(link.CreatedAt);

                httpContext.Response.Headers.CacheControl = "private, must-revalidate";
                httpContext.Response.Headers.ETag = etag;
                httpContext.Response.Headers.LastModified = lastModified.ToString("R");

                await linkService.IncrementClicks(link.Id);

                if (IsNotModified(httpContext.Request, etag, lastModified))
                {
                    // 304 doesn't include a body: the client already has a valid copy,
                    // this saves us from resending Location/payload.
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                return Results.Redirect(link.Url);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .RequireCors("ApiCors"); // Only cross-origin caller: a JS client resolving/previewing a short link via fetch.
    }

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