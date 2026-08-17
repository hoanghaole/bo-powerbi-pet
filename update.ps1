$ErrorActionPreference = 'Stop'
# Lấy tag release mới nhất (không dùng main — tránh script lệch version với app)
$repo = 'hoanghaole/bo-powerbi-pet'
$headers = @{ 'User-Agent' = 'BoBIPet-updater' }
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
$tag = $release.tag_name
Write-Host "Cập nhật BoBIPet lên $tag ..." -ForegroundColor Cyan
$script = Invoke-RestMethod "https://raw.githubusercontent.com/$repo/$tag/install.ps1" -Headers $headers
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
