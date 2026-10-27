using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

public record CreatePaymentRequest(string OrderId, string PaymentMethodToken);

// Provider abstraction. Unknown means the outcome could not be determined.
public record ProviderResult(bool IsApproved, bool IsDeclined, string? ProviderId)
{
    public static ProviderResult Unknown { get; } = new(false, false, null);
}
public interface IPaymentProvider
{
    Task<ProviderResult> ChargeAsync(Guid paymentId, decimal amount, string currency,
        string paymentMethodToken, CancellationToken ct);
}

// Webhook payload (provider-specific in real life) and the dedupe table row.
public record ProviderEvent(string EventId, string PaymentId, string Type);
public class WebhookEvent { public string ProviderEventId { get; set; } = ""; public DateTime ReceivedAt { get; set; } }

/*
Data model essentials:
Payments(Id PK, UserId, OrderId, IdempotencyKey, RequestHash, Amount,
         Currency, Status, ProviderPaymentId)
UNIQUE(UserId, IdempotencyKey)     -- retries
UNIQUE(ActiveOrderId)              -- one non-failed payment per order
WebhookEvents(ProviderEventId PK)  -- duplicate webhook protection

ActiveOrderId is a generated column: NULL when Status = 'Failed', otherwise
OrderId. Only store provider-issued payment tokens, never card details.

Assumed elsewhere: AppDbContext (Payments, Orders, WebhookEvents), the Payment
and Order entities, and a User.GetUserId() claims helper.
State flow: Pending -> Processing -> Succeeded | Failed | Unknown.
Unknown is resolved later by a webhook or a provider status check.
*/

[ApiController]
[Authorize]
public sealed class PaymentsController(
    AppDbContext db, IPaymentProvider provider, IConfiguration config) : ControllerBase
{
    [HttpPost("api/payments")]
    public async Task<IActionResult> Create(
        [FromHeader(Name = "Idempotency-Key")] string? key,
        [FromBody] CreatePaymentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(request.OrderId)
            || string.IsNullOrWhiteSpace(request.PaymentMethodToken))
            return Problem("Invalid request.", statusCode: 400);

        var userId = User.GetUserId();
        var orderId = request.OrderId.Trim();
        var hash = Sha256(orderId);

        var payment = await db.Payments.SingleOrDefaultAsync(
            p => p.UserId == userId && p.IdempotencyKey == key, ct);
        if (payment is not null)
            return payment.RequestHash == hash
                ? ReplaySavedResult(payment)
                : Problem("Key was used for a different request.", statusCode: 422);

        // Always get price and currency from the authenticated user's order.
        var order = await db.Orders.SingleOrDefaultAsync(
            o => o.Id == orderId && o.UserId == userId, ct);
        if (order is null) return NotFound();

        payment = new Payment {
            Id = Guid.NewGuid(), UserId = userId, OrderId = order.Id,
            IdempotencyKey = key, RequestHash = hash, Amount = order.TotalAmount,
            Currency = order.Currency, Status = "Pending"
        };
        try
        {
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct); // unique indexes decide the winner
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
        {
            payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(
                p => p.UserId == userId && p.IdempotencyKey == key, ct);
            return payment is null
                ? Conflict(new { error = "Payment already exists or is processing." })
                : payment.RequestHash == hash
                    ? ReplaySavedResult(payment)
                    : Problem("Key was used for a different request.", statusCode: 422);
        }

        await db.Payments.Where(p => p.Id == payment.Id && p.Status == "Pending")
            .ExecuteUpdateAsync(update => update.SetProperty(p => p.Status, "Processing"), ct);

        // Do not hold a database transaction during the network call. Use the
        // payment ID as the provider idempotency key; a lock is not a DB constraint.
        ProviderResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            result = await provider.ChargeAsync(payment.Id, payment.Amount,
                payment.Currency, request.PaymentMethodToken, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            result = ProviderResult.Unknown; // Reconcile; do not assume it failed.
        }

        payment.Status = result.IsApproved ? "Succeeded"
            : result.IsDeclined ? "Failed" : "Unknown";
        payment.ProviderPaymentId = result.ProviderId;
        await db.SaveChangesAsync(CancellationToken.None); // persist even if the client left

        return payment.Status switch
        {
            "Succeeded" => Created($"/api/payments/{payment.Id}", payment),
            "Failed" => StatusCode(402, new { status = "failed" }),
            _ => Accepted($"/api/payments/{payment.Id}", new { status = "unknown" })
        };
    }

    // Same key, same request: return the saved outcome instead of charging again.
    private IActionResult ReplaySavedResult(Payment p)
    {
        switch (p.Status)
        {
            case "Succeeded": return Created($"/api/payments/{p.Id}", p);
            case "Failed":    return StatusCode(402, new { status = "failed" });
            case "Unknown":   return Accepted($"/api/payments/{p.Id}", new { status = "unknown" });
            default: // Pending or Processing: the first request is still running
                Response.Headers.RetryAfter = "2";
                return Conflict(new { status = "processing" });
        }
    }

    [AllowAnonymous]
    [RequestSizeLimit(65_536)]
    [HttpPost("api/payments/webhook")]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        // 1. Verify the signature over the exact raw bytes, plus a recent timestamp.
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();

        var timestamp = Request.Headers["X-Webhook-Timestamp"].ToString();
        var signature = Request.Headers["X-Webhook-Signature"].ToString();
        if (!long.TryParse(timestamp, out var ts)
            || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
            return Unauthorized();

        byte[] received;
        try { received = Convert.FromHexString(signature); }
        catch (FormatException) { return Unauthorized(); }

        var secret = Encoding.UTF8.GetBytes(config["Payments:WebhookSecret"]!);
        var signedPayload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        var expected = HMACSHA256.HashData(secret, signedPayload);
        if (!CryptographicOperations.FixedTimeEquals(expected, received))
            return Unauthorized();

        var evt = JsonSerializer.Deserialize<ProviderEvent>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (evt is null || string.IsNullOrWhiteSpace(evt.EventId) || !Guid.TryParse(evt.PaymentId, out var paymentId))
            return BadRequest();

        var newStatus = evt.Type switch
        {
            "payment.succeeded" => "Succeeded",
            "payment.failed" => "Failed",
            _ => null
        };
        if (newStatus is null) return Ok(); // event type we do not act on

        // 2. One transaction: record the event ID and update the payment.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Set<WebhookEvent>().Add(new WebhookEvent
            {
                ProviderEventId = evt.EventId, ReceivedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct); // duplicate event ID -> 1062

            // Only move forward from a non-final state; late or reordered events cannot undo a result.
            await db.Payments
                .Where(p => p.Id == paymentId
                    && (p.Status == "Processing" || p.Status == "Unknown" || p.Status == "Pending"))
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, newStatus), ct);

            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
        {
            // 3. Duplicate delivery: already handled, so acknowledge it.
        }
        return Ok();
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}