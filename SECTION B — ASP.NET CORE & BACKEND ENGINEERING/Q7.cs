// Payment API Design

// // Data Model 

CREATE TABLE Payments(
  Id CHAR(36) PRIMARY KEY,
  UserId INT NOT NULL,
  OrderId VARCHAR(40) NOT NULL,
  IdempotencyKey VARCHAR(64) NOT NULL,
  RequestHash CHAR(64) NOT NULL,                 -- SHA-256 of the canonical request
  Amount DECIMAL(12,2) NOT NULL,
  Currency CHAR(3) NOT NULL,
  Status VARCHAR(12) NOT NULL DEFAULT 'Pending', -- Pending|Processing|Succeeded|Failed|Unknown
  ProviderPaymentId VARCHAR(64) NULL,
  FailureCode VARCHAR(50) NULL,
  CreatedAt DATETIME(3) NOT NULL,
  UpdatedAt DATETIME(3) NOT NULL,
  ActiveOrderId VARCHAR(40) GENERATED ALWAYS AS (IF(Status='Failed', NULL, OrderId)) STORED,
  UNIQUE KEY UX_Pay_Key (UserId, IdempotencyKey),   -- idempotency
  UNIQUE KEY UX_Pay_ActiveOrder (ActiveOrderId),    -- one live payment per order (NULLs allowed repeatedly)
  UNIQUE KEY UX_Pay_Provider (ProviderPaymentId),
  KEY IX_Pay_Status (Status, UpdatedAt)             -- reconciler scan
);
CREATE TABLE WebhookEvents(
  ProviderEventId VARCHAR(64) PRIMARY KEY,          -- dedupe
  Type VARCHAR(50) NOT NULL, PaymentId CHAR(36) NULL, ReceivedAt DATETIME(3) NOT NULL
);
INSERT INTO Payments(Id,UserId,OrderId,IdempotencyKey,RequestHash,Amount,Currency,Status,CreatedAt,UpdatedAt)
VALUES (UUID(),1,'ORD-1','key-1',REPEAT('a',64),500.00,'BDT','Pending',NOW(3),NOW(3));


// Critical path (C#)

public enum PaymentStatus { Pending, Processing, Succeeded, Failed, Unknown }
public record CreatePaymentRequest(string OrderId, decimal Amount, string Currency, string Token);

[HttpPost("api/payments")]
public async Task<IActionResult> Create([FromHeader(Name = "Idempotency-Key")] string? key,
                                        CreatePaymentRequest req, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
        return Problem("Idempotency-Key header is required.", statusCode: 400);
    if (req.Amount <= 0 || req.Currency?.Length != 3)
        return Problem("Invalid amount or currency.", statusCode: 422);

    var userId = User.GetUserId();
    var hash = Sha256($"{req.OrderId}|{req.Amount}|{req.Currency}");   // never include card data
    var p = new Payment { Id = Guid.NewGuid(), UserId = userId, OrderId = req.OrderId,
        IdempotencyKey = key, RequestHash = hash, Amount = req.Amount, Currency = req.Currency,
        Status = PaymentStatus.Pending, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    // 1) Idempotency + duplicate prevention: the DB unique indexes decide the winner
    db.Payments.Add(p);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException e) when (e.InnerException is MySqlException { Number: 1062 })
    {
        db.Entry(p).State = EntityState.Detached;
        var existing = await db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.IdempotencyKey == key, ct);
        return existing is null
            ? Problem("An active payment already exists for this order.", statusCode: 409)  // order-level unique hit
            : Replay(existing, hash);
    }

    // 2) Claim (only the request that inserted the row gets here)
    await db.Payments.Where(x => x.Id == p.Id && x.Status == PaymentStatus.Pending)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PaymentStatus.Processing)
                                  .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);

    // 3) Provider call: no DB transaction open, hard timeout, provider idempotency key = our Id
    ProviderResult r;
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        r = await provider.ChargeAsync(p.Id.ToString(), req.Amount, req.Currency, req.Token, cts.Token);
    }
    catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
    {
        r = ProviderResult.Unknown;     // timeout/network: outcome is UNKNOWN, not "failed"
    }

    // 4) Persist outcome with CancellationToken.None so a client disconnect can't lose the result
    var status = r.Outcome switch {
        Outcome.Approved => PaymentStatus.Succeeded,
        Outcome.Declined => PaymentStatus.Failed,
        _                => PaymentStatus.Unknown };      // 5xx / timeout / unparseable

    await db.Payments.Where(x => x.Id == p.Id &&
            (x.Status == PaymentStatus.Processing || x.Status == PaymentStatus.Unknown))
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status)
            .SetProperty(x => x.ProviderPaymentId, r.ProviderId)
            .SetProperty(x => x.FailureCode, r.Code)
            .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), CancellationToken.None);

    return Result(p.Id, status);
}

IActionResult Result(Guid id, PaymentStatus s) => s switch {
    PaymentStatus.Succeeded => Created($"/api/payments/{id}", new { id, status = "succeeded" }),
    PaymentStatus.Failed    => StatusCode(402, new { id, status = "failed" }),
    _                       => Accepted($"/api/payments/{id}", new { id, status = "processing" }) };

IActionResult Replay(Payment e, string hash)
{
    if (e.RequestHash != hash)
        return Problem("Idempotency-Key was already used with a different request.", statusCode: 422);
    Response.Headers["Idempotent-Replayed"] = "true";
    if (e.Status is PaymentStatus.Pending or PaymentStatus.Processing)
    { Response.Headers["Retry-After"] = "2"; return Problem("Payment is still in progress.", statusCode: 409); }
    return Result(e.Id, e.Status);
}


// Webhook (verification and dedupe)

[HttpPost("api/webhooks/provider")]
public async Task<IActionResult> Webhook()
{
    var body = await new StreamReader(Request.Body).ReadToEndAsync();      // RAW body, not re-serialized JSON
    if (!Verify(body, Request.Headers["X-Timestamp"], Request.Headers["X-Signature"]))
        return Unauthorized();

    var evt = JsonSerializer.Deserialize<ProviderEvent>(body)!;
    await using var tx = await db.Database.BeginTransactionAsync();        // dedupe + apply atomically
    db.WebhookEvents.Add(new WebhookEvent { ProviderEventId = evt.Id, Type = evt.Type,
                                            PaymentId = evt.PaymentRef, ReceivedAt = DateTime.UtcNow });
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException e) when (e.InnerException is MySqlException { Number: 1062 })
    { return Ok(); }                                                       // duplicate delivery

    var target = evt.Type == "payment.succeeded" ? PaymentStatus.Succeeded : PaymentStatus.Failed;
    await db.Payments.Where(x => x.Id == evt.PaymentRef &&
            x.Status != PaymentStatus.Succeeded && x.Status != PaymentStatus.Failed)  // terminal states are immutable
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, target)
            .SetProperty(x => x.ProviderPaymentId, evt.ProviderId)
            .SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
    await tx.CommitAsync();
    return Ok();            // an exception -> 500, tx rolls back, the provider retries
}

static bool Verify(string body, string ts, string sig)
{
    if (!long.TryParse(ts, out var t) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 300) return false; // replay window
    using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
    var exp = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{body}"))).ToLowerInvariant();
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(exp), Encoding.UTF8.GetBytes(sig));
}

