// Dynamic LINQ Sorting

// Setup

CREATE TABLE Employees(Id INT PRIMARY KEY AUTO_INCREMENT, Name VARCHAR(50), Salary DECIMAL(10,2), JoiningDate DATE);
INSERT INTO Employees(Name,Salary,JoiningDate) VALUES ('Rahim',500,'2020-01-10'),('Karim',700,'2019-05-01');

public class Employee { public int Id {get;set;} public string Name {get;set;} = "";
                        public decimal Salary {get;set;} public DateTime JoiningDate {get;set;} }


// Extension
using System.Linq.Expressions;
using System.Reflection;

public class InvalidSortException(string msg) : ArgumentException(msg);

public static class SortExtensions
{
    public static IQueryable<T> OrderBy<T>(this IQueryable<T> source, string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort)) return source;              // nothing requested

        var parts = sort.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2) throw new InvalidSortException("Invalid sort.");

        // validate: must be a real public property, scalar only (no navigation properties)
        var prop = typeof(T).GetProperty(parts[0],
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null || !(prop.PropertyType.IsValueType || prop.PropertyType == typeof(string)))
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
}

// Usage

db.Employees.OrderBy("Name");
db.Employees.OrderBy("Salary desc");
db.Employees.OrderBy("JoiningDate");

[HttpGet("api/employees")]
public async Task<IActionResult> List(string? sort, CancellationToken ct)
{
    try { return Ok(await db.Employees.AsNoTracking().OrderBy(sort).ToListAsync(ct)); }
    catch (InvalidSortException ex) { return Problem(ex.Message, statusCode: 400); }
}

/*

// Why it is safe


The input is never put into SQL. The name is only used to look up a real property, and EF parameterizes the resulting expression tree.
Unknown properties and navigation properties are rejected, so a path like Customer.Password can’t be sorted on.
Only asc and desc are accepted, and the default is ascending.


// Invalid input
Null or empty: return the query unchanged.
Unknown property, bad direction or extra tokens: throw InvalidSortException, returned as 400 Bad Request.
Don’t silently ignore bad input. It hides client bugs.