param(
  [int]$Port = 63401,
  [string]$ProfilePath = (Join-Path $env:LOCALAPPDATA 'LllmFacade\chatgpt-browser')
)

$ErrorActionPreference = 'Stop'

$chrome = Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
$browser = if (Test-Path $chrome) { $chrome } elseif (Test-Path $edge) { $edge } else { $null }
if (!$browser) {
  throw 'Chrome o Edge non trovato. Aprire manualmente un profilo dedicato con --remote-debugging-port=63401.'
}

New-Item -ItemType Directory -Force $ProfilePath | Out-Null
$endpoint = "http://127.0.0.1:$Port/json/version"
$alreadyListening = $false
try {
  Invoke-RestMethod -Uri $endpoint -TimeoutSec 1 | Out-Null
  $alreadyListening = $true
} catch {
  # No browser is listening yet: expected on the first launch.
}
if ($alreadyListening) {
  throw "La porta CDP $Port è già in uso: chiudere prima il browser dedicato."
}

# This is a normal, visible Chromium window. The user signs in or completes any verification manually.
Start-Process -FilePath $browser -ArgumentList @(
  "--remote-debugging-port=$Port",
  "--user-data-dir=$ProfilePath",
  '--profile-directory=Default',
  '--new-window',
  'https://chatgpt.com'
)

$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
  try {
    Invoke-RestMethod -Uri $endpoint -TimeoutSec 1 | Out-Null
    Write-Host "Browser aperto. Accedi a ChatGPT nel suo profilo."
    Write-Host "Nel file .env imposta: BROWSER_CDP_URL=http://host.docker.internal:$Port"
    Write-Host "Poi esegui: docker compose up -d --force-recreate api"
    exit 0
  } catch {
    Start-Sleep -Milliseconds 250
  }
}

throw "Il browser non ha esposto CDP su 127.0.0.1:$Port entro il timeout."
