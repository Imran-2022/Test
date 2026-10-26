-- Seven Consecutive Login Days

CREATE TABLE UserLogin(UserId INT, LoginDate DATETIME);

INSERT INTO UserLogin VALUES
(1,'2026-06-01 09:00'),(1,'2026-06-01 18:00'),(1,'2026-06-02 10:00'),(1,'2026-06-03 10:00'),
(1,'2026-06-04 10:00'),(1,'2026-06-05 10:00'),(1,'2026-06-06 10:00'),(1,'2026-06-07 10:00'),
(2,'2026-06-01 10:00'),(2,'2026-06-03 10:00');

WITH d AS (SELECT DISTINCT UserId, DATE(LoginDate) AS D FROM UserLogin),   -- removes duplicates
g AS (SELECT UserId, D,
        DATE_SUB(D, INTERVAL ROW_NUMBER() OVER (PARTITION BY UserId ORDER BY D) DAY) AS grp
      FROM d)
SELECT UserId, MIN(D) AS StartDate, MAX(D) AS EndDate
FROM g GROUP BY UserId, grp
HAVING COUNT(*) >= 7;

-- Overlap: this returns one row per maximal streak, so there are no overlapping periods. A 10-day streak gives one row (day 1 to day 10).
-- If you want every overlapping 7-day window instead (a 10-day streak gives 4 rows), use this:

WITH d AS (SELECT DISTINCT UserId, DATE(LoginDate) AS D FROM UserLogin)
SELECT UserId, D AS StartDate, DATE_ADD(D, INTERVAL 6 DAY) AS EndDate
FROM (SELECT UserId, D, LEAD(D,6) OVER (PARTITION BY UserId ORDER BY D) AS l6 FROM d) x
WHERE DATEDIFF(l6, D) = 6;