
using System;
using System.Collections.Generic;
using System.Linq;

public record Customer(int Id, string Name);
public record Order(int Id, int CustomerId, DateTime OrderDate, decimal Amount);

public static class Program
{
    public static void Main()
    {
        var customers = new List<Customer>
        {
            new(1, "Rahim"), new(2, "John"), new(3, "Salma")
        };
        var orders = new List<Order>
        {
            new(1, 1, new(2026, 6, 1), 100),
            new(2, 1, new(2026, 6, 5), 300),
            new(3, 2, new(2026, 6, 2), 50)
        };

        // This version returns only customers who have orders.
        var summary = orders
            .GroupBy(order => order.CustomerId)
            .Select(group => new
            {
                CustomerId = group.Key,
                TotalOrders = group.Count(),
                HighestOrderAmount = group.Max(order => order.Amount),
                AverageOrderAmount = group.Average(order => order.Amount),
                LatestOrderDate = group.Max(order => order.OrderDate)
            });

        // Start from customers to include those who have no orders.
        var allCustomers = customers
            .GroupJoin(
                orders,
                customer => customer.Id,
                order => order.CustomerId,
                (customer, customerOrders) => new
                {
                    Customer = customer,
                    Orders = customerOrders.ToList()
                })
            .Select(result => new
            {
                CustomerId = result.Customer.Id,
                TotalOrders = result.Orders.Count,
                HighestOrderAmount = result.Orders.Count > 0
                    ? result.Orders.Max(order => order.Amount)
                    : (decimal?)null,
                AverageOrderAmount = result.Orders.Count > 0
                    ? result.Orders.Average(order => order.Amount)
                    : (decimal?)null,
                LatestOrderDate = result.Orders.Count > 0
                    ? result.Orders.Max(order => order.OrderDate)
                    : (DateTime?)null
            });

        // Orders with an unknown CustomerId do not appear in this customer-based result.
        _ = summary;
        _ = allCustomers;
    }
}