static class PbipServiceTests
{
    public static void Run()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pbip-test-" + Guid.NewGuid().ToString("N"));
        var reportPages = Path.Combine(tmp, "MyReport", "Report", "pages");
        var page1 = Path.Combine(reportPages, "Page1");
        Directory.CreateDirectory(page1);
        File.WriteAllText(Path.Combine(page1, "page.json"), "{\"displayName\":\"4.2 Số giờ đào tạo\"}");
        var pbip = Path.Combine(tmp, "MyReport", "MyReport.pbip");
        File.WriteAllText(pbip, "{}");

        var projects = PbipService.FindProjects(force: true, roots: new[] { tmp });
        ContractTests.Assert(projects.Count == 1 && projects[0] == pbip, "FindProjects tìm thấy .pbip");
        var pages = PbipService.ListPages(pbip);
        ContractTests.Assert(pages.Count == 1 && pages[0].displayName == "4.2 Số giờ đào tạo", "ListPages đọc displayName");
        var pageJson = pages[0].path;
        ContractTests.Assert(PbipService.IsKnownPagePath(pageJson), "page.json trong project được chấp nhận");
        ContractTests.Assert(!PbipService.IsKnownPagePath(Path.Combine(tmp, "evil", "page.json")), "page.json ngoài project bị chặn");
        var r = PbipService.ReadPage(pageJson);
        ContractTests.Assert(r.ok && r.content.Contains("displayName"), "ReadPage đọc nội dung");
        var w = PbipService.WritePage(pageJson, "{\"displayName\":\"Đã sửa\"}");
        ContractTests.Assert(w.ok && w.backupPath != null && File.Exists(w.backupPath), "WritePage backup + ghi");
        var r2 = PbipService.ReadPage(pageJson);
        ContractTests.Assert(r2.ok && r2.content.Contains("Đã sửa"), "WritePage thay đổi nội dung");
        try { Directory.Delete(tmp, true); } catch { }
    }
}
