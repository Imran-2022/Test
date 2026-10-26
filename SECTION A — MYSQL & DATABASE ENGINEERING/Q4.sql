-- MySQL 8.0+. A half-open date range keeps OrderDate searchable by an index.
SELECT o.Id, o.OrderDate, o.Amount, c.Name, p.Name AS Product
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Products p ON p.Id = o.ProductId
WHERE o.OrderDate >= '2026-06-01' AND o.OrderDate < '2026-07-01'
  AND c.Country = 'BD'
ORDER BY o.OrderDate DESC;

-- Candidate indexes. Keep only the choices supported by the measured plan.
CREATE INDEX idx_customers_country ON Customers(Country);
CREATE INDEX idx_orders_date_customer ON Orders(OrderDate, CustomerId);
-- Customers.Id and Products.Id already have primary-key indexes. If the
-- optimizer starts with the filtered customers, test (CustomerId, OrderDate)
-- as an alternative; do not add both order indexes without evidence.

EXPLAIN ANALYZE
SELECT o.Id, o.OrderDate, o.Amount, c.Name, p.Name AS Product
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Products p ON p.Id = o.ProductId
WHERE o.OrderDate >= '2026-06-01' AND o.OrderDate < '2026-07-01'
  AND c.Country = 'BD'
ORDER BY o.OrderDate DESC;
