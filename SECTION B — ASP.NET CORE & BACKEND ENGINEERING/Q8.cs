using System.Linq.Expressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// Generic query service
/*
MySQL setup outline:
CREATE TABLE Customers(Id INT PRIMARY KEY, Name VARCHAR(100), Country CHAR(2));
CREATE TABLE Orders(Id INT PRIMARY KEY AUTO_INCREMENT, CustomerId INT NOT NULL, OrderDate DATETIME NOT NULL,
                    Amount DECIMAL(12,2) NOT NULL, Status VARCHAR(20) NOT NULL,
                    FOREIGN KEY (CustomerId) REFERENCES Customers(Id));
CREATE INDEX IX_Orders_Status_Date ON Orders(Status, OrderDate);
INSERT INTO Customers VALUES (1,'Rahim','BD'),(2,'John','US');
INSERT INTO Orders(CustomerId,OrderDate,Amount,Status) VALUES (1,'2026-06-10',500,'PAID'),(2,'2026-06-11',300,'NEW');
*/

public class Customer { public int Id {get;set;} public string Name {get;set;} = ""; public string Country {get;set;} = ""; }
public class Order { public int Id {get;set;} public int CustomerId {get;set;} public Customer Customer {get;set;} = null!;
                     public DateTime OrderDate {get;set;} public decimal Amount {get;set;} public string Status {get;set;} = ""; }
public record OrderDto(int Id, string CustomerName, DateTime OrderDate, decimal Amount, string Status);

// Abstraction

public sealed class QuerySpec<T, TDto>
{
    public List<Expression<Func<T, bool>>> Filters { get; } = new();          // build from validated server-side input
    public List<Expression<Func<T, object>>> Includes { get; } = new();
    public Expression<Func<T, TDto>> Select { get; init; } = default!;        // project only the required fields
    public Func<IQueryable<T>, IOrderedQueryable<T>> Order { get; init; } = default!;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public interface IQueryService
{
    Task<PagedResult<TDto>> GetPageAsync<T, TDto>(QuerySpec<T, TDto> spec, CancellationToken ct) where T : class;
}

public sealed class QueryService(AppDbContext db) : IQueryService
{
    public async Task<PagedResult<TDto>> GetPageAsync<T, TDto>(QuerySpec<T, TDto> spec, CancellationToken ct) where T : class
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Select is null || spec.Order is null)
            throw new ArgumentException("A projection and sort order are required.", nameof(spec));

        var size = Math.Clamp(spec.PageSize, 1, 100);
        var page = Math.Max(spec.Page, 1);
        var offset = (long)(page - 1) * size;
        if (offset > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(spec.Page), "The requested page is too large.");

        IQueryable<T> q = db.Set<T>().AsNoTracking();
        foreach (var include in spec.Includes) q = q.Include(include);
        foreach (var filter in spec.Filters) q = q.Where(filter);

        var total = await q.CountAsync(ct);
        if (total == 0) return new(Array.Empty<TDto>(), 0, page, size);

        var items = await spec.Order(q)
            .Skip((int)offset).Take(size)
            .Select(spec.Select)
            .ToListAsync(ct);

        return new(items, total, page, size);
    }
}


// Using it: allowlists and type safety. The customer name is translated from
// the navigation-property expression; an Include is not needed for this DTO.

public record OrderSearch(string? Status, DateTime? From, DateTime? To, decimal? MinAmount,
                          string? SortBy, string? SortDir, int Page = 1, int PageSize = 20);

static readonly Dictionary<string, Func<IQueryable<Order>, bool, IOrderedQueryable<Order>>> Sorts =
    new(StringComparer.OrdinalIgnoreCase)
{
    ["date"]   = (q, desc) => desc ? q.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
                                   : q.OrderBy(o => o.OrderDate).ThenBy(o => o.Id),
    ["amount"] = (q, desc) => desc ? q.OrderByDescending(o => o.Amount).ThenByDescending(o => o.Id)
                                   : q.OrderBy(o => o.Amount).ThenBy(o => o.Id),
    ["status"] = (q, desc) => desc ? q.OrderByDescending(o => o.Status).ThenByDescending(o => o.Id)
                                   : q.OrderBy(o => o.Status).ThenBy(o => o.Id),
};

[HttpGet("api/orders")]
public async Task<IActionResult> Search([FromQuery] OrderSearch s, IQueryService svc, CancellationToken ct)
{
    if (s.SortBy is not null && !Sorts.ContainsKey(s.SortBy)) return Problem("Invalid SortBy.", statusCode: 400);
    if (s.SortDir is not null
        && !s.SortDir.Equals("asc", StringComparison.OrdinalIgnoreCase)
        && !s.SortDir.Equals("desc", StringComparison.OrdinalIgnoreCase))
        return Problem("SortDir must be asc or desc.", statusCode: 400);
    if (s.From is not null && s.To is not null && s.From > s.To)
        return Problem("From must not be later than To.", statusCode: 400);
    var sort = Sorts[s.SortBy ?? "date"];
    var desc = !string.Equals(s.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

    var spec = new QuerySpec<Order, OrderDto>
    {
        Select = o => new OrderDto(o.Id, o.Customer.Name, o.OrderDate, o.Amount, o.Status),
        Order  = q => sort(q, desc),
        Page = s.Page, PageSize = s.PageSize
    };
    if (s.Status is not null)    spec.Filters.Add(o => o.Status == s.Status);
    if (s.From is not null)      spec.Filters.Add(o => o.OrderDate >= s.From);
    if (s.To is not null)        spec.Filters.Add(o => o.OrderDate < s.To.Value.AddDays(1));
    if (s.MinAmount is not null) spec.Filters.Add(o => o.Amount >= s.MinAmount);
    if (s.Page < 1 || s.PageSize < 1 || s.PageSize > 100
        || (long)(s.Page - 1) * s.PageSize > int.MaxValue)
        return Problem("Page must be positive and PageSize must be between 1 and 100.", statusCode: 400);

    return Ok(await svc.GetPageAsync(spec, ct));
}
