using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// Database assumptions: Users.Mobile is unique, and OutboxMessages.Id is its
// primary key. A background worker processes the outbox and deduplicates events.
[ApiController]
[Route("api/users")]
public sealed class SubscriptionController(AppDbContext db) : ControllerBase
{
    [HttpPut("{mobile}/subscription")]
    public async Task<IActionResult> Subscribe(string mobile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mobile))
            return Problem("Mobile is required.", statusCode: 400);

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var now = DateTime.UtcNow;
            var changed = await db.Users
                .Where(user => user.Mobile == mobile && !user.IsSubscribed)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(user => user.IsSubscribed, true)
                    .SetProperty(user => user.SubscribedAt, now), ct);

            if (changed == 1)
            {
                var userId = await db.Users
                    .Where(user => user.Mobile == mobile)
                    .Select(user => user.Id)
                    .SingleAsync(ct);

                db.OutboxMessages.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    EventType = "UserSubscribed",
                    CreatedAt = now
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return Ok(new { status = "subscribed" });
            }

            await transaction.RollbackAsync(ct);
        }

        var userExists = await db.Users.AnyAsync(user => user.Mobile == mobile, ct);
        return userExists
            ? Ok(new { status = "already subscribed" })
            : NotFound();
    }
}

// Example outbox entity; configure its table and primary key in the DbContext.
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public string EventType { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
