using System;
using System.Collections.Generic;
using System.Linq;

public record Employee(int Id, string Name, int DepartmentId, decimal Salary);
public record DepartmentSalaryStats(
    int DepartmentId,
    int EmployeeCount,
    decimal? HighestSalary,
    decimal? LowestSalary,
    decimal? AverageSalary,
    decimal? MedianSalary);

public static class Program
{
    public static void Main()
    {
        var employees = new List<Employee>
        {
            new(1, "A", 1, 100),
            new(2, "B", 1, 300),
            new(3, "C", 1, 200),
            new(4, "D", 1, 400),
            new(5, "E", 2, 500),
            new(6, "F", 2, 700),
            new(7, "G", 2, 600)
        };

        // Include department IDs even when a department has no employees.
        var departmentIds = new[] { 1, 2, 3 };
        var statsByDepartment = employees
            .GroupBy(employee => employee.DepartmentId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var salaries = group
                        .Select(employee => employee.Salary)
                        .OrderBy(salary => salary)
                        .ToArray();

                    return new DepartmentSalaryStats(
                        group.Key,
                        salaries.Length,
                        salaries[^1],
                        salaries[0],
                        salaries.Average(),
                        Median(salaries));
                });

        var stats = departmentIds
            .Select(id => statsByDepartment.TryGetValue(id, out var departmentStats)
                ? departmentStats
                : new DepartmentSalaryStats(id, 0, null, null, null, null))
            .ToList();

        _ = stats;
    }

    static decimal Median(decimal[] sortedSalaries)
    {
        int middle = sortedSalaries.Length / 2;
        return sortedSalaries.Length % 2 == 1
            ? sortedSalaries[middle]
            : (sortedSalaries[middle - 1] + sortedSalaries[middle]) / 2m;
    }
}

// An even-sized group's median is the average of its two middle salaries.
// An empty department has count zero and null salary statistics.
// Grouping is O(n); sorting once per group is O(n log n) worst case.
