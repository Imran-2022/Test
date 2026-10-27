using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

public sealed class LoggingMiddleware(
    RequestDelegate next,
    ILogger<LoggingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        // The server creates the ID; do not trust a client-supplied value.
        var id = Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Correlation-ID"] = id;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = id
        });

        var timer = Stopwatch.StartNew();
        int? statusOverride = null; // the response status is not final when an exception is thrown
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected. This is a cancellation, not a server error.
            logger.LogInformation("Request cancelled by client: {Method} {Path}",
                context.Request.Method, context.Request.Path);
            statusOverride = 499;
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Request failed: {Method} {Path}",
                context.Request.Method, context.Request.Path);
            statusOverride = StatusCodes.Status500InternalServerError;
            throw; // Let the central exception handler create the response.
        }
        finally
        {
            logger.LogInformation("{Method} {Path} returned {Status} in {Ms} ms",
                context.Request.Method,
                context.Request.Path,
                statusOverride ?? context.Response.StatusCode,
                timer.ElapsedMilliseconds);
        }
    }
}

/*
Registration (Program.cs). Put it early so it times the whole pipeline, and
keep the central exception handler outside it or in the controllers' filter:

    app.UseMiddleware<LoggingMiddleware>();

Body logging should be opt-in for safe endpoints:
- Request: for a small body, call Request.EnableBuffering(), read a limited
  prefix, and restore Request.Body.Position = 0 in finally so the endpoint can
  still read the body. Skip files, multipart content and oversized bodies.
- Response: capture only with a bounded pass-through (tee) stream that forwards
  bytes immediately and keeps a small prefix; never buffer a large or streaming
  response in memory.
- Redact sensitive fields. Never log passwords, access tokens, authorization or
  cookie headers, payment credentials or unnecessary personal data. Prefer an
  endpoint allowlist over regex-only redaction.
*/