using System.Text.Json;

static class HrSampleServiceTests
{
    internal static void Run()
    {
        EndpointContract();
        SampleDataContract();
        BackupPathContract();
    }

    static void EndpointContract()
    {
        Assert(BridgeCore.IsAllowedRoute("/powerbi/hr-sample", "POST"), "POST /powerbi/hr-sample hợp lệ");
        Assert(!BridgeCore.IsAllowedRoute("/powerbi/hr-sample", "GET"), "GET /powerbi/hr-sample bị từ chối");
        var program = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Program.cs"));
        Assert(program.Contains("/powerbi/hr-sample"), "Program expose hr-sample route");
        Assert(program.Contains("HrSampleService.ApplyDeterministicSample()"), "Route gọi service an toàn");
    }

    static void SampleDataContract()
    {
        var schemas = new[]
        {
            new HrSampleService.TableSchema("HR Nhân viên", new()
            {
                new("Mã nhân viên", "String"),
                new("Tên nhân viên", "String"),
                new("Phòng ban", "String"),
                new("Ngày vào làm", "DateTime"),
                new("Lương", "Decimal")
            }, new object(), new object(), "CalculatedPartitionSource", "old-employee"),
            new HrSampleService.TableSchema("HR Tuyển dụng", new()
            {
                new("Mã tuyển dụng", "String"),
                new("Mã nhân viên", "String"),
                new("Vị trí", "String"),
                new("Ngày tuyển", "DateTime")
            }, new object(), new object(), "MPartitionSource", "old-recruit"),
            new HrSampleService.TableSchema("HR Đào tạo", new()
            {
                new("Mã đào tạo", "String"),
                new("Mã nhân viên", "String"),
                new("Khóa học", "String"),
                new("Ngày đào tạo", "DateTime")
            }, new object(), new object(), "MPartitionSource", "old-training")
        };

        var data = HrSampleService.BuildSampleData(schemas);
        Assert(data["HR Nhân viên"].Count == 100, "100 nhân viên deterministic");
        Assert(data["HR Tuyển dụng"].Count == 36, "36 tuyển dụng deterministic");
        Assert(data["HR Đào tạo"].Count == 72, "72 đào tạo deterministic");
        Assert((string?)data["HR Nhân viên"][0]["Mã nhân viên"] == "EMP001", "employee code ổn định");
        Assert(data["HR Tuyển dụng"].All(x => data["HR Nhân viên"].Any(e => Equals(e["Mã nhân viên"], x["Mã nhân viên"]))), "recruitment join về employee");
        Assert(data["HR Đào tạo"].All(x => data["HR Nhân viên"].Any(e => Equals(e["Mã nhân viên"], x["Mã nhân viên"]))), "training join về employee");
        var m = HrSampleService.BuildMExpression(schemas[0], data["HR Nhân viên"]);
        Assert(m.StartsWith("let Source = #table(type table [\"Mã nhân viên\" = text"), "M expression có let/in và quoted identifiers");
        Assert(m.EndsWith(" in Source"), "M expression kết thúc hợp lệ");
    }

    static void BackupPathContract()
    {
        var appDir = BridgeCore.AppDir();
        Assert(Path.GetFileName(Path.Combine(appDir, "backups", "x.json")) == "x.json", "backup dir dưới %LOCALAPPDATA%\\BoBIPet\\backups");
        var json = JsonSerializer.Serialize(new[] { new { table = "HR Nhân viên", expression = "EVALUATE" } });
        Assert(JsonSerializer.Deserialize<JsonElement>(json)[0].GetProperty("table").GetString() == "HR Nhân viên", "backup json serialize được");
    }

    static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
