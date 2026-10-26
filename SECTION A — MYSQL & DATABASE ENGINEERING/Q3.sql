-- MySQL 8.0+. Id makes the ordering deterministic when timestamps are equal.
SELECT Id, CustomerId, OrderDate, Amount,
       LAG(Amount) OVER w AS PreviousAmount,
       Amount - LAG(Amount) OVER w AS DifferenceFromPrevious,
       SUM(Amount) OVER (
           w ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
       ) AS RunningTotal
FROM Orders
WINDOW w AS (PARTITION BY CustomerId ORDER BY OrderDate, Id);

-- LAG is NULL for a customer's first order. ROWS advances the running total
-- one order at a time, including when timestamps are tied.