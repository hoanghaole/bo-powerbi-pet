$ErrorActionPreference = 'Stop'
# BoBIPet self-contained updater — tải từ GitHub release assets (objects.githubusercontent.com CDN, không dính 429 raw)
if ($env:OS -ne 'Windows_NT') { throw 'BoBIPet chỉ hỗ trợ Windows.' }
$repo = 'hoanghaole/bo-powerbi-pet'
$dir = Join-Path $env:LOCALAPPDATA 'BoBIPet'
$tmp = Join-Path $env:TEMP ("BoBIPet-" + [guid]::NewGuid())
$headers = @{ 'User-Agent' = 'BoBIPet-updater' }
$base = "https://github.com/$repo/releases/latest/download"
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
  Write-Host 'Tai BoBIPet...' -ForegroundColor Cyan
  $zip = Join-Path $tmp 'BoBIPet-win-x64.zip'
  $sums = Join-Path $tmp 'SHA256SUMS'
  Invoke-WebRequest "$base/BoBIPet-win-x64.zip" -OutFile $zip -Headers $headers
  Invoke-WebRequest "$base/SHA256SUMS" -OutFile $sums -Headers $headers
  $expected = ((Get-Content $sums -Raw) -split '\s+')[0].ToLowerInvariant()
  $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) { throw "SHA-256 khong khop: $actual" }
  Write-Host 'Dong app cu...' -ForegroundColor Cyan
  Get-Process BoBIPet, BoPowerBIPet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 3
  Get-Process BoBIPet, BoPowerBIPet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
  if (Get-Process BoBIPet, BoPowerBIPet -ErrorAction SilentlyContinue) {
    Start-Sleep -Seconds 2
    taskkill /F /T /IM BoBIPet.exe 2>$null | Out-Null
    taskkill /F /T /IM BoPowerBIPet.exe 2>$null | Out-Null
    Start-Sleep -Seconds 2
  }
  $oldExe = Join-Path $dir 'BoBIPet.exe'
  if ((Test-Path $oldExe) -and $ENV:OS -eq 'Windows_NT') {
    try { Rename-Item $oldExe ($oldExe + '.old') -Force -ErrorAction Stop } catch { }
  }
  New-Item -ItemType Directory -Force $dir | Out-Null
  Remove-Item (Join-Path $dir 'access.txt') -Force -ErrorAction SilentlyContinue
  Expand-Archive $zip -DestinationPath $dir -Force
  Remove-Item ($oldExe + '.old') -Force -ErrorAction SilentlyContinue
  $exe = Join-Path $dir 'BoBIPet.exe'
  if (-not (Test-Path $exe)) { throw 'Khong tim thay BoBIPet.exe sau giai nen.' }
  $desktop = [Environment]::GetFolderPath('Desktop')
  $shell = New-Object -ComObject WScript.Shell
  $shortcut = $shell.CreateShortcut((Join-Path $desktop 'BoBIPet.lnk'))
  $shortcut.TargetPath = $exe
  $shortcut.WorkingDirectory = $dir
  $shortcut.Save()
  Start-Process $exe
  Write-Host 'Da cai. Cho app chay...' -ForegroundColor Cyan
  $acc = Join-Path $dir 'access.txt'
  for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path $acc) {
      Write-Host ''
      Write-Host '=== URL + TOKEN (dan nguyen 2 dong cho Bo) ===' -ForegroundColor Cyan
      Get-Content $acc
      Write-Host ''
      Write-Host 'BoBIPet san sang. Bam Enter de dong cua so nay.' -ForegroundColor Green
      break
    }
  }
  if (-not (Test-Path $acc)) {
    Write-Host 'Chua thay access.txt - mo BoBIPet (khay he thong) va bam "Copy URL + Token".' -ForegroundColor Yellow
  }
} finally {
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
