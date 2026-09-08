$keyPath = Join-Path $env:LOCALAPPDATA 'FlashNextManager\api-key.txt'
if (-not (Test-Path -LiteralPath $keyPath)) { throw 'Start FlashNext Manager once before configuring OpenCode.' }
$env:FLASHNEXT_API_KEY = (Get-Content -LiteralPath $keyPath -Raw).Trim()
Write-Host 'FLASHNEXT_API_KEY is set for this PowerShell process. Start OpenCode from this window.'
