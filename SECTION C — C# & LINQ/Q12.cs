
public record Customer(int Id, string Name);
public record Order(int Id, int CustomerId, DateTime OrderDate, decimal Amount);

var customers = new List<Customer> { new(1,"Rahim"), new(2,"John"), new(3,"Salma") };
var orders = new List<Order> {
    new(1,1,new(2026,6,1),100), new(2,1,new(2026,6,5),300),
    new(3,2,new(2026,6,2),50) };

// Orders only: customers with no orders do not appear
var summary = orders
    .GroupBy(o => o.CustomerId)
    .Select(g => new {
        CustomerId         = g.Key,
        TotalOrders        = g.Count(),
        HighestOrderAmount = g.Max(o => o.Amount),
        AverageOrderAmount = g.Average(o => o.Amount),
        LatestOrderDate    = g.Max(o => o.OrderDate) });


// Customers with no orders. GroupBy on orders never sees them. If every customer must appear, left join from the customers collection:

var all = customers
    .GroupJoin(orders, c => c.Id, o => o.CustomerId, (c, os) => new { c, os = os.ToList() })
    .Select(x => new {
        CustomerId         = x.c.Id,
        TotalOrders        = x.os.Count,
        HighestOrderAmount = x.os.Count > 0 ? x.os.Max(o => o.Amount) : (decimal?)null,
        AverageOrderAmount = x.os.Count > 0 ? x.os.Average(o => o.Amount) : (decimal?)null,
        LatestOrderDate    = x.os.Count > 0 ? x.os.Max(o => o.OrderDate) : (DateTime?)null });


// Customers with no orders get TotalOrders = 0 and null for the other values. The Count > 0 check is needed because Max and Average on an empty decimal sequence throw InvalidOperationException. Orders whose CustomerId matches no customer are dropped by this version. Report them separately if that matters.