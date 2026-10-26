// Product Search API

CREATE TABLE Products(
  Id INT AUTO_INCREMENT PRIMARY KEY, Name VARCHAR(100) NOT NULL, Description TEXT,
  Price DECIMAL(10,2) NOT NULL, CategoryId INT NOT NULL, BrandId INT NOT NULL,
  IsActive TINYINT(1) NOT NULL DEFAULT 1, CreatedAt DATETIME NOT NULL,
  FULLTEXT KEY FT_Products_Name (Name),
  KEY IX_Products_Cat_Price (CategoryId, Price, Id),
  KEY IX_Products_Brand_Price (BrandId, Price, Id)
);
INSERT INTO Products(Name,Price,CategoryId,BrandId,CreatedAt) VALUES
('Phone X',500,1,5,NOW()),('Phone Y',800,1,7,NOW()),('Laptop Z',900,2,5,NOW());


public class Product { public int Id {get;set;} public string Name {get;set;} = ""; public decimal Price {get;set;}
                       public int CategoryId {get;set;} public int BrandId {get;set;} public bool IsActive {get;set;}
                       public DateTime CreatedAt {get;set;} }
public record ProductDto(int Id, string Name, decimal Price, int CategoryId, int BrandId);
public record ProductSearch(string? Q, decimal? MinPrice, decimal? MaxPrice, int[]? Categories, int[]? Brands,
                            string? SortBy, string? SortDir, int Page = 1, int PageSize = 20);



// Endpoint

[HttpGet("api/products")]
public async Task<IActionResult> Search([FromQuery] ProductSearch s, CancellationToken ct)
{
    // validation
    if (s.Page < 1 || s.PageSize is < 1 or > 100) return Problem("Page >= 1, PageSize 1-100.", statusCode: 400);
    if (s.MinPrice > s.MaxPrice) return Problem("MinPrice > MaxPrice.", statusCode: 400);
    if ((s.Categories?.Length ?? 0) > 50 || (s.Brands?.Length ?? 0) > 50) return Problem("Too many filters.", statusCode: 400);
    var desc = !string.Equals(s.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

    IQueryable<Product> q = db.Products.AsNoTracking().Where(p => p.IsActive);

    if (!string.IsNullOrWhiteSpace(s.Q))
    {
        var term = s.Q.Trim();
        q = q.Where(p => EF.Functions.Like(p.Name, term + "%"));      // prefix LIKE can use an index; parameterized
        // for word search use FULLTEXT: MATCH(Name) AGAINST(@term) via a provider extension or raw SQL
    }
    if (s.MinPrice is not null) q = q.Where(p => p.Price >= s.MinPrice);
    if (s.MaxPrice is not null) q = q.Where(p => p.Price <= s.MaxPrice);
    if (s.Categories is { Length: > 0 }) q = q.Where(p => s.Categories.Contains(p.CategoryId));   // becomes IN (...)
    if (s.Brands is { Length: > 0 })     q = q.Where(p => s.Brands.Contains(p.BrandId));

    // sort: allowlist only, with Id as a tiebreaker so pages are stable
    IOrderedQueryable<Product> ordered = (s.SortBy?.ToLowerInvariant()) switch
    {
        null or "" or "newest" => desc ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
        "price" => desc ? q.OrderByDescending(p => p.Price) : q.OrderBy(p => p.Price),
        "name"  => desc ? q.OrderByDescending(p => p.Name)  : q.OrderBy(p => p.Name),
        _ => null!
    };
    if (ordered is null) return Problem("Invalid SortBy. Use newest, price or name.", statusCode: 400);
    ordered = desc ? ordered.ThenByDescending(p => p.Id) : ordered.ThenBy(p => p.Id);

    var total = await q.CountAsync(ct);                                // same filters, no ORDER BY, no joins
    var items = total == 0 ? new List<ProductDto>() : await ordered
        .Skip((s.Page - 1) * s.PageSize).Take(s.PageSize)
        .Select(p => new ProductDto(p.Id, p.Name, p.Price, p.CategoryId, p.BrandId))   // projection
        .ToListAsync(ct);

    return Ok(new { items, total, page = s.Page, pageSize = s.PageSize });
}

/*

Explanation
SQL injection: all values go through LINQ, so EF sends them as parameters. The sort field is never used as text. It is matched against a switch allowlist, and direction is only asc or desc. Never use FromSqlRaw with string concatenation. If raw SQL is needed (FULLTEXT), use FromSqlInterpolated or explicit parameters.

Validation: cap the page size at 100, and also cap the filter-array sizes and the price range. Return 400 with ProblemDetails for bad input.

AsNoTracking and projection: no change tracking and no SELECT *. Only the 5 columns the DTO needs are read, and Description is never fetched.

Efficient count: the count query shares the filters but has no ORDER BY, no Skip/Take and no projection, so it can use a covering index. Skip the data query when the total is 0. For very heavy traffic, cache the count for common filters for 30 to 60 seconds, or return hasNext (fetch pageSize + 1 rows) instead of an exact count.

Indexes: (CategoryId, Price, Id) and (BrandId, Price, Id) cover the common filters. Equality or IN columns come first, then the range or sort column. Check each filter combination with EXPLAIN ANALYZE.

Cancellation: ct is passed to every async call.