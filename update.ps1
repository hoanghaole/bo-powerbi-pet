$ErrorActionPreference = 'Stop'
$package = 'bobipet'

if (-not $IsWindows) { throw 'BoBIPet chỉ hỗ trợ Windows.' }
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Cần npm trong PATH để cập nhật bobipet.' }

npm update -g $package
if ($LASTEXITCODE -ne 0) { throw "npm update -g $package thất bại." }

$cmd = Get-Command bobipet -ErrorAction Stop
& $cmd.Source
