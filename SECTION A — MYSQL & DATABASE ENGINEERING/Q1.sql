-- Top 3 Highest-Paid Employees per Department

create database AdPlay;
use AdPlay;
CREATE TABLE Employee(Id INT PRIMARY KEY, Name VARCHAR(50), DepartmentId INT, Salary DECIMAL(10,2), JoiningDate DATE);

INSERT INTO Employee VALUES
(1,'A',1,100,'2020-01-01'),(2,'B',1,100,'2021-01-01'),(3,'C',1,90,'2019-01-01'),
(4,'D',1,90,'2022-01-01'),(5,'E',1,80,'2018-01-01'),(6,'F',2,70,'2020-05-01');

SELECT Id, Name, DepartmentId, Salary
FROM (SELECT e.*, DENSE_RANK() OVER (PARTITION BY DepartmentId ORDER BY Salary DESC) rnk
      FROM Employee e) t
WHERE rnk <= 3;

