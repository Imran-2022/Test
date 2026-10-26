// Department Salary Statistics

public record Employee(int Id, string Name, int DepartmentId, decimal Salary);

var emps = new List<Employee> {
    new(1,"A",1,100), new(2,"B",1,300), new(3,"C",1,200), new(4,"D",1,400),
    new(5,"E",2,500), new(6,"F",2,700), new(7,"G",2,600) };
var departmentIds = new[] { 1, 2, 3 };        // 3 has no employees

static decimal Median(decimal[] sorted)       // expects an already sorted, non-empty array
{
    int n = sorted.Length, mid = n / 2;
    return n % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
}

var stats = emps
    .GroupBy(e => e.DepartmentId)
    .Select(g =>
    {
        var s = g.Select(e => e.Salary).OrderBy(x => x).ToArray();   // sort once per department
        return new {
            DepartmentId = g.Key,
            Highest = s[^1],                  // last element
            Lowest  = s[0],                   // first element
            Average = s.Average(),
            Median  = Median(s) };
    })
    .ToList();

// Include empty departments: look up by id, or use a left join against the department list
var lookup = stats.ToDictionary(x => x.DepartmentId);
foreach (var d in departmentIds)
    Console.WriteLine(lookup.TryGetValue(d, out var r)
        ? $"{d}: max {r.Highest}, min {r.Lowest}, avg {r.Average}, median {r.Median}"
        : $"{d}: no employees");


/*

Output for department 1 is max 400, min 100, avg 250, median 250.

Even count: the median is the average of the two middle values. For 100, 200, 300, 400 it is (200 + 300) / 2 = 250. An odd count uses the single middle value.
Empty department: GroupBy on employees never produces a group for it, so s[0] and Median are never called on an empty array. If a department list exists, report it as “no employees” or return nullable values. Calling Max, Min or Average on an empty decimal sequence would throw.


Complexity:
One pass groups the employees in O(n).
Each department is sorted once, giving O(k log k) per department and O(n log n) overall in the worst case.
Min, max and median then come from the sorted array in O(1), and the average is one O(k) pass.
Sorting once avoids repeated OrderBy calls for min, max and median.
If only the median were needed on large data, quickselect would give O(n) average.