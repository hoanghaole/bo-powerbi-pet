using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;

static class PowerBiAuthoringServiceTests
{
    internal static void Run()
    {
        RouteContract();
        RequestLimitsContract();
        OperationShapeContract();
        MExpressionContract();
        FingerprintContract();
        BackupRestoreContract();
        BatchSemanticContract();
        RelationshipValidationContract();
        CalculatedTableRelationshipContract();
        RestorePathContract();
    }

    static void CalculatedTableRelationshipContract()
    {
        var fact = new Table { Name = "Fact" };
        fact.Columns.Add(new DataColumn { Name = "Date", DataType = DataType.DateTime });
        var calendar = new Table { Name = "Ngày" };
        calendar.Columns.Add(new CalculatedTableColumn { Name = "Date", DataType = DataType.DateTime });
        var find = typeof(PowerBiAuthoringService).GetMethod("FindRelationshipColumn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert(find.Invoke(null, [fact, "Date"]) is DataColumn, "relationship hỗ trợ data column");
        Assert(find.Invoke(null, [calendar, "Date"]) is CalculatedTableColumn, "relationship hỗ trợ calculated-table column");
        AssertThrows(() => find.Invoke(null, [calendar, "Missing"]), "column_not_found");
    }

    static void RouteContract()
    {
        Assert(BridgeCore.IsAllowedRoute("/v1/powerbi/model/inspect", "POST"), "POST inspect hợp lệ");
        Assert(BridgeCore.IsAllowedRoute("/v1/powerbi/model/operations", "POST"), "POST operations hợp lệ");
        Assert(!BridgeCore.IsAllowedRoute("/v1/powerbi/model/inspect", "GET"), "GET inspect bị từ chối");
        Assert(!BridgeCore.IsAllowedRoute("/v1/powerbi/model/operations", "GET"), "GET operations bị từ chối");
        var program = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Program.cs"));
        Assert(program.Contains("/v1/powerbi/model/inspect"), "Program expose inspect route");
        Assert(program.Contains("/v1/powerbi/model/operations"), "Program expose operations route");
    }

    static void RequestLimitsContract()
    {
        Assert(PowerBiAuthoringService.MaxRequestBytes <= 262_144, "request size limit có đặt");
        Assert(PowerBiAuthoringService.MaxOperations <= 100, "operations limit có đặt");
        Assert(PowerBiAuthoringService.MaxRowsPerOperation <= 200, "rows limit có đặt");
        Assert(PowerBiAuthoringService.MaxRowCells <= 64, "row cell limit có đặt");
        Assert(PowerBiAuthoringService.DestructiveConfirmation == "DELETE", "destructive confirm cố định");
    }

    static void OperationShapeContract()
    {
        PowerBiAuthoringService.ValidateRequestShape(new(
            null,
            null,
            true,
            null,
            [new("create_measure", "Sales", null, "Revenue", "SUM(Sales[Amount])", "#,0", "KPIs", false, null, null, null, null, null, null, null, null, null, null, null, null, null)]));

        PowerBiAuthoringService.ValidateRequestShape(new(
            null,
            null,
            true,
            null,
            [new("create_table", "Sales", null, null, null, null, null, null, [], [new("Amount", "Decimal")], null, null, null, null, null, null, null, null, null, null, null)]));

        PowerBiAuthoringService.ValidateRequestShape(new(
            null,
            null,
            false,
            "DELETE",
            [new("delete_table", "Sales", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null)]));

        AssertThrows(() => PowerBiAuthoringService.ValidateRequestShape(new(null, null, true, null, [new("delete_model", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null)])), "unsupported_operation_type");
        AssertThrows(() => PowerBiAuthoringService.ValidateRequestShape(new(null, null, true, null, [new("import_sample_rows", "T", "P", null, null, null, null, null, [], null, null, null, null, null, null, null, null, null, null, null, null)])), "rows_required");
        AssertThrows(() => PowerBiAuthoringService.ValidateRequestShape(new(null, null, true, null, [new("update_measure", "T", null, "M", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null)])), "expression_required");
        AssertThrows(() => PowerBiAuthoringService.ValidateRequestShape(new(null, null, false, null, [new("delete_table", "T", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null)])), "destructive_confirmation_required");
        var tooManyRows = Enumerable.Range(0, PowerBiAuthoringService.MaxRowsPerOperation + 1)
            .Select(_ => new Dictionary<string, JsonElement> { ["A"] = JsonDocument.Parse("1").RootElement.Clone() })
            .ToList();
        AssertThrows(() => PowerBiAuthoringService.ValidateRequestShape(new(null, null, true, null, [new("import_sample_rows", "T", "P", null, null, null, null, null, tooManyRows, null, null, null, null, null, null, null, null, null, null, null, null)])), $"too_many_rows>{PowerBiAuthoringService.MaxRowsPerOperation}");
    }

    static void MExpressionContract()
    {
        var columns = new List<DataColumn>
        {
            new() { Name = "Name", DataType = DataType.String },
            new() { Name = "Amount", DataType = DataType.Decimal },
            new() { Name = "CreatedAt", DataType = DataType.DateTime },
            new() { Name = "IsActive", DataType = DataType.Boolean }
        };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["Name"] = "Alice",
                ["Amount"] = 12.5m,
                ["CreatedAt"] = new DateTime(2026, 1, 2, 3, 4, 5),
                ["IsActive"] = true
            }
        };
        var m = PowerBiAuthoringService.BuildMTableExpression(columns, rows);
        Assert(m.Contains("type table [Name = text, Amount = number, CreatedAt = datetime, IsActive = logical]"), "M expression simple identifiers không bị quote như string literal");
        Assert(m.Contains("#datetime(2026,1,2,3,4,5)"), "M expression datetime literal");
        Assert(m.EndsWith(" in Source"), "M expression kết thúc hợp lệ");

        var specialColumns = new List<DataColumn>
        {
            new() { Name = "Mã nhân viên", DataType = DataType.String },
            new() { Name = "Tên \"hiển thị\"", DataType = DataType.String }
        };
        var specialRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["Mã nhân viên"] = "EMP001",
                ["Tên \"hiển thị\""] = "Alice"
            }
        };
        var specialM = PowerBiAuthoringService.BuildMTableExpression(specialColumns, specialRows);
        Assert(specialM.Contains("type table [#\"Mã nhân viên\" = text, #\"Tên \"\"hiển thị\"\"\" = text]"), "M expression special identifiers dùng quoted identifier đúng cú pháp M");
    }

    static void FingerprintContract()
    {
        var db = new Database { Name = "Demo", ID = "db1", CompatibilityLevel = 1565 };
        var model = new Model();
        db.Model = model;
        var table = new Table { Name = "Sales" };
        table.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Decimal });
        table.Columns.Add(new CalculatedColumn { Name = "Net", Expression = "1", DataType = DataType.Decimal });
        table.Partitions.Add(new Partition { Name = "Sales", Source = new MPartitionSource { Expression = "let Source = #table({\"Amount\"}, {{1}}) in Source" } });
        table.Measures.Add(new Measure { Name = "Revenue", Expression = "SUM(Sales[Amount])" });
        model.Tables.Add(table);
        model.Relationships.Add(new SingleColumnRelationship
        {
            Name = "Rel1",
            FromColumn = table.Columns.Find("Amount") as DataColumn,
            ToColumn = table.Columns.Find("Amount") as DataColumn,
            IsActive = true,
            CrossFilteringBehavior = CrossFilteringBehavior.OneDirection
        });
        var fp1 = PowerBiAuthoringService.ComputeFingerprint(model);
        ((DataColumn)table.Columns.Find("Amount")!).IsHidden = true;
        var fpHidden = PowerBiAuthoringService.ComputeFingerprint(model);
        ((DataColumn)table.Columns.Find("Amount")!).IsHidden = false;
        ((DataColumn)table.Columns.Find("Amount")!).IsKey = true;
        var fpKey = PowerBiAuthoringService.ComputeFingerprint(model);
        ((DataColumn)table.Columns.Find("Amount")!).IsKey = false;
        (table.Partitions[0].Source as MPartitionSource)!.Expression = "let Source = #table({\"Amount\"}, {{2}}) in Source";
        var fp2 = PowerBiAuthoringService.ComputeFingerprint(model);
        Assert(fp1.Length == 64, "fingerprint sha256 hex");
        Assert(fp1 != fpHidden, "fingerprint đổi khi hidden đổi");
        Assert(fpHidden != fpKey, "fingerprint đổi khi key đổi");
        Assert(fp1 != fp2, "fingerprint đổi khi model đổi");
    }

    static void BackupRestoreContract()
    {
        var db = new Database { Name = "Demo", ID = "db1", CompatibilityLevel = 1565 };
        var model = new Model();
        db.Model = model;
        var sales = new Table { Name = "Sales" };
        sales.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.Int64 });
        sales.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Decimal });
        sales.Columns.Add(new CalculatedColumn { Name = "Net", Expression = "1", DataType = DataType.Decimal, IsHidden = true });
        sales.Partitions.Add(new Partition { Name = "Sales", Source = new MPartitionSource { Expression = "let Source = #table(type table [\"Id\" = Int64.Type, \"Amount\" = number], {{1,2}}) in Source" } });
        sales.Measures.Add(new Measure { Name = "Revenue", Expression = "SUM(Sales[Amount])" });
        var dim = new Table { Name = "Dim" };
        dim.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.Int64 });
        dim.Partitions.Add(new Partition { Name = "Dim", Source = new MPartitionSource { Expression = "let Source = #table(type table [\"Id\" = Int64.Type], {{1}}) in Source" } });
        model.Tables.Add(sales);
        model.Tables.Add(dim);
        model.Relationships.Add(new SingleColumnRelationship
        {
            Name = "Sales_Dim",
            FromColumn = sales.Columns.Find("Id") as DataColumn,
            ToColumn = dim.Columns.Find("Id") as DataColumn,
            IsActive = true,
            CrossFilteringBehavior = CrossFilteringBehavior.OneDirection
        });
        var fp = PowerBiAuthoringService.ComputeFingerprint(model);
        var snapshotMethod = typeof(PowerBiAuthoringService).GetMethod("SnapshotModel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var restoreMethod = typeof(PowerBiAuthoringService).GetMethod("RestoreModelSnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var payload = snapshotMethod.Invoke(null, [model, 12345, fp])!;

        sales.Measures.Clear();
        sales.Columns.Remove(sales.Columns.Find("Net")!);
        (sales.Partitions[0].Source as MPartitionSource)!.Expression = "let Source = #table(type table [\"Id\" = Int64.Type, \"Amount\" = number], {{9,9}}) in Source";
        model.Relationships.Clear();

        restoreMethod.Invoke(null, [model, payload]);

        Assert(sales.Measures.Find("Revenue") is not null, "restore measure");
        Assert(sales.Columns.Find("Net") is CalculatedColumn, "restore calculated column");
        Assert(((sales.Partitions[0].Source as MPartitionSource)?.Expression ?? "").Contains("{{1,2}}"), "restore partition expression");
        Assert(model.Relationships.Find("Sales_Dim") is not null, "restore relationship");
    }

    static void BatchSemanticContract()
    {
        PowerBiAuthoringService.ValidateRequestShape(new(
            null,
            null,
            true,
            null,
            [new("create_measure", "Sales", null, "Revenue", "1", null, null, false, null, null, null, null, null, null, null, null, null, null, null, null, null)]));

        var db = new Database { Name = "Demo", ID = "db1", CompatibilityLevel = 1565 };
        var model = new Model();
        db.Model = model;
        var table = new Table { Name = "Sales" };
        table.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.Int64 });
        model.Tables.Add(table);
        using var server = new Server();
        using var session = new PowerBiAuthoringService.Session(server, db, model, 12345);
        var validateBatch = typeof(PowerBiAuthoringService).GetMethod("ValidateBatchSemantics", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var prepare = typeof(PowerBiAuthoringService).GetMethod("PrepareOperation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var restoreOp = prepare.Invoke(null, [new PowerBiAuthoringService.OperationRequest("restore", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, "a.json", null)])!;
        var measureOp = prepare.Invoke(null, [new PowerBiAuthoringService.OperationRequest("create_measure", "Sales", null, "M", "1", null, null, false, null, null, null, null, null, null, null, null, null, null, null, null, null)])!;
        AssertThrows(() => validateBatch.Invoke(null, [session, ToPreparedList(restoreOp, measureOp)]), "restore_must_be_single_operation");

        var createTable = prepare.Invoke(null, [new PowerBiAuthoringService.OperationRequest("create_table", "T", null, null, null, null, null, null, [], [new("Id", "Int64")], null, null, null, null, null, null, null, null, null, null, null)])!;
        AssertThrows(() => validateBatch.Invoke(null, [session, ToPreparedList(createTable, measureOp)]), "topology_batch_not_supported_in_v4:create_table");
    }

    static object ToPreparedList(params object[] operations)
    {
        var type = operations[0].GetType();
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        foreach (var operation in operations) list.Add(operation);
        return list;
    }

    static void RelationshipValidationContract()
    {
        var db = new Database { Name = "Demo", ID = "db1", CompatibilityLevel = 1565 };
        var model = new Model();
        db.Model = model;
        var sales = new Table { Name = "Sales" };
        sales.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.Int64 });
        var dim = new Table { Name = "Dim" };
        dim.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.String });
        model.Tables.Add(sales);
        model.Tables.Add(dim);
        using var server = new Server();
        using var session = new PowerBiAuthoringService.Session(server, db, model, 12345);
        var prepare = typeof(PowerBiAuthoringService).GetMethod("PrepareOperation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var validateOp = typeof(PowerBiAuthoringService).GetMethod("ValidateOperation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var batchStateType = typeof(PowerBiAuthoringService).GetNestedType("BatchState", System.Reflection.BindingFlags.NonPublic)!;
        var batchState = Activator.CreateInstance(batchStateType)!;
        var relOp = prepare.Invoke(null, [new PowerBiAuthoringService.OperationRequest("create_relationship", null, null, null, null, null, null, null, null, null, null, null, "Sales", "Id", "Dim", "Id", "Sales_Dim", true, "OneDirection", null, null)])!;
        AssertThrows(() => validateOp.Invoke(null, [session, relOp, batchState]), "relationship_column_type_mismatch");
    }

    static void RestorePathContract()
    {
        var resolve = typeof(PowerBiAuthoringService).GetMethod("ResolveBackupPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        AssertThrows(() => resolve.Invoke(null, ["../escape.json", null]), "backup_id_basename_required");
    }

    static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    static void AssertThrows(Action action, string contains)
    {
        try
        {
            action();
            throw new Exception("FAIL: expected exception containing " + contains);
        }
        catch (Exception ex) when (ExceptionChainContains(ex, contains))
        {
            Console.WriteLine("PASS: throws " + contains);
        }
    }

    static bool ExceptionChainContains(Exception ex, string contains)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
            if (current.Message.Contains(contains, StringComparison.Ordinal))
                return true;
        return false;
    }
}
