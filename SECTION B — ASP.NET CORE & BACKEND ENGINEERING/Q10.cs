// Request/Response Logging Middleware

// Middleware

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

public class LoggingMiddleware(RequestDelegate next, ILogger<LoggingMiddleware> log)
{
    const int Max = 2048;                                        // log at most 2 KB of a body
    static readonly Regex Secret = new(
        "\"(password|token|access_token|refresh_token|cardNumber|cvv|email|phone)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task Invoke(HttpContext ctx)
    {
        var cid = Guid.NewGuid().ToString("N");                  // don't trust client-supplied IDs
        ctx.Response.Headers["X-Correlation-ID"] = cid;
        using var scope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = cid });
        var sw = Stopwatch.StartNew();

        // request body: make it seekable, read a small prefix, rewind
        string req = "";
        if (IsText(ctx.Request.ContentType) && ctx.Request.ContentLength is > 0 and < 1_000_000)
        {
            ctx.Request.EnableBuffering();
            req = await Peek(ctx.Request.Body);
            ctx.Request.Body.Position = 0;                       // controller can still read it
        }

        // response body: swap in a MemoryStream, copy back at the end
        var original = ctx.Response.Body;
        var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            log.LogInformation("Client cancelled {Path}", ctx.Request.Path);   // not an error
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            buffer.SetLength(0);
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($"{{\"error\":\"Server error\",\"correlationId\":\"{cid}\"}}");
        }
        finally
        {
            string res = "";
            if (IsText(ctx.Response.ContentType) && buffer.Length < 1_000_000)
            {
                buffer.Position = 0;
                res = await Peek(buffer);
            }
            buffer.Position = 0;
            ctx.Response.Body = original;                        // always restore
            if (!ctx.RequestAborted.IsCancellationRequested)
                await buffer.CopyToAsync(original);
            await buffer.DisposeAsync();

            log.LogInformation("{Method} {Path} -> {Status} in {Ms} ms | req={Req} | res={Res}",
                ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode,
                sw.ElapsedMilliseconds, Redact(req), Redact(res));
        }
    }

    static bool IsText(string? t) => t != null && (t.Contains("json") || t.StartsWith("text/"));

    static async Task<string> Peek(Stream s)
    {
        var b = new byte[Max];
        var n = await s.ReadAsync(b);                            // at most 2 KB, never the whole stream
        return Encoding.UTF8.GetString(b, 0, n);
    }

    static string Redact(string s) => Secret.Replace(s, m => $"\"{m.Groups[1].Value}\":\"***\"");
}


// Register it first
app.UseMiddleware<LoggingMiddleware>();


