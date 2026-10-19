using FunAndChecks.Domain.Entities;

namespace FunAndChecks.Application.Tasks;

/// <summary>
/// Единый порядок задач во всех списках: лексикографически по названию («1.01» &lt; «1.1» &lt; «1.2»),
/// без учёта регистра; при равных названиях — в порядке создания. Сортируем в коде, а не в БД:
/// сортировка Postgres по локали пропускает точки и знаки и дала бы другой порядок.
/// </summary>
public static class TaskOrder
{
    public static IOrderedEnumerable<T> Apply<T>(IEnumerable<T> tasks, Func<T, string> name, Func<T, int> id) =>
        tasks.OrderBy(name, StringComparer.OrdinalIgnoreCase).ThenBy(name, StringComparer.Ordinal).ThenBy(id);

    public static List<CourseTask> Apply(IEnumerable<CourseTask> tasks) => Apply(tasks, t => t.Name, t => t.Id).ToList();
}
