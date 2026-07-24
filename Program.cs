using System.Globalization;
using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Serilog;
using Shortly.Application.Interfaces;
using Shortly.Application.Services;
using Shortly.Endpoints;
using Shortly.Infrastructure;
using Shortly.Infrastructure.Persistence;
using Shortly.Infrastructure.Repositories;
using Shortly.Middleware;

// Creates the ASP.NET Core application builder with initial configuration
var builder = WebApplication.CreateBuilder(args);

// Configures Serilog as the global bootstrap logger, reading all settings from appsettings.json
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

// Tells the host to use Serilog as its logging system
builder.Host.UseSerilog();

// Registers Razor Pages services
builder.Services.AddRazorPages();

// Registers the OpenAPI document generator with version 3.1 and API metadata
builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info = new()
        {
            Title = "Shortly API",
            Description = "A URL shortener service with user authentication and link management.",
            Version = "v1"
        };
        return Task.CompletedTask;
    });
});

// Registers the SQLite database context using Entity Framework Core
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("AppDbContext")));

// Configures a volatile server-side ticket store (auth state lost on restart)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSingleton<MemoryCacheTicketStore>();

// Configures cookie authentication with a server-side ticket store
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Error";

        // Cookie hardening audit (#9): explicit flags instead of relying on framework defaults.
        // - HttpOnly: document.cookie can never read this cookie, so even a successful XSS
        //   injection can't exfiltrate the session -- the #1 session-theft vector.
        // - SameSite=Strict: the browser never attaches this cookie to a request that originated
        //   from another site (including top-level link clicks), the strongest CSRF defense.
        //   Login is always a same-site form POST here (no cross-site login flow, and the CORS
        //   policy from #7 never sends credentials), so Strict costs nothing functionally.
        // - Path=/: scopes the cookie to the whole app, matching how every page actually uses it.
        // - Secure: mandatory outside Development so the cookie is never sent over plain HTTP;
        //   SameAsRequest only in Development keeps the http:// launch profile usable locally
        //   without hardcoding false (which would silently weaken production too).
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

// Injects the ticket store into the cookie options after the service provider is built
builder.Services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>>(sp =>
{
    var store = sp.GetRequiredService<MemoryCacheTicketStore>();
    return new ConfigureNamedOptions<CookieAuthenticationOptions>(
        CookieAuthenticationDefaults.AuthenticationScheme,
        options => options.SessionStore = store);
});

// Registers the authorization service
builder.Services.AddAuthorization();

// Razor Pages registers its antiforgery (CSRF token) cookie implicitly via AddRazorPages() below,
// with framework defaults. Configured explicitly here so the second cookie in play on every
// Login/Register/Index form submission gets the same hardening as the auth cookie above, instead
// of an unaudited implicit one.
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

// HTTP-semantic rate limiting (RFC 6585 429 Too Many Requests): replaces the old hand-rolled
// ConcurrentDictionary throttle in UserService.Login with the framework's rate limiter, keyed
// per client IP -- 10 login attempts per 5-minute fixed window, no queueing (extra attempts are
// rejected immediately rather than held/delayed). AddPolicy (not AddFixedWindowLimiter) is used
// so each IP gets its own independent window instead of one global counter shared by everyone.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("login", httpContext =>
    {
        // CORS preflight (OPTIONS) never reaches UserService.Login and carries no credentials --
        // counting it against the same budget as real attempts would let preflights from any
        // origin exhaust a victim's login quota. It's routed to an unlimited partition instead.
        if (HttpMethods.IsOptions(httpContext.Request.Method))
        {
            return RateLimitPartition.GetNoLimiter("preflight");
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0
            });
    });

    options.OnRejected = async (rejectedContext, cancellationToken) =>
    {
        // Retry-After tells the client exactly when it's worth retrying, instead of the client
        // guessing and hammering the endpoint again immediately.
        if (rejectedContext.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            rejectedContext.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        rejectedContext.HttpContext.Response.ContentType = "text/plain";
        await rejectedContext.HttpContext.Response.WriteAsync(
            "Too many login attempts. Please try again later.", cancellationToken);
    };
});

// CORS (Fetch Living Standard / RFC 9110 preflight): a browser blocks script-initiated
// cross-origin requests unless the server opts in. For "non-simple" requests -- custom headers,
// methods beyond GET/HEAD/POST-with-simple-content-type -- the browser first sends an OPTIONS
// "preflight" carrying Access-Control-Request-Method/-Headers, and only proceeds with the real
// request if the server answers with matching Access-Control-Allow-Origin/-Methods/-Headers.
// Only the redirect endpoint (GET /{shortUrl}) is a machine-consumable API here -- Razor Pages
// (Login/Register/Index) are server-rendered, same-origin, cookie-authenticated forms with no
// legitimate cross-origin caller, so no policy is applied to them and AllowAnyOrigin is never used.
const string ApiCorsPolicy = "ApiCors";
var corsAllowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["https://mi-frontend.example.com"]; // TODO: replace with the real frontend origin(s)

builder.Services.AddCors(options =>
{
    options.AddPolicy(ApiCorsPolicy, policy =>
    {
        policy.WithOrigins(corsAllowedOrigins)
            .WithMethods("GET")
            .WithHeaders("Content-Type", "Accept")
            .WithExposedHeaders("ETag", "Last-Modified", "X-Response-Time");
    });
});

// HTTP content-encoding negotiation (RFC 9110 §8.4): the client advertises the codecs it
// understands via `Accept-Encoding`, the server picks one, compresses the body, and marks it with
// `Content-Encoding` so the client knows how to inflate it. For compressible text (HTML/CSS/JS/JSON)
// this cuts transfer size substantially with negligible CPU cost, shortening time-to-first-byte on
// slow links. Brotli is registered first so it's preferred over Gzip when both are accepted, since
// it typically compresses smaller for the same content.
// EnableForHttps is left off (the framework default): compressing dynamic HTTPS responses that
// reflect user input next to a secret (e.g. the antiforgery token Razor Pages embeds in the Login
// and Register forms) can leak that secret byte-by-byte via response-size side channel (the BREACH
// attack). Static assets and plain-HTTP responses are unaffected and still get compressed.
builder.Services.AddResponseCompression(options =>
{
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml"]);
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

// Registers repositories and services for dependency injection (scoped lifetime)
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ILinkRepository, LinkRepository>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ILinkService, LinkService>();

// Builds the application with all registered configurations
var app = builder.Build();

// Registers baseline browser-side security headers on every response (pages, API, redirects,
// error pages) -- placed first so it wraps the whole pipeline, including the exception handler.
app.UseSecurityHeaders();

// Measures request latency and appends X-Response-Time; logs a dedicated warning for any
// request over 500ms. Placed early so the timer wraps the entire downstream pipeline.
app.UsePerformanceMeasurement();

// Compresses response bodies (Brotli/Gzip) before they're written to the wire. Must run before
// UseStaticFiles/endpoint execution -- it wraps the response stream so anything written downstream
// gets encoded -- and after the timing middleware so X-Response-Time still reflects the true
// request duration including compression work.
app.UseResponseCompression();

// In non-development environments, uses a friendly error page
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Redirects HTTP requests to HTTPS automatically
// app.UseHttpsRedirection();

// Serves static files from the wwwroot/ folder
app.UseStaticFiles();

// Enables request routing
app.UseRouting();

// Resolves CORS preflight/actual requests against the policy attached to the matched endpoint
// (must come after UseRouting so endpoint metadata is available). Placed before UseRateLimiter so
// a preflight OPTIONS to a CORS-protected endpoint is answered here directly and never reaches the
// rate limiter at all.
app.UseCors();

// Enables the rate limiter middleware (must come after UseRouting so endpoint metadata like
// [EnableRateLimiting] is available, and before the endpoints it protects run)
app.UseRateLimiter();

// Enables authentication (must come after UseRouting)
app.UseAuthentication();

// Enables authorization (must come after UseAuthentication)
app.UseAuthorization();

// Maps static assets with automatic versioning
app.MapStaticAssets();

// Maps Razor Pages with static asset support
app.MapRazorPages().WithStaticAssets();

// Exposes the OpenAPI document at /openapi/v1.json
app.MapOpenApi();

// Serves the Scalar interactive API reference UI at /scalar/v1
app.MapScalarApiReference();

// Maps the redirect endpoint GET /{shortUrl} from Endpoints/UrlRedirectEndpoint.cs
app.MapUrlRedirect();

// Creates a scope for scoped services (e.g. AppDbContext)
using (var scope = app.Services.CreateScope())
{
    // Gets the database context from the DI container
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Creates the database and tables if they do not exist
    db.Database.EnsureCreated();
    // Reads the admin password from configuration or uses a default value
    var seedPassword = app.Configuration["Seed:AdminPassword"] ?? "admin123";
    // Seeds initial data (admin user and sample links)
    await DbInitializer.InitializeAsync(db, seedPassword);
}

// Starts the application and begins listening for HTTP requests
await app.RunAsync();
