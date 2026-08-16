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
  Get-Process BoBIPet, BoPowerBIPet -ErrorAction SilentlyContinue | Stop-Process -Force
  New-Item -ItemType Directory -Force $dir | Out-Null
  Expand-Archive $zip -DestinationPath $dir -Force
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
