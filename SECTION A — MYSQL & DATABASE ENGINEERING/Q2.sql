-- MySQL 8.0+. Return one row for each maximal streak of at least seven days.
-- DISTINCT removes multiple logins on the same calendar day.
WITH distinct_days AS (
    SELECT DISTINCT UserId, DATE(LoginDate) AS LoginDay
    FROM UserLogin
),
streaks AS (
    SELECT UserId, LoginDay,
           DATE_SUB(
               LoginDay,
               INTERVAL ROW_NUMBER() OVER (
                   PARTITION BY UserId ORDER BY LoginDay
               ) DAY
           ) AS StreakGroup
    FROM distinct_days
)
SELECT UserId, MIN(LoginDay) AS StartDate, MAX(LoginDay) AS EndDate
FROM streaks
GROUP BY UserId, StreakGroup
HAVING COUNT(*) >= 7;

-- The query above returns maximal streaks, not overlapping windows. To return
-- every seven-day window inside a streak, use this query instead.
WITH distinct_days AS (
    SELECT DISTINCT UserId, DATE(LoginDate) AS LoginDay
    FROM UserLogin
)
SELECT UserId, LoginDay AS StartDate, DATE_ADD(LoginDay, INTERVAL 6 DAY) AS EndDate
FROM (
    SELECT UserId, LoginDay,
           LEAD(LoginDay, 6) OVER (
               PARTITION BY UserId ORDER BY LoginDay
           ) AS SeventhDay
    FROM distinct_days
) AS days
WHERE DATEDIFF(SeventhDay, LoginDay) = 6;