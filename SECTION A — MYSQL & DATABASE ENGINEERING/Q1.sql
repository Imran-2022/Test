-- MySQL 8.0+. Include everyone whose salary falls in the top three salary
-- levels for their department. Tied salaries receive the same rank.
SELECT Id, Name, DepartmentId, Salary
FROM (SELECT e.*, DENSE_RANK() OVER (PARTITION BY DepartmentId ORDER BY Salary DESC) rnk
      FROM Employee e) t
WHERE rnk <= 3;

-- To return at most three employees per department instead, use ROW_NUMBER
-- with a stable tie-breaker. It returns exactly three only when at least three
-- employees are in that department.
WITH ranked AS (
    SELECT Id, Name, DepartmentId, Salary,
           ROW_NUMBER() OVER (
               PARTITION BY DepartmentId
               ORDER BY Salary DESC, Id
           ) AS rn
    FROM Employee
)
SELECT Id, Name, DepartmentId, Salary
FROM ranked
WHERE rn <= 3;
