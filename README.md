# BoBIPet

Bridge Windows tối giản để Claude Desktop/OpenClaw gọi Power BI Desktop qua HTTP có bearer token.

Bản 4.0.5 hỗ trợ tạo relationship tới cột của calculated table như `Ngày[Date]`.

## Cài đặt (duy nhất)

### Cài mới
```powershell
iex (irm https://raw.githubusercontent.com/hoanghaole/bo-powerbi-pet/main/install.ps1)
```

Script sẽ:
- tải `BoBIPet-win-x64.zip` từ GitHub Release mới nhất
- tải `SHA256SUMS`, xác minh SHA-256 trước khi giải nén
- đóng process cũ `BoBIPet`/`BoPowerBIPet` nếu đang chạy
- cài vào `%LOCALAPPDATA%\BoBIPet` và chạy `BoBIPet.exe`

### Cập nhật
```powershell
iex (irm https://raw.githubusercontent.com/hoanghaole/bo-powerbi-pet/main/update.ps1)
```

(update.ps1 chỉ gọi lại install.ps1 mới nhất)

## API giữ lại
Cần header:

```http
Authorization: Bearer <token>
```

Endpoint:
- `GET /health`
- `GET /powerbi/processes`
- `GET /powerbi/listeners`
- `GET /powerbi/model-summary`
- `POST /powerbi/dax` với body JSON: `{"query":"EVALUATE ..."}`
- `POST /powerbi/hr-sample` body rỗng hoặc JSON bất kỳ; endpoint tự kiểm tra 3 calculated tables `HR Nhân viên`, `HR Tuyển dụng`, `HR Đào tạo`, backup expression cũ vào `%LOCALAPPDATA%\BoBIPet\backups\*.json`, rồi thay bằng dữ liệu HR mẫu deterministic giữ nguyên schema/relationships/measures
- `POST /v1/powerbi/model/inspect` trả metadata typed + `fingerprint`
- `POST /v1/powerbi/model/operations` nhận batch typed; chỉ hỗ trợ:
  - `import_sample_rows` cho partition `MPartitionSource` đã tồn tại
  - `create_table`
  - `delete_table` (`destructiveConfirm: "DELETE"` khi apply)
  - `create_measure`
  - `update_measure`
  - `delete_measure`
  - `create_calculated_column`
  - `update_calculated_column`
  - `delete_calculated_column`
  - `create_relationship`
  - `delete_relationship`
  - `update_partition_expression` (M only)
  - `restore` từ `backupId` basename-only hoặc `backupPath` allowlisted dưới `%LOCALAPPDATA%\BoBIPet\backups`

## Typed authoring contract
`POST /v1/powerbi/model/inspect`

Response tối thiểu:
```json
{
  "ok": true,
  "port": 49783,
  "fingerprint": "64hex...",
  "model": {
    "name": "Model",
    "id": "...",
    "compatibilityLevel": 1565,
    "counts": { "tables": 1, "columns": 3, "measures": 2, "calculatedColumns": 1, "relationships": 0, "partitions": 1 },
    "tables": [{ "name": "Sales", "hidden": false, "partitions": [{ "name": "Sales", "sourceType": "MPartitionSource", "expression": "let ..." }], "columns": ["Amount"] }],
    "measures": [{ "table": "Sales", "name": "Revenue", "hidden": false, "formatString": "#,0", "displayFolder": "KPIs", "expression": "SUM(Sales[Amount])" }],
    "calculatedColumns": [{ "table": "Sales", "name": "Net", "hidden": false, "formatString": null, "displayFolder": null, "expression": "[Amount] * 0.9", "dataType": "Decimal" }],
    "relationships": []
  }
}
```

`POST /v1/powerbi/model/operations`

- `dryRun` mặc định `true`
- apply thật bắt buộc có `port` + `expectedFingerprint` khớp inspect hiện tại
- batch được validate toàn bộ trước mutation
- `Program.Handle` chặn mọi route/method ngoài allowlist `BridgeCore.IsAllowedRoute`
- operation destructive (`delete_*`, `restore`) bắt buộc `destructiveConfirm: "DELETE"`
- backup model state vào `%LOCALAPPDATA%\BoBIPet\backups\authoring-*.json` trước khi apply
- fingerprint gồm measures + calculated columns + M partition expressions + data column datatype/hidden/key + relationship cardinality/behavior
- `SaveChanges()` gọi đúng 1 lần mỗi batch apply
- nếu `SaveChanges()` lỗi: restore fail-closed từ backup rồi `SaveChanges()` lại; không fallback `RequestRefresh(Full)` giả rollback
- topology batch (`create/delete_table`, `create/delete_relationship`, `restore`) bị cấm đi chung op khác trong v4 MVP
- lỗi trả root error, không expose generic PowerShell/TOM reflection surface
- giới hạn hiện tại:
  - body <= 256 KB
  - tối đa 100 operations/batch
  - tối đa 200 rows cho mỗi `import_sample_rows`
  - tối đa 64 cells/row

Ví dụ dry run:
```json
{
  "operations": [
    {
      "type": "create_measure",
      "table": "Sales",
      "measure": "Revenue",
      "expression": "SUM(Sales[Amount])",
      "formatString": "#,0"
    }
  ]
}
```

Ví dụ apply:
```json
{
  "port": 49783,
  "expectedFingerprint": "<inspect fingerprint>",
  "dryRun": false,
  "destructiveConfirm": "DELETE",
  "operations": [
    {
      "type": "delete_measure",
      "table": "Sales",
      "measure": "Revenue"
    }
  ]
}
```

## Cách chạy
Sau khi cài, chạy `BoBIPet.exe` (đã nằm trong `%LOCALAPPDATA%\BoBIPet`). App hiển thị:
- URL `https://...trycloudflare.com`
- token hiện tại
- port local đang dùng

Nút `Copy URL + Token` copy 2 dòng: URL public, token.

## Phát hành
Tạo tag `vX.Y.Z`. GitHub Actions sẽ:
1. chạy contract test .NET + test Node
2. `dotnet publish` win-x64 self-contained single-file tạo `BoBIPet.exe`
3. zip artifact `BoBIPet-win-x64.zip`
4. tạo `SHA256SUMS`
5. publish GitHub Release

Phân phối chính thức là GitHub Release; `install.ps1`/`update.ps1` là đường cài/update duy nhất.

## Build local
```bash
dotnet publish BoPowerBIPet.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
```

## Test local
```bash
dotnet run --project BoPowerBIPet.Contracts.csproj
npm test
```

## Giới hạn còn lại
- `POST /powerbi/hr-sample` phụ thuộc Power BI Desktop đang mở + `Microsoft.AnalysisServices.Tabular.dll` trong thư mục cài Power BI Desktop.
- Endpoint HR sample chỉ hỗ trợ 3 table calculated có schema còn tương thích; nếu user đổi tên column/table thì endpoint sẽ từ chối trước khi `SaveChanges`.
- Vẫn phụ thuộc Power BI Desktop + `Microsoft.PowerBI.AdomdClient.dll` trên máy user.
- Chưa có chữ ký code.
