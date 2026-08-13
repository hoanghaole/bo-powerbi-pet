using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

internal static class HrSampleService
{
    internal sealed record SampleResult(int Employees, int Recruitments, int Trainings, int Port, string BackupPath);
    internal sealed record TableContract(string Name, string[] RequiredColumns);
    internal sealed record TableSchema(string Name, List<ColumnSchema> Columns, object Table, object Partition, string SourceType, string ExistingExpression);
    internal sealed record ColumnSchema(string Name, string DataType);

    static readonly TableContract[] Contracts =
    [
        new("HR Nhân viên", ["Mã nhân viên"]),
        new("HR Tuyển dụng", ["Mã nhân viên"]),
        new("HR Đào tạo", ["Mã nhân viên"])
    ];

    internal static SampleResult ApplyDeterministicSample()
    {
        _ = FindPowerBiDesktopBin();
        var asm = Assembly.Load("Microsoft.AnalysisServices.Tabular");
        dynamic server = Activator.CreateInstance(asm.GetType("Microsoft.AnalysisServices.Tabular.Server", true)!)!;
        try
        {
            var port = FindMsmdsrvPort();
            server.Connect($"DataSource=localhost:{port}");
            var db = FirstOrThrow((System.Collections.IEnumerable)server.Databases, "Không tìm thấy database Power BI Desktop");
            var model = db.GetType().GetProperty("Model")!.GetValue(db)!;
            var tablesCollection = model.GetType().GetProperty("Tables")!.GetValue(model)!;

            var schemas = new List<TableSchema>();
            foreach (var contract in Contracts)
            {
                var table = FindTable(tablesCollection, contract.Name) ?? throw new InvalidOperationException($"Thiếu table bắt buộc: {contract.Name}");
                var schema = ReadSchema(table);
                EnsureContract(schema, contract);
                schemas.Add(schema);
            }

            var data = BuildSampleData(schemas);
            var backupPath = BackupExpressions(schemas);
            foreach (var schema in schemas)
            {
                SetSourceExpression(schema, data[schema.Name]);
            }

            model.GetType().GetMethod("SaveChanges", Type.EmptyTypes)!.Invoke(model, null);
            return new SampleResult(data["HR Nhân viên"].Count, data["HR Tuyển dụng"].Count, data["HR Đào tạo"].Count, port, backupPath);
        }
        finally
        {
            try { server.Disconnect(); } catch { }
        }
    }

    internal static Dictionary<string, List<Dictionary<string, object?>>> BuildSampleData(IReadOnlyList<TableSchema> schemas)
    {
        var byName = schemas.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var employees = BuildEmployees(byName["HR Nhân viên"]);
        var recruitments = BuildRelated(byName["HR Tuyển dụng"], employees, "recruitment");
        var trainings = BuildRelated(byName["HR Đào tạo"], employees, "training");
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["HR Nhân viên"] = employees,
            ["HR Tuyển dụng"] = recruitments,
            ["HR Đào tạo"] = trainings
        };
    }

    static List<Dictionary<string, object?>> BuildEmployees(TableSchema schema)
    {
        var rows = new List<Dictionary<string, object?>>(100);
        for (var i = 1; i <= 100; i++)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in schema.Columns)
                row[col.Name] = EmployeeValue(col, i);
            rows.Add(row);
        }
        return rows;
    }

    static List<Dictionary<string, object?>> BuildRelated(TableSchema schema, List<Dictionary<string, object?>> employees, string kind)
    {
        var rows = new List<Dictionary<string, object?>>();
        var total = kind == "recruitment" ? 36 : 72;
        for (var i = 1; i <= total; i++)
        {
            var employee = employees[(i - 1) % employees.Count];
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in schema.Columns)
                row[col.Name] = RelatedValue(col, i, employee, kind);
            rows.Add(row);
        }
        return rows;
    }

    static object? EmployeeValue(ColumnSchema col, int i)
    {
        var n = Normalize(col.Name);
        if (ContainsAny(n, "mãnhânviên", "manhanvien", "employeeid", "employeecode", "nhanvienid", "staffid", "staffcode")) return $"EMP{i:000}";
        if (ContainsAny(n, "tênnhânviên", "hoten", "họtên", "ten", "fullname", "employeename", "staffname", "name")) return $"Nhân viên {i:000}";
        if (ContainsAny(n, "phòngban", "đơnvị", "donvi", "department", "bophan", "team")) return new[] { "Kinh doanh", "Marketing", "Vận hành", "Tài chính", "Công nghệ" }[(i - 1) % 5];
        if (ContainsAny(n, "chucdanh", "vitri", "title", "position", "job")) return new[] { "Nhân viên", "Chuyên viên", "Trưởng nhóm", "Quản lý" }[(i - 1) % 4];
        if (ContainsAny(n, "capquanly", "manager", "quanly")) return i <= 10 ? "Ban điều hành" : $"Quản lý {((i - 1) % 10) + 1:00}";
        if (ContainsAny(n, "gioitinh", "gender", "sex")) return i % 2 == 0 ? "Nữ" : "Nam";
        if (ContainsAny(n, "trangthai", "status", "active")) return i % 12 == 0 ? "Tạm nghỉ" : "Đang làm việc";
        if (ContainsAny(n, "diachi", "address", "thanhpho", "city", "location", "noilamviec")) return new[] { "Hà Nội", "TP.HCM", "Đà Nẵng", "Cần Thơ" }[(i - 1) % 4];
        if (ContainsAny(n, "email", "mail")) return $"emp{i:000}@example.com";
        if (ContainsAny(n, "dienthoai", "phone", "mobile", "sdt")) return $"090{(100000 + i):000000}";
        if (ContainsAny(n, "ngaysinh", "birth", "dob")) return new DateTime(1985, 1, 1).AddDays(i * 97 % 7000);
        if (ContainsAny(n, "ngàynhậnviệc", "ngaynhanviec", "ngayvaolam", "hiredate", "joiningdate", "startdate", "onboard")) return new DateTime(2019, 1, 1).AddDays(i * 11);
        if (ContainsAny(n, "ngàynghỉviệc", "ngaynghiviec", "terminationdate", "leavedate", "enddate")) return i % 12 == 0 ? new DateTime(2025, 1, 1).AddDays(i * 3) : null;
        if (ContainsAny(n, "luong", "salary", "thuNhap", "thunhap", "allowance", "bonus")) return 9_000_000m + i * 175_000m;
        if (ContainsAny(n, "tuoi", "age")) return 22 + (i % 23);
        return DefaultValue(col, i, "EMP");
    }

    static object? RelatedValue(ColumnSchema col, int i, Dictionary<string, object?> employee, string kind)
    {
        var n = Normalize(col.Name);
        if (ContainsAny(n, "mãnhânviên", "manhanvien", "employeeid", "employeecode", "nhanvienid", "staffid", "staffcode"))
            return employee.FirstOrDefault(kv => ContainsAny(Normalize(kv.Key), "mãnhânviên", "manhanvien", "employeeid", "employeecode", "nhanvienid", "staffid", "staffcode")).Value ?? $"EMP{((i - 1) % 100) + 1:000}";
        if (ContainsAny(n, "tênnhânviên", "hoten", "họtên", "fullname", "employeename", "staffname", "name") && !ContainsAny(n, "khoahoc", "course", "chuongtrinh", "program"))
            return employee.FirstOrDefault(kv => ContainsAny(Normalize(kv.Key), "tênnhânviên", "hoten", "họtên", "fullname", "employeename", "staffname", "name")).Value ?? $"Nhân viên {((i - 1) % 100) + 1:000}";
        if (ContainsAny(n, "mã", "id", "code") && !ContainsAny(n, "mãnhânviên", "manhanvien", "employeeid", "staffid"))
            return (kind == "recruitment" ? "REC" : "TRN") + i.ToString("000", CultureInfo.InvariantCulture);
        if (kind == "recruitment")
        {
            if (ContainsAny(n, "vitri", "position", "job", "chucdanh")) return new[] { "Thực tập sinh", "Nhân viên bán hàng", "Chuyên viên phân tích", "Kỹ sư dữ liệu" }[(i - 1) % 4];
            if (ContainsAny(n, "kenh", "source", "nguon")) return new[] { "Referral", "LinkedIn", "TopCV", "Career Page" }[(i - 1) % 4];
            if (ContainsAny(n, "trangthai", "status", "ketqua")) return new[] { "Đạt", "Đang xử lý", "Không đạt" }[(i - 1) % 3];
            if (ContainsAny(n, "ngay", "date")) return new DateTime(2024, 1, 1).AddDays(i * 5);
            if (ContainsAny(n, "chiphi", "cost", "phi")) return 1_000_000m + i * 125_000m;
            if (ContainsAny(n, "soluong", "count", "qty", "quantity")) return 1 + (i % 3);
        }
        else
        {
            if (ContainsAny(n, "khoahoc", "course", "chuongtrinh", "program", "module")) return new[] { "Onboarding", "Power BI", "Excel nâng cao", "Kỹ năng quản lý", "An toàn lao động", "Bán hàng" }[(i - 1) % 6];
            if (ContainsAny(n, "hinhthuc", "format", "type")) return i % 2 == 0 ? "Online" : "Offline";
            if (ContainsAny(n, "ketqua", "status", "completion", "trangthai")) return i % 5 == 0 ? "Đang học" : "Hoàn thành";
            if (ContainsAny(n, "ngay", "date")) return new DateTime(2024, 2, 1).AddDays(i * 3);
            if (ContainsAny(n, "gio", "hour", "duration")) return 2 + (i % 6);
            if (ContainsAny(n, "chiphi", "cost", "phi")) return 500_000m + i * 90_000m;
        }
        return DefaultValue(col, i, kind == "recruitment" ? "REC" : "TRN");
    }

    static object? DefaultValue(ColumnSchema col, int i, string prefix)
    {
        return col.DataType switch
        {
            "String" => $"{prefix}-{Slug(col.Name)}-{i:000}",
            "Int64" => i,
            "Decimal" => decimal.Round(i * 1.25m, 2),
            "Double" => i * 1.5,
            "Boolean" => i % 2 == 0,
            "DateTime" => new DateTime(2024, 1, 1).AddDays(i),
            _ => $"{prefix}-{i:000}"
        };
    }

    static string BuildExpression(TableSchema schema, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DATATABLE (");
        for (var i = 0; i < schema.Columns.Count; i++)
        {
            var col = schema.Columns[i];
            sb.Append("    \"").Append(EscapeDax(col.Name)).Append("\", ").Append(col.DataType);
            sb.AppendLine(i == schema.Columns.Count - 1 ? "," : ",");
        }
        sb.AppendLine("    {");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            sb.Append("        { ");
            for (var colIndex = 0; colIndex < schema.Columns.Count; colIndex++)
            {
                var col = schema.Columns[colIndex];
                sb.Append(ToDaxLiteral(row[col.Name], col.DataType));
                if (colIndex < schema.Columns.Count - 1) sb.Append(", ");
            }
            sb.Append(" }");
            if (rowIndex < rows.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.AppendLine("    }");
        sb.Append(')');
        return sb.ToString();
    }

    internal static string BuildMExpression(TableSchema schema, List<Dictionary<string, object?>> rows)
    {
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        static string MType(string type) => type switch
        {
            "Int64" => "Int64.Type", "Decimal" => "type number", "Double" => "type number",
            "Boolean" => "type logical", "DateTime" => "type datetime", _ => "type text"
        };
        static string MValue(object? value, string type) => value switch
        {
            null => "null",
            DateTime dt => $"#datetime({dt.Year},{dt.Month},{dt.Day},{dt.Hour},{dt.Minute},{dt.Second})",
            bool b => b ? "true" : "false",
            string x => Q(x),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
        };
        var type = "type table [" + string.Join(", ", schema.Columns.Select(c => "#" + Q(c.Name) + " = " + MType(c.DataType))) + "]";
        var data = "{" + string.Join(",", rows.Select(row => "{" + string.Join(",", schema.Columns.Select(c => MValue(row[c.Name], c.DataType))) + "}")) + "}";
        return $"let Source = #table({type}, {data}) in Source";
    }

    static string BackupExpressions(IEnumerable<TableSchema> schemas)
    {
        var backupDir = Path.Combine(BridgeCore.AppDir(), "backups");
        Directory.CreateDirectory(backupDir);
        var path = Path.Combine(backupDir, $"hr-sample-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var payload = schemas.Select(x => new { table = x.Name, sourceType = x.SourceType, expression = x.ExistingExpression, backedUpAt = DateTimeOffset.Now }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true }), new UTF8Encoding(false));
        return path;
    }

    static void EnsureContract(TableSchema schema, TableContract contract)
    {
        if (schema.Columns.Count == 0) throw new InvalidOperationException($"Table {schema.Name} không có column");
        foreach (var column in contract.RequiredColumns)
            if (!schema.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Table {schema.Name} thiếu column bắt buộc: {column}");
    }

    static TableSchema ReadSchema(object table)
    {
        var tableType = table.GetType();
        var name = (string)tableType.GetProperty("Name")!.GetValue(table)!;
        var columnsObj = tableType.GetProperty("Columns")!.GetValue(table)!;
        var columns = new List<ColumnSchema>();
        foreach (var col in (System.Collections.IEnumerable)columnsObj)
        {
            var colType = col.GetType();
            if (colType.GetProperty("Type")?.GetValue(col)?.ToString() == "RowNumber") continue;
            var colName = (string)colType.GetProperty("Name")!.GetValue(col)!;
            var dataType = colType.GetProperty("DataType")!.GetValue(col)!.ToString()!;
            columns.Add(new ColumnSchema(colName, dataType));
        }

        var partitionsObj = tableType.GetProperty("Partitions")!.GetValue(table)!;
        var partition = FirstOrThrow((System.Collections.IEnumerable)partitionsObj, $"Table {name} không có partition");
        var source = partition.GetType().GetProperty("Source")!.GetValue(partition) ?? throw new InvalidOperationException($"Table {name} không có Source");
        var sourceType = source.GetType().Name;
        if (!sourceType.Contains("CalculatedPartitionSource", StringComparison.Ordinal)
            && !sourceType.Contains("MPartitionSource", StringComparison.Ordinal))
            throw new InvalidOperationException($"Table {name} có partition không hỗ trợ: {sourceType}");
        var expression = source.GetType().GetProperty("Expression")!.GetValue(source)?.ToString() ?? "";
        return new TableSchema(name, columns, table, partition, sourceType, expression);
    }

    static void SetSourceExpression(TableSchema schema, List<Dictionary<string, object?>> rows)
    {
        var source = schema.Partition.GetType().GetProperty("Source")!.GetValue(schema.Partition) ?? throw new InvalidOperationException("Partition source null");
        var expression = schema.SourceType.Contains("MPartitionSource", StringComparison.Ordinal)
            ? BuildMExpression(schema, rows)
            : BuildExpression(schema, rows);
        source.GetType().GetProperty("Expression")!.SetValue(source, expression);
    }

    static object? FindTable(object tablesCollection, string tableName)
    {
        foreach (var item in (System.Collections.IEnumerable)tablesCollection)
        {
            var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
            if (string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase)) return item;
        }
        return null;
    }

    static string FindPowerBiDesktopBin()
    {
        foreach (var p in Process.GetProcessesByName("PBIDesktop"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)) return Path.GetDirectoryName(path)!;
            }
            catch { }
        }
        throw new InvalidOperationException("Không tìm thấy tiến trình PBIDesktop");
    }

    static int FindMsmdsrvPort()
    {
        var processIds = Process.GetProcessesByName("msmdsrv").Select(p => p.Id).ToHashSet();
        if (processIds.Count == 0) throw new InvalidOperationException("Không tìm thấy tiến trình msmdsrv");

        try
        {
            using var net = Process.Start(new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            var text = net!.StandardOutput.ReadToEnd();
            net.WaitForExit(3000);
            foreach (var line in text.Split('\n'))
            {
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5 || !int.TryParse(parts[4], out var pid) || !processIds.Contains(pid)) continue;
                if (Uri.TryCreate("tcp://" + parts[1], UriKind.Absolute, out var endpoint)) return endpoint.Port;
            }
        }
        catch { }
        throw new InvalidOperationException("Không tìm thấy cổng listener của msmdsrv");
    }

    static object FirstOrThrow(System.Collections.IEnumerable items, string message)
    {
        foreach (var item in items)
            return item ?? throw new InvalidOperationException(message);
        throw new InvalidOperationException(message);
    }

    static bool ContainsAny(string input, params string[] needles) => needles.Any(input.Contains);
    static string Normalize(string input) => new(input.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
    static string Slug(string input) => Normalize(input) switch { "" => "col", var x => x };
    static string EscapeDax(string input) => input.Replace("\"", "\"\"");

    static string ToDaxLiteral(object? value, string dataType)
    {
        if (value is null) return "BLANK()";
        return dataType switch
        {
            "String" => $"\"{EscapeDax(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}\"",
            "Int64" => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            "Decimal" => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            "Double" => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            "Boolean" => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "TRUE" : "FALSE",
            "DateTime" => value is DateTime dt
                ? $"DATE({dt.Year},{dt.Month},{dt.Day}) + TIME({dt.Hour},{dt.Minute},{dt.Second})"
                : $"DATE({Convert.ToDateTime(value, CultureInfo.InvariantCulture).Year},{Convert.ToDateTime(value, CultureInfo.InvariantCulture).Month},{Convert.ToDateTime(value, CultureInfo.InvariantCulture).Day})",
            _ => $"\"{EscapeDax(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}\""
        };
    }
}
