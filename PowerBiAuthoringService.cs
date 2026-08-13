using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SystemJsonSerializer = System.Text.Json.JsonSerializer;

internal static class PowerBiAuthoringService
{
    internal const int MaxRequestBytes = 262_144;
    internal const int MaxOperations = 100;
    internal const int MaxRowsPerOperation = 200;
    internal const int MaxRowCells = 64;
    internal const int MaxMeasureExpressionChars = 16_384;
    internal const int MaxMeasureNameChars = 256;
    internal const int MaxColumnExpressionChars = 16_384;
    internal const int MaxColumnNameChars = 256;
    internal const int MaxPartitionExpressionChars = 65_536;
    internal const int MaxTableNameChars = 256;
    internal const int MaxRelationshipNameChars = 256;
    internal const int MaxRestorePathChars = 512;
    internal const int MaxTableColumns = 64;
    internal const int MaxTableRows = 500;
    internal const string DestructiveConfirmation = "DELETE";

    internal sealed record InspectResponse(bool ok, int port, string fingerprint, ModelMetadata model);
    internal sealed record ModelMetadata(string name, string id, int compatibilityLevel, Counts counts, List<TableMetadata> tables, List<MeasureMetadata> measures, List<CalculatedColumnMetadata> calculatedColumns, List<RelationshipMetadata> relationships);
    internal sealed record Counts(int tables, int columns, int measures, int calculatedColumns, int relationships, int partitions);
    internal sealed record TableMetadata(string name, bool hidden, List<PartitionMetadata> partitions, List<string> columns);
    internal sealed record PartitionMetadata(string name, string sourceType, string? expression);
    internal sealed record MeasureMetadata(string table, string name, bool hidden, string? formatString, string? displayFolder, string expression);
    internal sealed record CalculatedColumnMetadata(string table, string name, bool hidden, string? formatString, string? displayFolder, string expression, string dataType);
    internal sealed record RelationshipMetadata(string name, string fromTable, string fromColumn, string toTable, string toColumn, bool isActive, string crossFilteringBehavior);
    internal sealed record ColumnMetadata(string table, string name, string kind, string dataType, bool hidden, bool isKey);

    internal sealed record OperationsRequest(int? port, string? expectedFingerprint, bool? dryRun, string? destructiveConfirm, List<OperationRequest>? operations);
    internal sealed record OperationRequest(
        string? type,
        string? table,
        string? partition,
        string? measure,
        string? expression,
        string? formatString,
        string? displayFolder,
        bool? hidden,
        List<Dictionary<string, JsonElement>>? rows,
        List<ColumnSpec>? columns,
        string? column,
        string? dataType,
        string? fromTable,
        string? fromColumn,
        string? toTable,
        string? toColumn,
        string? relationship,
        bool? isActive,
        string? crossFilteringBehavior,
        string? backupId,
        string? backupPath);
    internal sealed record ColumnSpec(string? name, string? dataType);
    internal sealed record OperationsResponse(bool ok, bool dryRun, int port, string fingerprint, string? backupPath, List<object> operations);
    internal sealed record BackupEnvelope(DateTimeOffset createdAt, int port, string fingerprint, List<BackupTable> tables, List<BackupRelationship> relationships);
    internal sealed record BackupTable(string name, bool hidden, List<BackupPartition> partitions, List<BackupMeasure> measures, List<BackupCalculatedColumn> calculatedColumns, List<BackupDataColumn> dataColumns);
    internal sealed record BackupPartition(string name, string sourceType, string? expression);
    internal sealed record BackupMeasure(string name, string expression, string? formatString, string? displayFolder, bool hidden);
    internal sealed record BackupCalculatedColumn(string name, string expression, string dataType, string? formatString, string? displayFolder, bool hidden);
    internal sealed record BackupDataColumn(string name, string dataType, bool hidden, bool isKey);
    internal sealed record BackupRelationship(string name, string fromTable, string fromColumn, string toTable, string toColumn, bool isActive, string crossFilteringBehavior);

    internal sealed class Session : IDisposable
    {
        internal Session(Server server, Database database, Model model, int port)
        {
            Server = server;
            Database = database;
            Model = model;
            Port = port;
        }

        internal Server Server { get; }
        internal Database Database { get; }
        internal Model Model { get; }
        internal int Port { get; }

        public void Dispose()
        {
            try { Server.Disconnect(); } catch { }
            Server.Dispose();
        }
    }

    internal static string InspectJson()
    {
        using var session = OpenSingleModelSession();
        return BridgeCore.Json(BuildInspectResponse(session));
    }

    internal static string ApplyOperationsJson(string body)
    {
        if (Encoding.UTF8.GetByteCount(body) > MaxRequestBytes)
            throw new InvalidOperationException($"request_too_large>{MaxRequestBytes}");

        var request = SystemJsonSerializer.Deserialize<OperationsRequest>(body, JsonOptions())
            ?? throw new InvalidOperationException("invalid_request");
        var operations = request.operations ?? throw new InvalidOperationException("operations_required");
        if (operations.Count == 0) throw new InvalidOperationException("operations_required");
        if (operations.Count > MaxOperations) throw new InvalidOperationException($"too_many_operations>{MaxOperations}");

        using var session = OpenSingleModelSession();
        var inspect = BuildInspectResponse(session);
        var dryRun = request.dryRun ?? true;
        if (!dryRun)
        {
            if (request.port is null) throw new InvalidOperationException("port_required_for_apply");
            if (request.expectedFingerprint is null) throw new InvalidOperationException("expectedFingerprint_required_for_apply");
            if (request.port.Value != inspect.port) throw new InvalidOperationException("port_mismatch");
            if (!string.Equals(request.expectedFingerprint, inspect.fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("fingerprint_mismatch");
        }

        var destructiveNeeded = operations.Any(IsDestructiveOperation);
        if (!dryRun && destructiveNeeded && !string.Equals(request.destructiveConfirm, DestructiveConfirmation, StringComparison.Ordinal))
            throw new InvalidOperationException("destructive_confirmation_required");

        var prepared = operations.Select(PrepareOperation).ToList();
        ValidateBatchSemantics(session, prepared);
        var results = new List<object>(operations.Count);
        var batchState = new BatchState();
        foreach (var op in prepared)
            results.Add(ValidateOperation(session, op, batchState));

        string? backupPath = null;
        if (!dryRun)
        {
            backupPath = BackupModel(session, inspect.fingerprint);
            try
            {
                foreach (var op in prepared)
                    ApplyValidatedOperation(session, op);
                session.Model.SaveChanges();
            }
            catch
            {
                FailClosedRestoreAfterFailure(session, backupPath);
                throw;
            }
        }

        return BridgeCore.Json(new OperationsResponse(true, dryRun, inspect.port, ComputeFingerprint(session.Model), backupPath, results));
    }

    internal static InspectResponse BuildInspectResponse(Session session)
        => new(true, session.Port, ComputeFingerprint(session.Model), BuildMetadata(session.Model));

    internal static ModelMetadata BuildMetadata(Model model)
    {
        var tables = model.Tables
            .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TableMetadata(
                t.Name,
                t.IsHidden,
                t.Partitions.OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PartitionMetadata(p.Name, p.Source?.GetType().Name ?? "null", (p.Source as MPartitionSource)?.Expression))
                    .ToList(),
                t.Columns.OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase).Select(static c => c.Name).ToList()))
            .ToList();
        var columns = model.Tables
            .SelectMany(t => t.Columns.OfType<DataColumn>().Select(c => new ColumnMetadata(t.Name, c.Name, "data", c.DataType.ToString(), c.IsHidden, c.IsKey)))
            .OrderBy(static x => x.table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var measures = model.Tables
            .SelectMany(t => t.Measures.Select(m => new MeasureMetadata(t.Name, m.Name, m.IsHidden, m.FormatString, m.DisplayFolder, m.Expression ?? string.Empty)))
            .OrderBy(static x => x.table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var calculatedColumns = model.Tables
            .SelectMany(t => t.Columns.OfType<CalculatedColumn>().Select(c => new CalculatedColumnMetadata(t.Name, c.Name, c.IsHidden, c.FormatString, c.DisplayFolder, c.Expression ?? string.Empty, c.DataType.ToString())))
            .OrderBy(static x => x.table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var relationships = model.Relationships.OfType<SingleColumnRelationship>()
            .Select(r => new RelationshipMetadata(r.Name, r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name, r.IsActive, r.CrossFilteringBehavior.ToString()))
            .OrderBy(static x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ModelMetadata(
            model.Name,
            model.Database.ID,
            model.Database.CompatibilityLevel,
            new Counts(model.Tables.Count, model.Tables.Sum(static t => t.Columns.Count), measures.Count, calculatedColumns.Count, relationships.Count, model.Tables.Sum(static t => t.Partitions.Count)),
            tables,
            measures,
            calculatedColumns,
            relationships);
    }

    internal static string ComputeFingerprint(Model model)
    {
        var fingerprintShape = new
        {
            model = BuildMetadata(model),
            dataColumns = model.Tables
                .SelectMany(t => t.Columns.OfType<DataColumn>().Select(c => new { table = t.Name, name = c.Name, dataType = c.DataType.ToString(), hidden = c.IsHidden, isKey = c.IsKey }))
                .OrderBy(static x => x.table, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static x => x.name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            relationships = model.Relationships.OfType<SingleColumnRelationship>()
                .Select(r => new { r.Name, fromTable = r.FromTable.Name, fromColumn = r.FromColumn.Name, toTable = r.ToTable.Name, toColumn = r.ToColumn.Name, r.IsActive, crossFilteringBehavior = r.CrossFilteringBehavior.ToString(), fromCardinality = r.FromCardinality.ToString(), toCardinality = r.ToCardinality.ToString() })
                .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        var json = SystemJsonSerializer.Serialize(fingerprintShape, JsonOptions());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    static void ValidateBatchSemantics(Session session, IReadOnlyList<PreparedOperation> operations)
    {
        var createdTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdMeasures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedMeasures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in operations)
        {
            if (op.Type == "restore" && operations.Count > 1)
                throw new InvalidOperationException("restore_must_be_single_operation");
            if (operations.Count > 1 && IsTopologyOperation(op.Type))
                throw new InvalidOperationException($"topology_batch_not_supported_in_v4:{op.Type}");

            switch (op.Type)
            {
                case "create_table":
                    if (!createdTables.Add(op.Table!)) throw new InvalidOperationException($"duplicate_table_create_in_batch:{op.Table}");
                    break;
                case "delete_table":
                    if (!deletedTables.Add(op.Table!)) throw new InvalidOperationException($"duplicate_table_delete_in_batch:{op.Table}");
                    break;
                case "create_measure":
                    if (!createdMeasures.Add($"{op.Table}.{op.Measure}")) throw new InvalidOperationException($"duplicate_measure_create_in_batch:{op.Table}.{op.Measure}");
                    break;
                case "delete_measure":
                    if (!deletedMeasures.Add($"{op.Table}.{op.Measure}")) throw new InvalidOperationException($"duplicate_measure_delete_in_batch:{op.Table}.{op.Measure}");
                    break;
                case "create_calculated_column":
                    if (!createdColumns.Add($"{op.Table}.{op.Column}")) throw new InvalidOperationException($"duplicate_column_create_in_batch:{op.Table}.{op.Column}");
                    break;
                case "delete_calculated_column":
                    if (!deletedColumns.Add($"{op.Table}.{op.Column}")) throw new InvalidOperationException($"duplicate_column_delete_in_batch:{op.Table}.{op.Column}");
                    break;
                case "create_relationship":
                    if (!createdRelationships.Add(op.Relationship!)) throw new InvalidOperationException($"duplicate_relationship_create_in_batch:{op.Relationship}");
                    EnsureRelationshipTargetsExist(session.Model, op, createdTables, deletedTables, createdColumns, deletedColumns);
                    break;
                case "delete_relationship":
                    if (!deletedRelationships.Add(op.Relationship!)) throw new InvalidOperationException($"duplicate_relationship_delete_in_batch:{op.Relationship}");
                    break;
                default:
                    EnsureOpTargetsNotDeleted(op, deletedTables, deletedMeasures, deletedColumns, deletedRelationships);
                    break;
            }
        }
    }

    static void EnsureOpTargetsNotDeleted(PreparedOperation op, HashSet<string> deletedTables, HashSet<string> deletedMeasures, HashSet<string> deletedColumns, HashSet<string> deletedRelationships)
    {
        if (op.Table is not null && deletedTables.Contains(op.Table)) throw new InvalidOperationException($"operation_targets_deleted_table:{op.Table}");
        if (op.Measure is not null && op.Table is not null && deletedMeasures.Contains($"{op.Table}.{op.Measure}")) throw new InvalidOperationException($"operation_targets_deleted_measure:{op.Table}.{op.Measure}");
        if (op.Column is not null && op.Table is not null && deletedColumns.Contains($"{op.Table}.{op.Column}")) throw new InvalidOperationException($"operation_targets_deleted_column:{op.Table}.{op.Column}");
        if (op.Relationship is not null && deletedRelationships.Contains(op.Relationship) && op.Type != "delete_relationship") throw new InvalidOperationException($"operation_targets_deleted_relationship:{op.Relationship}");
    }

    static void EnsureRelationshipTargetsExist(Model model, PreparedOperation op, HashSet<string> createdTables, HashSet<string> deletedTables, HashSet<string> createdColumns, HashSet<string> deletedColumns)
    {
        ValidateRelationshipEndpoint(model, op.FromTable!, op.FromColumn!, createdTables, deletedTables, createdColumns, deletedColumns);
        ValidateRelationshipEndpoint(model, op.ToTable!, op.ToColumn!, createdTables, deletedTables, createdColumns, deletedColumns);
    }

    static void ValidateRelationshipEndpoint(Model model, string tableName, string columnName, HashSet<string> createdTables, HashSet<string> deletedTables, HashSet<string> createdColumns, HashSet<string> deletedColumns)
    {
        if (deletedTables.Contains(tableName)) throw new InvalidOperationException($"relationship_targets_deleted_table:{tableName}");
        if (createdTables.Contains(tableName)) throw new InvalidOperationException($"relationship_targets_new_table_unsupported_in_v4:{tableName}");
        var table = model.Tables.Find(tableName) ?? throw new InvalidOperationException("table_not_found");
        var key = $"{tableName}.{columnName}";
        if (deletedColumns.Contains(key)) throw new InvalidOperationException($"relationship_targets_deleted_column:{key}");
        if (createdColumns.Contains(key)) throw new InvalidOperationException($"relationship_targets_new_column_unsupported_in_v4:{key}");
        _ = FindDataColumn(table, columnName);
    }

    internal static void ValidateRequestShape(OperationsRequest request)
    {
        var operations = request.operations ?? throw new InvalidOperationException("operations_required");
        if (operations.Count > MaxOperations) throw new InvalidOperationException($"too_many_operations>{MaxOperations}");
        var destructiveNeeded = operations.Any(op => IsDestructiveOperation((op.type ?? string.Empty).Trim()));
        if (!(request.dryRun ?? true) && destructiveNeeded && !string.Equals(request.destructiveConfirm, DestructiveConfirmation, StringComparison.Ordinal))
            throw new InvalidOperationException("destructive_confirmation_required");
        foreach (var op in operations) ValidateOperationShape(op);
    }

    internal static void ValidateOperationShape(OperationRequest op)
    {
        PrepareOperation(op);
    }

    static PreparedOperation PrepareOperation(OperationRequest op)
    {
        var type = RequireName(op.type, "operation_type");
        return type switch
        {
            "import_sample_rows" => new PreparedOperation(type, Table: RequireName(op.table, "table"), Partition: RequireName(op.partition, "partition"), Rows: ValidateRows(op.rows), Raw: op),
            "create_measure" or "update_measure" => new PreparedOperation(type, Table: RequireName(op.table, "table"), Measure: ValidateName(op.measure, "measure", MaxMeasureNameChars), Expression: ValidateExpression(op.expression, MaxMeasureExpressionChars), FormatString: op.formatString, DisplayFolder: op.displayFolder, Hidden: op.hidden, Raw: op),
            "delete_measure" => new PreparedOperation(type, Table: RequireName(op.table, "table"), Measure: ValidateName(op.measure, "measure", MaxMeasureNameChars), Raw: op),
            "create_calculated_column" or "update_calculated_column" => new PreparedOperation(type, Table: RequireName(op.table, "table"), Column: ValidateName(op.column, "column", MaxColumnNameChars), Expression: ValidateExpression(op.expression, MaxColumnExpressionChars), FormatString: op.formatString, DisplayFolder: op.displayFolder, Hidden: op.hidden, DataType: ValidateDataTypeName(op.dataType), Raw: op),
            "delete_calculated_column" => new PreparedOperation(type, Table: RequireName(op.table, "table"), Column: ValidateName(op.column, "column", MaxColumnNameChars), Raw: op),
            "create_table" => new PreparedOperation(type, Table: ValidateName(op.table, "table", MaxTableNameChars), Columns: ValidateColumns(op.columns), Rows: ValidateCreateTableRows(op.rows), Raw: op),
            "delete_table" => new PreparedOperation(type, Table: ValidateName(op.table, "table", MaxTableNameChars), Raw: op),
            "create_relationship" => new PreparedOperation(type, Relationship: ValidateRelationshipName(op.relationship), FromTable: RequireName(op.fromTable, "fromTable"), FromColumn: RequireName(op.fromColumn, "fromColumn"), ToTable: RequireName(op.toTable, "toTable"), ToColumn: RequireName(op.toColumn, "toColumn"), IsActive: op.isActive ?? true, CrossFilteringBehavior: ValidateCrossFilteringBehavior(op.crossFilteringBehavior), Raw: op),
            "delete_relationship" => new PreparedOperation(type, Relationship: ValidateRelationshipName(op.relationship), Raw: op),
            "update_partition_expression" => new PreparedOperation(type, Table: RequireName(op.table, "table"), Partition: RequireName(op.partition, "partition"), Expression: ValidateExpression(op.expression, MaxPartitionExpressionChars), Raw: op),
            "restore" => new PreparedOperation(type, BackupId: ValidateRestoreLocator(op.backupId, "backupId"), BackupPath: ValidateRestoreLocator(op.backupPath, "backupPath"), Raw: op),
            _ => throw new InvalidOperationException("unsupported_operation_type")
        };
    }

    static object ValidateOperation(Session session, PreparedOperation op, BatchState batchState)
        => op.Type switch
        {
            "import_sample_rows" => ValidateImportSampleRows(session, op, batchState),
            "create_measure" => ValidateMeasure(session, op, mustExist: false),
            "update_measure" => ValidateMeasure(session, op, mustExist: true),
            "delete_measure" => ValidateDeleteMeasure(session, op),
            "create_calculated_column" => ValidateCalculatedColumn(session, op, mustExist: false),
            "update_calculated_column" => ValidateCalculatedColumn(session, op, mustExist: true),
            "delete_calculated_column" => ValidateDeleteCalculatedColumn(session, op),
            "create_table" => ValidateCreateTable(session, op),
            "delete_table" => ValidateDeleteTable(session, op),
            "create_relationship" => ValidateCreateRelationship(session, op),
            "delete_relationship" => ValidateDeleteRelationship(session, op),
            "update_partition_expression" => ValidateUpdatePartitionExpression(session, op),
            "restore" => ValidateRestore(op),
            _ => throw new InvalidOperationException("unsupported_operation_type")
        };

    static void ApplyValidatedOperation(Session session, PreparedOperation op)
    {
        switch (op.Type)
        {
            case "import_sample_rows": ApplyImportSampleRows(session, op); break;
            case "create_measure":
            case "update_measure": ApplyMeasure(session, op, op.Type == "update_measure"); break;
            case "delete_measure": ApplyDeleteMeasure(session, op); break;
            case "create_calculated_column":
            case "update_calculated_column": ApplyCalculatedColumn(session, op, op.Type == "update_calculated_column"); break;
            case "delete_calculated_column": ApplyDeleteCalculatedColumn(session, op); break;
            case "create_table": ApplyCreateTable(session, op); break;
            case "delete_table": ApplyDeleteTable(session, op); break;
            case "create_relationship": ApplyCreateRelationship(session, op); break;
            case "delete_relationship": ApplyDeleteRelationship(session, op); break;
            case "update_partition_expression": ApplyUpdatePartitionExpression(session, op); break;
            case "restore": ApplyRestore(session, op); break;
            default: throw new InvalidOperationException("unsupported_operation_type");
        }
    }

    static object ValidateImportSampleRows(Session session, PreparedOperation op, BatchState batchState)
    {
        var table = FindTable(session.Model, op.Table!);
        var partition = FindPartition(table, op.Partition!);
        if (partition.Source is not MPartitionSource)
            throw new InvalidOperationException("partition_source_m_required");
        if (!batchState.TouchedTables.Add(table.Name))
            throw new InvalidOperationException("duplicate_table_import_in_batch");

        var columns = table.Columns.Where(static c => c.Type != ColumnType.RowNumber).OfType<DataColumn>().OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (columns.Count == 0) throw new InvalidOperationException("table_has_no_supported_columns");
        var rows = op.Rows!.Select(r => NormalizeRow(r, columns)).ToList();
        _ = BuildMTableExpression(columns, rows);
        return new { type = op.Type, table = table.Name, partition = partition.Name, rowCount = rows.Count, applied = false };
    }

    static void ApplyImportSampleRows(Session session, PreparedOperation op)
    {
        var table = FindTable(session.Model, op.Table!);
        var partition = FindPartition(table, op.Partition!);
        var mSource = partition.Source as MPartitionSource ?? throw new InvalidOperationException("partition_source_m_required");
        var columns = table.Columns.Where(static c => c.Type != ColumnType.RowNumber).OfType<DataColumn>().OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = op.Rows!.Select(r => NormalizeRow(r, columns)).ToList();
        mSource.Expression = BuildMTableExpression(columns, rows);
    }

    static object ValidateMeasure(Session session, PreparedOperation op, bool mustExist)
    {
        var table = FindTable(session.Model, op.Table!);
        var existing = table.Measures.Find(op.Measure!);
        if (mustExist && existing is null) throw new InvalidOperationException("measure_not_found");
        if (!mustExist && existing is not null) throw new InvalidOperationException("measure_already_exists");
        return new { type = op.Type, table = table.Name, measure = op.Measure, applied = false };
    }

    static void ApplyMeasure(Session session, PreparedOperation op, bool mustExist)
    {
        var table = FindTable(session.Model, op.Table!);
        var existing = table.Measures.Find(op.Measure!);
        if (mustExist && existing is null) throw new InvalidOperationException("measure_not_found");
        if (!mustExist && existing is not null) throw new InvalidOperationException("measure_already_exists");
        var measure = existing ?? new Measure { Name = op.Measure! };
        measure.Expression = op.Expression!;
        measure.FormatString = op.FormatString;
        measure.DisplayFolder = op.DisplayFolder;
        measure.IsHidden = op.Hidden ?? false;
        if (existing is null) table.Measures.Add(measure);
    }

    static object ValidateDeleteMeasure(Session session, PreparedOperation op)
    {
        var table = FindTable(session.Model, op.Table!);
        _ = table.Measures.Find(op.Measure!) ?? throw new InvalidOperationException("measure_not_found");
        return new { type = op.Type, table = table.Name, measure = op.Measure, applied = false, destructive = true };
    }

    static void ApplyDeleteMeasure(Session session, PreparedOperation op)
    {
        var table = FindTable(session.Model, op.Table!);
        var measure = table.Measures.Find(op.Measure!) ?? throw new InvalidOperationException("measure_not_found");
        table.Measures.Remove(measure);
    }

    static object ValidateCalculatedColumn(Session session, PreparedOperation op, bool mustExist)
    {
        var table = FindTable(session.Model, op.Table!);
        var existing = table.Columns.Find(op.Column!) as CalculatedColumn;
        if (mustExist && existing is null) throw new InvalidOperationException("calculated_column_not_found");
        if (!mustExist && existing is not null) throw new InvalidOperationException("calculated_column_already_exists");
        return new { type = op.Type, table = table.Name, column = op.Column, applied = false };
    }

    static void ApplyCalculatedColumn(Session session, PreparedOperation op, bool mustExist)
    {
        var table = FindTable(session.Model, op.Table!);
        var existing = table.Columns.Find(op.Column!) as CalculatedColumn;
        if (mustExist && existing is null) throw new InvalidOperationException("calculated_column_not_found");
        if (!mustExist && existing is not null) throw new InvalidOperationException("calculated_column_already_exists");
        var column = existing ?? new CalculatedColumn { Name = op.Column! };
        column.Expression = op.Expression!;
        column.DataType = ParseDataType(op.DataType!);
        column.FormatString = op.FormatString;
        column.DisplayFolder = op.DisplayFolder;
        column.IsHidden = op.Hidden ?? false;
        if (existing is null) table.Columns.Add(column);
    }

    static object ValidateDeleteCalculatedColumn(Session session, PreparedOperation op)
    {
        var table = FindTable(session.Model, op.Table!);
        var column = table.Columns.Find(op.Column!) as CalculatedColumn ?? throw new InvalidOperationException("calculated_column_not_found");
        return new { type = op.Type, table = table.Name, column = column.Name, applied = false, destructive = true };
    }

    static void ApplyDeleteCalculatedColumn(Session session, PreparedOperation op)
    {
        var table = FindTable(session.Model, op.Table!);
        var column = table.Columns.Find(op.Column!) as CalculatedColumn ?? throw new InvalidOperationException("calculated_column_not_found");
        table.Columns.Remove(column);
    }

    static object ValidateCreateTable(Session session, PreparedOperation op)
    {
        if (session.Model.Tables.Find(op.Table!) is not null) throw new InvalidOperationException("table_already_exists");
        var columns = op.Columns!;
        var rows = op.Rows ?? [];
        if (rows.Count > MaxTableRows) throw new InvalidOperationException($"too_many_rows>{MaxTableRows}");
        var dataColumns = columns.Select(c => new DataColumn { Name = c.name!, DataType = ParseDataType(c.dataType!) }).ToList();
        var normalized = rows.Select(r => NormalizeRow(r, dataColumns)).ToList();
        _ = BuildMTableExpression(dataColumns, normalized);
        return new { type = op.Type, table = op.Table, columns = columns.Count, rowCount = rows.Count, applied = false };
    }

    static void ApplyCreateTable(Session session, PreparedOperation op)
    {
        if (session.Model.Tables.Find(op.Table!) is not null) throw new InvalidOperationException("table_already_exists");
        var table = new Table { Name = op.Table! };
        var dataColumns = op.Columns!.Select(c => new DataColumn { Name = c.name!, DataType = ParseDataType(c.dataType!) }).ToList();
        foreach (var col in dataColumns) table.Columns.Add(col);
        var normalized = (op.Rows ?? []).Select(r => NormalizeRow(r, dataColumns)).ToList();
        table.Partitions.Add(new Partition { Name = op.Table!, Source = new MPartitionSource { Expression = BuildMTableExpression(dataColumns, normalized) } });
        session.Model.Tables.Add(table);
    }

    static object ValidateDeleteTable(Session session, PreparedOperation op)
    {
        _ = FindTable(session.Model, op.Table!);
        return new { type = op.Type, table = op.Table, applied = false, destructive = true };
    }

    static void ApplyDeleteTable(Session session, PreparedOperation op)
    {
        var table = FindTable(session.Model, op.Table!);
        session.Model.Tables.Remove(table);
    }

    static object ValidateCreateRelationship(Session session, PreparedOperation op)
    {
        if (session.Model.Relationships.Find(op.Relationship!) is not null) throw new InvalidOperationException("relationship_already_exists");
        var fromColumn = FindDataColumn(FindTable(session.Model, op.FromTable!), op.FromColumn!);
        var toColumn = FindDataColumn(FindTable(session.Model, op.ToTable!), op.ToColumn!);
        ValidateRelationshipColumns(fromColumn, toColumn);
        _ = new SingleColumnRelationship { Name = op.Relationship!, FromColumn = fromColumn, ToColumn = toColumn, IsActive = op.IsActive ?? true, CrossFilteringBehavior = ParseCrossFilteringBehavior(op.CrossFilteringBehavior!) };
        return new { type = op.Type, relationship = op.Relationship, fromTable = op.FromTable, fromColumn = op.FromColumn, toTable = op.ToTable, toColumn = op.ToColumn, applied = false };
    }

    static void ApplyCreateRelationship(Session session, PreparedOperation op)
    {
        if (session.Model.Relationships.Find(op.Relationship!) is not null) throw new InvalidOperationException("relationship_already_exists");
        var relationship = new SingleColumnRelationship
        {
            Name = op.Relationship!,
            FromColumn = FindDataColumn(FindTable(session.Model, op.FromTable!), op.FromColumn!),
            ToColumn = FindDataColumn(FindTable(session.Model, op.ToTable!), op.ToColumn!),
            IsActive = op.IsActive ?? true,
            CrossFilteringBehavior = ParseCrossFilteringBehavior(op.CrossFilteringBehavior!)
        };
        ValidateRelationshipColumns((DataColumn)relationship.FromColumn, (DataColumn)relationship.ToColumn);
        session.Model.Relationships.Add(relationship);
    }

    static object ValidateDeleteRelationship(Session session, PreparedOperation op)
    {
        _ = session.Model.Relationships.Find(op.Relationship!) ?? throw new InvalidOperationException("relationship_not_found");
        return new { type = op.Type, relationship = op.Relationship, applied = false, destructive = true };
    }

    static void ApplyDeleteRelationship(Session session, PreparedOperation op)
    {
        var relationship = session.Model.Relationships.Find(op.Relationship!) ?? throw new InvalidOperationException("relationship_not_found");
        session.Model.Relationships.Remove(relationship);
    }

    static object ValidateUpdatePartitionExpression(Session session, PreparedOperation op)
    {
        var table = FindTable(session.Model, op.Table!);
        var partition = FindPartition(table, op.Partition!);
        if (partition.Source is not MPartitionSource)
            throw new InvalidOperationException("partition_source_m_required");
        return new { type = op.Type, table = op.Table, partition = op.Partition, applied = false };
    }

    static void ApplyUpdatePartitionExpression(Session session, PreparedOperation op)
    {
        var table = FindTable(session.Model, op.Table!);
        var partition = FindPartition(table, op.Partition!);
        var mSource = partition.Source as MPartitionSource ?? throw new InvalidOperationException("partition_source_m_required");
        mSource.Expression = op.Expression!;
    }

    static object ValidateRestore(PreparedOperation op)
    {
        var path = ResolveBackupPath(op.BackupId, op.BackupPath);
        return new { type = op.Type, backupPath = path, applied = false, destructive = true };
    }

    static void ApplyRestore(Session session, PreparedOperation op)
    {
        var path = ResolveBackupPath(op.BackupId, op.BackupPath);
        var payload = SystemJsonSerializer.Deserialize<BackupEnvelope>(File.ReadAllText(path, Encoding.UTF8), JsonOptions())
            ?? throw new InvalidOperationException("backup_invalid");
        RestoreModelSnapshot(session.Model, payload);
    }

    static bool IsDestructiveOperation(OperationRequest op) => IsDestructiveOperation((op.type ?? string.Empty).Trim());
    static bool IsDestructiveOperation(string type)
        => type is "delete_measure" or "delete_calculated_column" or "delete_table" or "delete_relationship" or "restore";
    static bool IsTopologyOperation(string type)
        => type is "create_table" or "delete_table" or "create_relationship" or "delete_relationship" or "restore";

    static string BackupModel(Session session, string fingerprint)
    {
        var backupDir = Path.Combine(BridgeCore.AppDir(), "backups");
        Directory.CreateDirectory(backupDir);
        var payload = SnapshotModel(session.Model, session.Port, fingerprint);
        var path = Path.Combine(backupDir, $"authoring-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, SystemJsonSerializer.Serialize(payload, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true }), new UTF8Encoding(false));
        return path;
    }

    static BackupEnvelope SnapshotModel(Model model, int port, string fingerprint)
        => new(
            DateTimeOffset.Now,
            port,
            fingerprint,
            model.Tables.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new BackupTable(
                    t.Name,
                    t.IsHidden,
                    t.Partitions.OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new BackupPartition(p.Name, p.Source?.GetType().Name ?? "null", (p.Source as MPartitionSource)?.Expression)).ToList(),
                    t.Measures.OrderBy(static m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => new BackupMeasure(m.Name, m.Expression ?? string.Empty, m.FormatString, m.DisplayFolder, m.IsHidden)).ToList(),
                    t.Columns.OfType<CalculatedColumn>().OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new BackupCalculatedColumn(c.Name, c.Expression ?? string.Empty, c.DataType.ToString(), c.FormatString, c.DisplayFolder, c.IsHidden)).ToList(),
                    t.Columns.OfType<DataColumn>().OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new BackupDataColumn(c.Name, c.DataType.ToString(), c.IsHidden, c.IsKey)).ToList()))
                .ToList(),
            model.Relationships.OfType<SingleColumnRelationship>().OrderBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new BackupRelationship(r.Name, r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name, r.IsActive, r.CrossFilteringBehavior.ToString()))
                .ToList());

    static void FailClosedRestoreAfterFailure(Session session, string? backupPath)
    {
        if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
        {
            try
            {
                var payload = SystemJsonSerializer.Deserialize<BackupEnvelope>(File.ReadAllText(backupPath, Encoding.UTF8), JsonOptions());
                if (payload is not null)
                {
                    RestoreModelSnapshot(session.Model, payload);
                    session.Model.SaveChanges();
                    return;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("rollback_restore_failed", ex);
            }
        }
        throw new InvalidOperationException("rollback_backup_unavailable");
    }

    static void RestoreModelSnapshot(Model model, BackupEnvelope payload)
    {
        foreach (var relationship in model.Relationships.OfType<Relationship>().ToList())
            model.Relationships.Remove(relationship);

        foreach (var backupTable in payload.tables)
            _ = model.Tables.Find(backupTable.name) ?? throw new InvalidOperationException($"backup_table_not_found:{backupTable.name}");

        if (model.Tables.Count != payload.tables.Count)
            throw new InvalidOperationException("backup_topology_mismatch");

        foreach (var table in model.Tables)
        {
            foreach (var measure in table.Measures.ToList()) table.Measures.Remove(measure);
            foreach (var calc in table.Columns.OfType<CalculatedColumn>().ToList()) table.Columns.Remove(calc);
        }

        foreach (var backupTable in payload.tables)
        {
            var table = model.Tables.Find(backupTable.name) ?? throw new InvalidOperationException($"backup_table_not_found:{backupTable.name}");
            table.IsHidden = backupTable.hidden;

            foreach (var backupDataColumn in backupTable.dataColumns)
            {
                var column = FindDataColumn(table, backupDataColumn.name);
                if (column.DataType != ParseDataType(backupDataColumn.dataType))
                    throw new InvalidOperationException($"backup_data_column_type_mismatch:{backupTable.name}:{backupDataColumn.name}");
                column.IsHidden = backupDataColumn.hidden;
                column.IsKey = backupDataColumn.isKey;
            }

            foreach (var partition in table.Partitions.ToList())
                table.Partitions.Remove(partition);
            foreach (var backupPartition in backupTable.partitions)
            {
                if (!string.Equals(backupPartition.sourceType, nameof(MPartitionSource), StringComparison.Ordinal))
                    throw new InvalidOperationException($"backup_partition_source_unsupported:{backupTable.name}:{backupPartition.name}");
                table.Partitions.Add(new Partition { Name = backupPartition.name, Source = new MPartitionSource { Expression = backupPartition.expression ?? string.Empty } });
            }

            foreach (var backupMeasure in backupTable.measures)
                table.Measures.Add(new Measure { Name = backupMeasure.name, Expression = backupMeasure.expression, FormatString = backupMeasure.formatString, DisplayFolder = backupMeasure.displayFolder, IsHidden = backupMeasure.hidden });

            foreach (var backupColumn in backupTable.calculatedColumns)
                table.Columns.Add(new CalculatedColumn { Name = backupColumn.name, Expression = backupColumn.expression, DataType = ParseDataType(backupColumn.dataType), FormatString = backupColumn.formatString, DisplayFolder = backupColumn.displayFolder, IsHidden = backupColumn.hidden });
        }

        foreach (var rel in payload.relationships)
        {
            model.Relationships.Add(new SingleColumnRelationship
            {
                Name = rel.name,
                FromColumn = FindDataColumn(FindTable(model, rel.fromTable), rel.fromColumn),
                ToColumn = FindDataColumn(FindTable(model, rel.toTable), rel.toColumn),
                IsActive = rel.isActive,
                CrossFilteringBehavior = ParseCrossFilteringBehavior(rel.crossFilteringBehavior)
            });
        }
    }

    static string ResolveBackupPath(string? backupId, string? backupPath)
    {
        var normalizedBackupId = string.IsNullOrWhiteSpace(backupId) ? null : Path.GetFileName(backupId);
        if (normalizedBackupId is not null && !string.Equals(normalizedBackupId, backupId, StringComparison.Ordinal))
            throw new InvalidOperationException("backup_id_basename_required");
        var provided = normalizedBackupId ?? backupPath;
        if (string.IsNullOrWhiteSpace(provided)) throw new InvalidOperationException("backup_locator_required");
        if (provided.Length > MaxRestorePathChars) throw new InvalidOperationException($"backup_locator_too_long>{MaxRestorePathChars}");
        var backupDir = Path.GetFullPath(Path.Combine(BridgeCore.AppDir(), "backups"));
        var candidate = provided.Contains(Path.DirectorySeparatorChar) || provided.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(provided)
            : Path.GetFullPath(Path.Combine(backupDir, provided));
        if (!candidate.StartsWith(backupDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("backup_path_not_allowed");
        if (!File.Exists(candidate)) throw new InvalidOperationException("backup_not_found");
        return candidate;
    }

    internal static Session OpenSingleModelSession()
    {
        var port = FindSingleMsmdsrvPort();
        var server = new Server();
        try
        {
            server.Connect($"DataSource=localhost:{port}");
            if (server.Databases.Count != 1)
                throw new InvalidOperationException("expected_exactly_one_model");
            var db = server.Databases[0];
            var model = db.Model ?? throw new InvalidOperationException("model_not_available");
            return new Session(server, db, model, port);
        }
        catch
        {
            try { server.Disconnect(); } catch { }
            server.Dispose();
            throw;
        }
    }

    internal static int FindSingleMsmdsrvPort()
    {
        var msmdsrv = Process.GetProcessesByName("msmdsrv");
        if (msmdsrv.Length != 1) throw new InvalidOperationException("expected_exactly_one_msmdsrv_process");
        var pid = msmdsrv[0].Id;
        var listeners = FindListeningPorts(pid);
        if (listeners.Count != 1) throw new InvalidOperationException("expected_exactly_one_msmdsrv_listener");
        return listeners[0];
    }

    internal static List<int> FindListeningPorts(int pid)
    {
        using var net = Process.Start(new ProcessStartInfo("netstat", "-ano -p tcp")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("netstat_start_failed");
        var text = net.StandardOutput.ReadToEnd();
        net.WaitForExit(3000);
        var ports = new List<int>();
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || !int.TryParse(parts[4], out var ownerPid) || ownerPid != pid) continue;
            if (Uri.TryCreate("tcp://" + parts[1], UriKind.Absolute, out var endpoint)) ports.Add(endpoint.Port);
        }
        return ports.Distinct().OrderBy(static x => x).ToList();
    }

    internal static string BuildMTableExpression(IReadOnlyList<DataColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        static string QText(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        static bool IsSimpleIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (!(char.IsLetter(value[0]) || value[0] == '_')) return false;
            for (var i = 1; i < value.Length; i++)
            {
                var ch = value[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
            }
            return true;
        }
        static string MIdentifier(string value) => IsSimpleIdentifier(value) ? value : "#\"" + value.Replace("\"", "\"\"") + "\"";
        static string MValue(object? value) => value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            DateTime dt => $"#datetime({dt.Year},{dt.Month},{dt.Day},{dt.Hour},{dt.Minute},{dt.Second})",
            string x => QText(x),
            double x => x.ToString("R", CultureInfo.InvariantCulture),
            float x => x.ToString("R", CultureInfo.InvariantCulture),
            decimal x => x.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
        };
        var schema = "type table [" + string.Join(", ", columns.Select(c => $"{MIdentifier(c.Name)} = {ToMType(c.DataType)}")) + "]";
        var data = "{" + string.Join(",", rows.Select(row => "{" + string.Join(",", columns.Select(c => MValue(row[c.Name]))) + "}")) + "}";
        return $"let Source = #table({schema}, {data}) in Source";
    }

    internal static IReadOnlyDictionary<string, object?> NormalizeRow(Dictionary<string, JsonElement> row, IReadOnlyList<DataColumn> columns)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (!row.TryGetValue(column.Name, out var value))
                throw new InvalidOperationException($"missing_column:{column.Name}");
            result[column.Name] = ConvertJsonValue(value, column.DataType, column.Name);
        }
        foreach (var key in row.Keys)
            if (!columns.Any(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"unknown_column:{key}");
        return result;
    }

    static object? ConvertJsonValue(JsonElement value, DataType dataType, string column)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        try
        {
            return dataType switch
            {
                DataType.String => value.GetString() ?? string.Empty,
                DataType.Int64 => value.ValueKind == JsonValueKind.Number ? value.GetInt64() : long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                DataType.Double => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                DataType.Decimal => value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                DataType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : bool.Parse(value.GetString()!),
                DataType.DateTime => value.ValueKind == JsonValueKind.String ? DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : value.GetDateTime(),
                _ => throw new InvalidOperationException($"unsupported_column_type:{column}:{dataType}")
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"invalid_value:{column}", ex);
        }
    }

    static string ToMType(DataType dataType)
        => dataType switch
        {
            DataType.String => "text",
            DataType.Int64 => "Int64.Type",
            DataType.Double => "number",
            DataType.Decimal => "number",
            DataType.Boolean => "logical",
            DataType.DateTime => "datetime",
            _ => throw new InvalidOperationException($"unsupported_data_type:{dataType}")
        };

    static Table FindTable(Model model, string name)
        => model.Tables.Find(name) ?? throw new InvalidOperationException("table_not_found");

    static Partition FindPartition(Table table, string name)
        => table.Partitions.Find(name) ?? throw new InvalidOperationException("partition_not_found");

    static DataColumn FindDataColumn(Table table, string name)
        => table.Columns.Find(name) as DataColumn ?? throw new InvalidOperationException("column_not_found");

    static void ValidateRelationshipColumns(DataColumn fromColumn, DataColumn toColumn)
    {
        if (fromColumn.DataType != toColumn.DataType)
            throw new InvalidOperationException("relationship_column_type_mismatch");
        if (ReferenceEquals(fromColumn, toColumn))
            throw new InvalidOperationException("relationship_self_column_not_allowed");
    }

    static string RequireName(string? value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) throw new InvalidOperationException($"{field}_required");
        return trimmed;
    }

    static string ValidateName(string? value, string field, int maxLen)
    {
        var trimmed = RequireName(value, field);
        if (trimmed.Length > maxLen) throw new InvalidOperationException($"{field}_too_long>{maxLen}");
        return trimmed;
    }

    static string ValidateExpression(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("expression_required");
        if (value!.Length > maxLen) throw new InvalidOperationException($"expression_too_long>{maxLen}");
        return value;
    }

    static List<Dictionary<string, JsonElement>> ValidateRows(List<Dictionary<string, JsonElement>>? rows)
    {
        if (rows is null) throw new InvalidOperationException("rows_required");
        if (rows.Count == 0) throw new InvalidOperationException("rows_required");
        if (rows.Count > MaxRowsPerOperation) throw new InvalidOperationException($"too_many_rows>{MaxRowsPerOperation}");
        foreach (var row in rows)
            if (row.Count > MaxRowCells) throw new InvalidOperationException($"too_many_cells>{MaxRowCells}");
        return rows;
    }

    static List<Dictionary<string, JsonElement>> ValidateCreateTableRows(List<Dictionary<string, JsonElement>>? rows)
    {
        rows ??= [];
        if (rows.Count > MaxTableRows) throw new InvalidOperationException($"too_many_rows>{MaxTableRows}");
        foreach (var row in rows)
            if (row.Count > MaxRowCells) throw new InvalidOperationException($"too_many_cells>{MaxRowCells}");
        return rows;
    }

    static List<ColumnSpec> ValidateColumns(List<ColumnSpec>? columns)
    {
        if (columns is null) throw new InvalidOperationException("columns_required");
        if (columns.Count == 0) throw new InvalidOperationException("columns_required");
        if (columns.Count > MaxTableColumns) throw new InvalidOperationException($"too_many_columns>{MaxTableColumns}");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ColumnSpec>(columns.Count);
        foreach (var column in columns)
        {
            var name = ValidateName(column.name, "column", MaxColumnNameChars);
            var dataType = ValidateDataTypeName(column.dataType);
            if (!seen.Add(name)) throw new InvalidOperationException($"duplicate_column:{name}");
            normalized.Add(new ColumnSpec(name, dataType));
        }
        return normalized;
    }

    static string ValidateDataTypeName(string? value)
    {
        var name = RequireName(value, "dataType");
        _ = ParseDataType(name);
        return name;
    }

    static string ValidateCrossFilteringBehavior(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? CrossFilteringBehavior.OneDirection.ToString() : value!;
        _ = ParseCrossFilteringBehavior(normalized);
        return normalized;
    }

    static string ValidateRelationshipName(string? value)
        => ValidateName(value, "relationship", MaxRelationshipNameChars);

    static string? ValidateRestoreLocator(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value!.Length > MaxRestorePathChars) throw new InvalidOperationException($"{field}_too_long>{MaxRestorePathChars}");
        return value;
    }

    static DataType ParseDataType(string name)
        => Enum.TryParse<DataType>(name, ignoreCase: true, out var dt)
            ? dt
            : throw new InvalidOperationException($"unsupported_data_type:{name}");

    static CrossFilteringBehavior ParseCrossFilteringBehavior(string name)
        => Enum.TryParse<CrossFilteringBehavior>(name, ignoreCase: true, out var behavior)
            ? behavior
            : throw new InvalidOperationException($"unsupported_cross_filtering_behavior:{name}");

    static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };

    sealed class BatchState
    {
        internal HashSet<string> TouchedTables { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed record PreparedOperation(
        string Type,
        string? Table = null,
        string? Partition = null,
        string? Measure = null,
        string? Expression = null,
        string? FormatString = null,
        string? DisplayFolder = null,
        bool? Hidden = null,
        List<Dictionary<string, JsonElement>>? Rows = null,
        List<ColumnSpec>? Columns = null,
        string? Column = null,
        string? DataType = null,
        string? FromTable = null,
        string? FromColumn = null,
        string? ToTable = null,
        string? ToColumn = null,
        string? Relationship = null,
        bool? IsActive = null,
        string? CrossFilteringBehavior = null,
        string? BackupId = null,
        string? BackupPath = null,
        OperationRequest? Raw = null);
}
