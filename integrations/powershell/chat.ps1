$ErrorActionPreference = 'Stop'
$keyPath = Join-Path $env:LOCALAPPDATA 'FlashNextManager\api-key.txt'
$key = if ($env:FLASHNEXT_API_KEY) { $env:FLASHNEXT_API_KEY } else { (Get-Content -LiteralPath $keyPath -Raw).Trim() }
$body = @{
    model = 'Qwen3.8-Flash-Next'
    messages = @(@{ role = 'system'; content = 'You are a precise coding assistant.' }, @{ role = 'user'; content = 'Give one PowerShell security recommendation.' })
    stream = $false
    temperature = 0.2
    max_tokens = 512
    chat_template_kwargs = @{ enable_thinking = $false }
} | ConvertTo-Json -Depth 8
$response = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/v1/chat/completions' -Method Post -Headers @{ Authorization = ('Bearer ' + $key) } -ContentType 'application/json' -Body $body -TimeoutSec 1800
$response.choices[0].message.content
