-- Index-Friendly Query Optimization

CREATE TABLE Customers(Id INT PRIMARY KEY, Name VARCHAR(50), Country CHAR(2));
CREATE TABLE Products(Id INT PRIMARY KEY, Name VARCHAR(50));
CREATE TABLE Orders(Id INT PRIMARY KEY, CustomerId INT, ProductId INT, OrderDate DATETIME, Amount DECIMAL(10,2));

SELECT o.Id, o.OrderDate, o.Amount, c.Name, p.Name AS Product   -- list only needed columns, not *
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Products  p ON p.Id = o.ProductId
WHERE o.OrderDate >= '2026-06-01' AND o.OrderDate < '2026-07-01'
  AND c.Country = 'BD'
ORDER BY o.OrderDate DESC;

CREATE INDEX idx_cust_country   ON Customers(Country, Id);
CREATE INDEX idx_ord_cust_date  ON Orders(CustomerId, OrderDate);   -- plan: customers first, then orders
CREATE INDEX idx_ord_date_cust  ON Orders(OrderDate, CustomerId);   -- plan: date range first, also helps ORDER BY
-- Products.Id is already the primary key.

