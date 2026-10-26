using System.Net.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

public record CreatePaymentRequest(string OrderId, string PaymentMethodToken);

/*
Data model essentials:
Payments(Id PK, UserId, OrderId, IdempotencyKey, RequestHash, Amount,
         Currency, Status, ProviderPaymentId)
UNIQUE(UserId, IdempotencyKey)     -- retries
UNIQUE(ActiveOrderId)              -- one non-failed payment per order
WebhookEvents(ProviderEventId PK)  -- duplicate webhook protection

ActiveOrderId is a generated column: NULL when Status = 'Failed', otherwise
OrderId. Only store provider-issued payment tokens, never card details.
*/

[Authorize]
[HttpPost("api/payments")]
public async Task<IActionResult> Create(
    [FromHeader(Name = "Idempotency-Key")] string? key,
    CreatePaymentRequest request,
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
    await db.SaveChangesAsync(CancellationToken.None);

    return payment.Status switch
    {
        "Succeeded" => Created($"/api/payments/{payment.Id}", payment),
        "Failed" => StatusCode(402, new { status = "failed" }),
        _ => Accepted($"/api/payments/{payment.Id}", new { status = "unknown" })
    };
}

// Webhook outline:
// 1. Verify signature and timestamp on the raw body; reject invalid requests.
// 2. In one transaction, insert ProviderEventId and update payment status.
// 3. If the event ID is already present, roll back and return 200.
// This makes retries safe; use the provider API to reconcile Unknown payments.
