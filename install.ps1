$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'BoBIPet chỉ hỗ trợ Windows.' }
$repo = 'hoanghaole/bo-powerbi-pet'
$dir = Join-Path $env:LOCALAPPDATA 'BoBIPet'
$tmp = Join-Path $env:TEMP ("BoBIPet-" + [guid]::NewGuid())
$headers = @{ 'User-Agent' = 'BoBIPet-installer' }
$base = "https://github.com/$repo/releases/latest/download"
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
  $zip = Join-Path $tmp 'BoBIPet-win-x64.zip'
  $sums = Join-Path $tmp 'SHA256SUMS'
  Invoke-WebRequest "$base/BoBIPet-win-x64.zip" -OutFile $zip -Headers $headers
  Invoke-WebRequest "$base/SHA256SUMS" -OutFile $sums -Headers $headers
  $expected = ((Get-Content $sums -Raw) -split '\s+')[0].ToLowerInvariant()
  $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) { throw "SHA-256 không khớp: $actual" }
  Get-Process BoBIPet, BoPowerBIPet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 3
  # Retry đóng process phòng khi handle chưa nhả kịp
  Get-Process BoBIPet, BoPowerBIPet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
  # Nếu vẫn còn process sống, thử TaskKill /T /F (kill cả cây con)
  if (Get-Process BoBIPet, BoPowerBIPet -ErrorAction SilentlyContinue) {
    Start-Sleep -Seconds 2
    taskkill /F /T /IM BoBIPet.exe /IM BoPowerBIPet.exe 2>$null | Out-Null
    Start-Sleep -Seconds 2
  }
  # Fallback: nếu file exe cũ vẫn bị lock, đổi tên nó (rename không cần quyền ghi đè file đang chạy)
  $oldExe = Join-Path $dir 'BoBIPet.exe'
  if ((Test-Path $oldExe) -and $ENV:OS -eq 'Windows_NT') {
    try { Rename-Item $oldExe ($oldExe + '.old') -Force -ErrorAction Stop } catch { }
  }
  New-Item -ItemType Directory -Force $dir | Out-Null
  Remove-Item (Join-Path $dir 'access.txt') -Force -ErrorAction SilentlyContinue
  Expand-Archive $zip -DestinationPath $dir -Force
  # Dọn exe cũ đã rename
  Remove-Item ($oldExe + '.old') -Force -ErrorAction SilentlyContinue
  $exe = Join-Path $dir 'BoBIPet.exe'
  if (-not (Test-Path $exe)) { throw 'Không tìm thấy BoBIPet.exe sau giải nén.' }
  $desktop = [Environment]::GetFolderPath('Desktop')
  $shell = New-Object -ComObject WScript.Shell
  $shortcut = $shell.CreateShortcut((Join-Path $desktop 'BoBIPet.lnk'))
  $shortcut.TargetPath = $exe
  $shortcut.WorkingDirectory = $dir
  $shortcut.Save()
  Start-Process $exe
  Write-Host 'BoBIPet đã cài và chạy.' -ForegroundColor Green
} finally {
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
