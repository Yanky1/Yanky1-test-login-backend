namespace Solvelt.Api.Security;

public static class SafeRedirect
{
    /// <summary>Same-origin relative paths only: must start with a single '/', no scheme, host, backslash or control chars.</summary>
    public static string? Sanitize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2000) return null;
        if (path[0] != '/') return null;
        if (path.Length > 1 && (path[1] == '/' || path[1] == '\\')) return null;
        if (path.Any(c => c == '\\' || char.IsControl(c))) return null;
        if (path.Contains("://", StringComparison.Ordinal)) return null;
        return path;
    }
}
