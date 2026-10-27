using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

public record ProductDto(int Id, string Name, decimal Price, int CategoryId, int BrandId);
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int CategoryId { get; set; }
    public int BrandId { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
public record ProductSearch(
    string? Q, decimal? MinPrice, decimal? MaxPrice,
    int[]? Categories, int[]? Brands,
    string? SortBy, string? SortDir, int Page = 1, int PageSize = 20);

[ApiController]
public sealed class ProductsController(AppDbContext db) : ControllerBase
{
    [HttpGet("api/products")]
    public async Task<IActionResult> Search([FromQuery] ProductSearch s, CancellationToken ct)
    {
        if (s.Page < 1 || s.PageSize is < 1 or > 100
            || (long)(s.Page - 1) * s.PageSize > int.MaxValue)
            return Problem("Invalid page or page size.", statusCode: 400);
        if (s.MinPrice is decimal min && s.MaxPrice is decimal max && min > max)
            return Problem("MinPrice must not exceed MaxPrice.", statusCode: 400);
        if ((s.Categories?.Length ?? 0) > 50 || (s.Brands?.Length ?? 0) > 50)
            return Problem("Too many category or brand filters.", statusCode: 400);

        var sort = string.IsNullOrWhiteSpace(s.SortBy) ? "newest" : s.SortBy.ToLowerInvariant();
        if (sort is not ("newest" or "price" or "name"))
            return Problem("SortBy must be newest, price, or name.", statusCode: 400);
        if (s.SortDir is not null && !s.SortDir.Equals("asc", StringComparison.OrdinalIgnoreCase)
            && !s.SortDir.Equals("desc", StringComparison.OrdinalIgnoreCase))
            return Problem("SortDir must be asc or desc.", statusCode: 400);
        var desc = !string.Equals(s.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        IQueryable<Product> q = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(s.Q))
        {
            // Escape LIKE wildcards so user-typed % or _ are matched literally.
            var term = s.Q.Trim()
                .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = term + "%";
            q = q.Where(p => EF.Functions.Like(p.Name, pattern, "\\"));
        }
        if (s.MinPrice is decimal minPrice) q = q.Where(p => p.Price >= minPrice);
        if (s.MaxPrice is decimal maxPrice) q = q.Where(p => p.Price <= maxPrice);
        if (s.Categories is { Length: > 0 }) q = q.Where(p => s.Categories.Contains(p.CategoryId));
        if (s.Brands is { Length: > 0 }) q = q.Where(p => s.Brands.Contains(p.BrandId));

        IOrderedQueryable<Product> ordered = sort switch
        {
            "price" => desc ? q.OrderByDescending(p => p.Price) : q.OrderBy(p => p.Price),
            "name" => desc ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name),
            _ => desc ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt)
        };

        // Count uses the same filters but no sorting or paging.
        var total = await q.CountAsync(ct);
        var items = await ordered.ThenBy(p => p.Id)
            .Skip((s.Page - 1) * s.PageSize).Take(s.PageSize)
            .Select(p => new ProductDto(p.Id, p.Name, p.Price, p.CategoryId, p.BrandId))
            .ToListAsync(ct);

        return Ok(new { items, total, page = s.Page, pageSize = s.PageSize });
    }
}

// Filter values are parameterized by EF Core; sort input is allowlisted.
// AsNoTracking and DTO projection avoid tracking and fetching unused columns.
// Index common filters/sorts, then verify the plan with EXPLAIN ANALYZE.
// Prefix matching can use an index; word search may need MySQL FULLTEXT.