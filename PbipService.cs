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
                    MaxRecursionDepth = 16,
                    // KHÔNG đặt AttributesToSkip: mặc định Hidden|System, không skip ReparsePoint
                    // (OneDrive online-only đánh dấu bằng ReparsePoint — skip là mất project)
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

    // PBIP project: file .pbip đứng cạnh folder <Tên>.Report (PBIP mới) hoặc Report/ (cũ)
    // Có thể nhận: path tới file .pbip, hoặc path tới thư mục project
    internal static string? FindReportDir(string projectPath)
    {
        var full = SafeFull(projectPath).TrimEnd('\\', '/');
        string projectDir;
        string baseName;
        if (Directory.Exists(full))
        {
            projectDir = full;
            baseName = Path.GetFileName(full);
            if (baseName.EndsWith(".pbip", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^5];
        }
        else
        {
            projectDir = Path.GetDirectoryName(full) ?? full;
            baseName = Path.GetFileNameWithoutExtension(full);
        }
        foreach (var candidate in new[]
        {
            Path.Combine(projectDir, baseName + ".Report"),   // PBIP mới: <Tên>.Report
            Path.Combine(projectDir, "Report"),                // PBIP cũ: Report
            Path.Combine(full, baseName + ".Report"),          // folder project chứa <Tên>.Report
            Path.Combine(full, "Report")
        })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static List<PageInfo> ListPages(string projectPath)
    {
        var reportDir = FindReportDir(projectPath);
        if (reportDir == null) return new List<PageInfo>();
        var pages = new List<PageInfo>();
        foreach (var pagesRoot in new[] { Path.Combine(reportDir, "definition", "pages"), Path.Combine(reportDir, "pages") })
        {
            if (!Directory.Exists(pagesRoot)) continue;
            foreach (var dir in Directory.EnumerateDirectories(pagesRoot))
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
                catch { continue; }  // page.json rỗng/corrupt → bỏ qua, không hiện tên folder
                pages.Add(new PageInfo(Path.GetFileName(dir), displayName, pageJson));
            }
        }
        // Sắp theo pageOrder từ pagesMetadata.json nếu có (PBIR schema), fallback displayName
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pagesRoot in new[] { Path.Combine(reportDir, "definition", "pages"), Path.Combine(reportDir, "pages") })
        {
            var meta = Path.Combine(pagesRoot, "pagesMetadata.json");
            if (!File.Exists(meta)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                if (doc.RootElement.TryGetProperty("pages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var e in arr.EnumerateArray())
                    {
                        if (e.TryGetProperty("pageId", out var id) && id.ValueKind == JsonValueKind.String)
                            order[id.GetString()!] = i;
                        i++;
                    }
                }
            }
            catch { }
        }
        pages.Sort((a, b) =>
        {
            if (order.TryGetValue(a.name, out var oa) && order.TryGetValue(b.name, out var ob)) return oa.CompareTo(ob);
            if (order.TryGetValue(a.name, out oa)) return -1;
            if (order.TryGetValue(b.name, out ob)) return 1;
            return string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase);
        });
        return pages;
    }

    internal static bool IsKnownPagePath(string pagePath)
    {
        var full = SafeFull(pagePath);
        if (!full.EndsWith("page.json", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var proj in FindProjects())
        {
            var reportDir = FindReportDir(proj);
            if (reportDir == null) continue;
            foreach (var pagesRoot in new[] { Path.Combine(reportDir, "definition", "pages"), Path.Combine(reportDir, "pages") })
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
        // Validate JSON trước khi ghi — chặn content rác làm hỏng project
        try { using var _ = JsonDocument.Parse(content); }
        catch (Exception ex) { return (false, null, $"invalid_json: {ex.Message}"); }
        var backup = full + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            File.Copy(full, backup, overwrite: false);
            // Ghi atomic: temp + rename — crash giữa chừng không làm hỏng file gốc
            var tmp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, full, overwrite: true);
            // Giữ tối đa 5 backup gần nhất
            try
            {
                var old = Directory.GetFiles(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".bak-*")
                    .OrderByDescending(f => f).Skip(5).ToArray();
                foreach (var f in old) File.Delete(f);
            }
            catch { }
            return (true, backup, null);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(full + ".tmp-*")) { } } catch { }
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
