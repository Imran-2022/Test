using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// Dynamic LINQ sorting
/*
MySQL setup:
CREATE TABLE Employees(
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Name VARCHAR(50),
    Salary DECIMAL(10,2),
    JoiningDate DATE
);
*/

public class Employee { public int Id {get;set;} public string Name {get;set;} = "";
                        public decimal Salary {get;set;} public DateTime JoiningDate {get;set;} }


public class InvalidSortException(string msg) : ArgumentException(msg) { }

public static class SortExtensions
{
    public static IQueryable<T> OrderBy<T>(this IQueryable<T> source, string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort)) return source;              // nothing requested

        var parts = sort.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2) throw new InvalidSortException("Invalid sort.");

        // Accept public scalar properties, not navigation properties or indexers.
        // Compare names ourselves: GetProperty(IgnoreCase) can throw
        // AmbiguousMatchException when two properties differ only by case.
        var prop = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (prop == null || prop.GetIndexParameters().Length != 0 || !IsSortable(prop.PropertyType))
            throw new InvalidSortException($"Cannot sort by '{parts[0]}'.");

        var desc = false;
        if (parts.Length == 2)
        {
            if (parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase)) desc = true;
            else if (!parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidSortException("Direction must be asc or desc.");
        }

        var x = Expression.Parameter(typeof(T), "x");                    // x => x.Prop
        var lambda = Expression.Lambda(Expression.Property(x, prop), x);

        var call = Expression.Call(typeof(Queryable), desc ? "OrderByDescending" : "OrderBy",
            new[] { typeof(T), prop.PropertyType }, source.Expression, Expression.Quote(lambda));

        return source.Provider.CreateQuery<T>(call);
    }

    static bool IsSortable(Type type)
    {
        var scalar = Nullable.GetUnderlyingType(type) ?? type;
        return scalar.IsPrimitive || scalar.IsEnum || scalar == typeof(string)
            || scalar == typeof(decimal) || scalar == typeof(DateTime)
            || scalar == typeof(DateTimeOffset) || scalar == typeof(Guid)
            || scalar == typeof(TimeSpan);
    }
}

// Usage
//   db.Employees.OrderBy("Name");
//   db.Employees.OrderBy("Salary desc");
//   db.Employees.OrderBy("JoiningDate");

[ApiController]
public sealed class EmployeesController(AppDbContext db) : ControllerBase
{
    [HttpGet("api/employees")]
    public async Task<IActionResult> List(string? sort, CancellationToken ct)
    {
        try { return Ok(await db.Employees.AsNoTracking().OrderBy(sort).ToListAsync(ct)); }
        catch (InvalidSortException ex) { return Problem(ex.Message, statusCode: 400); }
    }
}

/*
Why it is safe
The input is never copied into SQL. The property name must match a public scalar
property, and EF translates the expression into a safe query.
Unknown properties, navigation paths, indexers, and invalid directions are rejected.

Invalid input
Null or empty: return the query unchanged because no sort was requested.
Unknown property, bad direction or extra tokens: throw InvalidSortException,
returned as 400 Bad Request. Don't silently ignore bad input; it hides client bugs.
*/