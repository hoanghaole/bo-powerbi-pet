# BoBIPet

Bridge Windows tối giản để Claude Desktop/OpenClaw gọi Power BI Desktop qua HTTP có bearer token.

## Phân phối chính
`bobipet` là npm package chính. Cài global:

```powershell
npm install -g bobipet
```

Lần chạy đầu `bobipet` hoặc `BoBIPet` sẽ:
- tải `BoBIPet-win-x64.zip` đúng version từ GitHub Release `hoanghaole/bo-powerbi-pet`
- tải `SHA256SUMS`, xác minh SHA-256 trước khi giải nén
- cài vào npm-managed prefix nếu có, fallback `%LOCALAPPDATA%\BoBIPet\npm`
- chạy `BoBIPet.exe`

Cập nhật:

```powershell
npm update -g bobipet
```

Xem version package:

```powershell
bobipet --version
```

Non-Windows trả lỗi rõ. PowerShell installer giữ lại làm fallback.

## Fallback PowerShell
### Cài mới
```powershell
irm https://raw.githubusercontent.com/hoanghaole/bo-powerbi-pet/main/install.ps1 | iex
```

### Cập nhật
```powershell
irm https://raw.githubusercontent.com/hoanghaole/bo-powerbi-pet/main/update.ps1 | iex
```

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

## Cách chạy
Sau khi cài, chạy `bobipet` hoặc `BoBIPet`. App hiển thị:
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
6. publish npm package `bobipet`

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
- Vẫn phụ thuộc Power BI Desktop + `Microsoft.PowerBI.AdomdClient.dll` trên máy user.
- Chưa có chữ ký code.
- npm wrapper dùng PowerShell `Expand-Archive` trên Windows; muốn bỏ phụ thuộc này thì thêm extractor stdlib/native khác.
