using System.Diagnostics;

namespace Shortly.Middleware;

public static class PerformanceMiddleware
{
    // Custom SourceContext to isolate these logs from the rest
    private const string SlowRequestLoggerCategory = "Shortly.SlowRequests";
    private static readonly TimeSpan SlowRequestThreshold = TimeSpan.FromMilliseconds(500);

    public static IApplicationBuilder UsePerformanceMeasurement(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var stopwatch = Stopwatch.StartNew();

            // Headers can only be written before the body starts, that's why
            // we calculate X-Response-Time here and not after `next()`.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Response-Time"] = $"{stopwatch.ElapsedMilliseconds}ms";
                return Task.CompletedTask;
            });

            await next();

            stopwatch.Stop();

            if (stopwatch.Elapsed > SlowRequestThreshold)
            {
                // Custom logger to filter/alert on slow requests
                // without the noise of normal logs.
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(SlowRequestLoggerCategory);

                logger.LogWarning(
                    "Slow request: {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds}ms",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        });
    }
}