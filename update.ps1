$ErrorActionPreference = 'Stop'
$script = Invoke-RestMethod 'https://raw.githubusercontent.com/hoanghaole/bo-powerbi-pet/main/install.ps1' -Headers @{ 'User-Agent' = 'BoBIPet-updater' }
& ([scriptblock]::Create($script))
