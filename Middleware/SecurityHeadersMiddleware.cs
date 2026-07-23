namespace Shortly.Middleware;

public static class SecurityHeadersMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use((context, next) =>
        {
            // OnStarting fires only once, just before the first byte of the response, so the headers remain set in any response
            // regardless of what the middleware does afterward.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                // Forces browsers to use HTTPS with this origin for one year.
                // Mitigates SSL-stripping/downgrade attacks on the first plaintext request.    
                headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains; preload";

                // Prevents the browser from "guessing" a content type other than the one declared in Content-Type. 
                // Mitigates XSS due to MIME confusion.
                headers.XContentTypeOptions = "nosniff";

                // Prevents this site from rendering within an iframe on any origin.
                // Mitigates clickjacking.
                headers["X-Frame-Options"] = "DENY";

                // Only sends the full URL as the Referer in same-origin requests; in cross-origin requests, 
                // send only the origin, and sends nothing in downgrades.
                //  Mitigates leakage of sensitive URL data to third-party sites.
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

                // Denies access to sensitive browser APIs that this app never uses.
                // Mitigates abuse of these APIs by a compromised/injected third-party script
                // to spy on the camera/microphone or read the location.
                headers["Permissions-Policy"] =
                    "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

                return Task.CompletedTask;
            });

            return next();
        });
    }
}