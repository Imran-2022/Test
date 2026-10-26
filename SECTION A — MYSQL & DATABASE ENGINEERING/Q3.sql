-- Customer Order Analytics with Window Functions

CREATE TABLE Orders(Id INT PRIMARY KEY, CustomerId INT, OrderDate DATETIME, Amount DECIMAL(10,2));
INSERT INTO Orders VALUES (1,1,'2026-06-01 10:00',100),(2,1,'2026-06-01 10:00',50),
(3,1,'2026-06-05 10:00',70),(4,2,'2026-06-02 10:00',200);

SELECT Id, CustomerId, OrderDate, Amount,
  LAG(Amount) OVER w                AS PrevAmount,
  Amount - LAG(Amount) OVER w       AS DiffFromPrev,
  SUM(Amount) OVER (w ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningTotal
FROM Orders
WINDOW w AS (PARTITION BY CustomerId ORDER BY OrderDate, Id);

-- Id is the tiebreaker, so orders with the same timestamp always come out in the same order. ROWS is needed because the default RANGE frame would give tied rows the same running total.