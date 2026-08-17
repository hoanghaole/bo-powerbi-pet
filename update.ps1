$ErrorActionPreference = 'Stop'
# Tải install.ps1 — ưu tiên jsDelivr CDN (không bị rate limit raw.githubusercontent), fallback raw
$repo = 'hoanghaole/bo-powerbi-pet'
$script = $null
foreach ($u in @(
  "https://cdn.jsdelivr.net/gh/$repo@main/install.ps1",
  "https://raw.githubusercontent.com/$repo/main/install.ps1"
)) {
  try {
    $script = Invoke-RestMethod $u -Headers @{ 'User-Agent' = 'BoBIPet-updater' }
    break
  } catch {
    Write-Host "Thử nguồn khác (429/thất bại): $u" -ForegroundColor DarkGray
  }
}
if (-not $script) { throw 'Không tải được install.ps1 (jsDelivr + raw đều lỗi). Thử lại sau 5 phút.' }
& ([scriptblock]::Create($script))

# Đợi app ghi access.txt (URL + token) rồi in ra — khỏi bấm nút Copy
$dir = Join-Path $env:LOCALAPPDATA 'BoBIPet'
$acc = Join-Path $dir 'access.txt'
for ($i = 0; $i -lt 60; $i++) {
  Start-Sleep -Seconds 1
  if (Test-Path $acc) {
    Write-Host ''
    Write-Host '=== URL + TOKEN (dán nguyên 2 dòng cho Bơ) ===' -ForegroundColor Cyan
    Get-Content $acc
    Write-Host ''
    Write-Host 'BoBIPet sẵn sàng. Bấm Enter để đóng cửa sổ này.' -ForegroundColor Green
    break
  }
}
if (-not (Test-Path $acc)) {
  Write-Host 'Chưa thấy access.txt — mở cửa sổ BoBIPet (khay hệ thống) và bấm "Copy URL + Token".' -ForegroundColor Yellow
}
