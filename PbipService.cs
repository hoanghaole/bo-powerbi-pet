using System.Text;
using System.Text.Json;

internal static class PbipService
{
    internal const int MaxBodyBytes = 262_144;
    internal const int MaxPathChars = 1024;

    private static readonly object CacheLock = new();
    private static List<string>? _projectsCache;
    private static DateTime _projectsCacheAt;

    internal static List<string> FindProjects(bool force = false, string[]? roots = null)
    {
        lock (CacheLock)
        {
            if (!force && _projectsCache != null && (DateTime.UtcNow - _projectsCacheAt).TotalMinutes < 5)
                return _projectsCache;
        }
        if (roots == null)
        {
            var rootList = new List<string>();
            var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var sub in new[] { "Documents", "Desktop", "Downloads", "OneDrive" })
            {
                var p = Path.Combine(up, sub);
                if (Directory.Exists(p)) rootList.Add(p);
            }
            foreach (var drive in new[] { "D:\\", "E:\\" })
                if (Directory.Exists(drive)) rootList.Add(drive);
            roots = rootList.ToArray();
        }

        var found = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.pbip", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 8,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    if (f.Contains("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(Path.GetFullPath(f));
                }
            }
            catch { }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        lock (CacheLock)
        {
            _projectsCache = found;
            _projectsCacheAt = DateTime.UtcNow;
        }
        return found;
    }

    internal sealed record PageInfo(string name, string displayName, string path);

    internal static List<PageInfo> ListPages(string projectPath)
    {
        var full = SafeFull(projectPath);
        var projectDir = Path.GetDirectoryName(full)!;
        var pages = new List<PageInfo>();
        foreach (var reportDir in new[] { Path.Combine(projectDir, "Report", "pages"), Path.Combine(projectDir, "Report", "definition", "pages") })
        {
            if (!Directory.Exists(reportDir)) continue;
            foreach (var dir in Directory.EnumerateDirectories(reportDir))
            {
                var pageJson = Path.Combine(dir, "page.json");
                if (!File.Exists(pageJson)) continue;
                string displayName = Path.GetFileName(dir);
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pageJson));
                    if (doc.RootElement.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                        displayName = dn.GetString()!;
                }
                catch { }
                pages.Add(new PageInfo(Path.GetFileName(dir), displayName, pageJson));
            }
        }
        pages.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
        return pages;
    }

    internal static bool IsKnownPagePath(string pagePath)
    {
        var full = SafeFull(pagePath);
        if (!full.EndsWith("page.json", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var proj in FindProjects())
        {
            var projDir = Path.GetDirectoryName(Path.GetFullPath(proj))!;
            foreach (var pagesRoot in new[] { Path.Combine(projDir, "Report", "pages"), Path.Combine(projDir, "Report", "definition", "pages") })
            {
                if (full.StartsWith(pagesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    internal static (bool ok, string content, string? error) ReadPage(string pagePath)
    {
        var full = SafeFull(pagePath);
        if (!IsKnownPagePath(full)) return (false, "", "page_path_outside_known_project");
        if (!File.Exists(full)) return (false, "", "page_not_found");
        try { return (true, File.ReadAllText(full, Encoding.UTF8), null); }
        catch (Exception ex) { return (false, "", ex.Message); }
    }

    internal static (bool ok, string? backupPath, string? error) WritePage(string pagePath, string content)
    {
        var full = SafeFull(pagePath);
        if (!IsKnownPagePath(full)) return (false, null, "page_path_outside_known_project");
        if (!File.Exists(full)) return (false, null, "page_not_found");
        var backup = full + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            File.Copy(full, backup, overwrite: false);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            return (true, backup, null);
        }
        catch (Exception ex)
        {
            return (false, backup, ex.Message);
        }
    }

    private static string SafeFull(string p)
    {
        if (string.IsNullOrWhiteSpace(p) || p.Length > MaxPathChars) throw new InvalidOperationException("invalid_path");
        return Path.GetFullPath(p);
    }

    internal static string Json(object x) => JsonSerializer.Serialize(x);
}
