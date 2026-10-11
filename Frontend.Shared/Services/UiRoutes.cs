namespace Frontend.Shared.Services;

/// <summary>Maps a UI page to its equivalent in the other interface.</summary>
public static class UiRoutes
{
    public static bool IsLegacy(string path) =>
        path.Equals("/legacy", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/legacy/", StringComparison.OrdinalIgnoreCase);

    public static string OtherVersion(string localUrl)
    {
        if (!localUrl.StartsWith('/') || localUrl.StartsWith("//", StringComparison.Ordinal) || localUrl.Contains('\\'))
            throw new ArgumentException("Expected a local absolute UI path.", nameof(localUrl));

        var end = localUrl.IndexOfAny(['?', '#']);
        var path = end < 0 ? localUrl : localUrl[..end];
        var suffix = end < 0 ? string.Empty : localUrl[end..];
        if (IsLegacy(path))
        {
            var target = path[7..];
            return (string.IsNullOrEmpty(target) ? "/" : target) + suffix;
        }

        if (path.TrimEnd('/').Equals("/admin/student-accounts", StringComparison.OrdinalIgnoreCase))
            return "/legacy/admin/grading" + suffix;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && (segments[0] == "admin" || segments[0] == "student") &&
            (segments[1] == "profile" || segments[1] == "profiles" || segments[1] == "achievements"))
            return "/legacy/" + segments[0];

        return (path == "/" ? "/legacy" : "/legacy" + path) + suffix;
    }
}
