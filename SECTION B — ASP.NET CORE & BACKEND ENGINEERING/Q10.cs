using System.Diagnostics;

public sealed class LoggingMiddleware(
    RequestDelegate next,
    ILogger<LoggingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        var id = Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Correlation-ID"] = id;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = id
        });

        var timer = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Request failed: {Method} {Path}",
                context.Request.Method, context.Request.Path);
            throw; // Let the central exception handler create the response.
        }
        finally
        {
            logger.LogInformation("{Method} {Path} returned {Status} in {Ms} ms",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                timer.ElapsedMilliseconds);
        }
    }

}

// Register after routing. Body logging should be opt-in for safe endpoints:
// for a small request, EnableBuffering(), read a limited prefix, and restore
// Position in finally. Redact sensitive fields and never log credentials or
// unnecessary personal data. Capture a response only with a bounded pass-through
// (tee) stream; do not buffer an entire or streaming response.
app.UseMiddleware<LoggingMiddleware>();
