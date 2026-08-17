$ErrorActionPreference = 'Stop'
$script = Invoke-RestMethod 'https://raw.githubusercontent.com/hoanghaole/bo-powerbi-pet/main/install.ps1' -Headers @{ 'User-Agent' = 'BoBIPet-updater' }
& ([scriptblock]::Create($script))

# Đợi app ghi access.txt (URL + token) rồi in ra — khỏi bấm nút Copy
$dir = Join-Path $env:LOCALAPPDATA 'BoBIPet'
$acc = Join-Path $dir 'access.txt'
for ($i = 0; $i -lt 45; $i++) {
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
