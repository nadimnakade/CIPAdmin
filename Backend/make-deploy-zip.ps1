$ErrorActionPreference = 'Stop'
Set-Location 'D:\Git_Repos\PDF\CIPAdmin\Backend'

$src = 'publish'
$stage = '_deploy_stage'
$zip = 'publish\CIPAdmin-deploy.zip'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# The staging folder mirrors publish/, minus the exclusions below.
robocopy $src $stage /E /NFL /NDL /NJH /NJS /NP | Out-Null

# pdf files / ocr uploads / logs are runtime content, not deploy payload.
foreach ($d in @('pdf files', 'ocr uploads', 'logs')) {
    $p = Join-Path $stage $d
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
Get-ChildItem $stage -Filter 'CIPAdmin-deploy.zip' -Recurse -File | Remove-Item -Force

# appsettings.Production.json is a TEMPLATE with placeholders
# (Server=<PROD-SQL-HOST>, <paste-your-x-api-token>). Shipping it would overwrite
# the live production secrets with placeholders and take the portal down.
$prod = Join-Path $stage 'appsettings.Production.json'
if (Test-Path $prod) { Remove-Item $prod -Force; Write-Host 'excluded appsettings.Production.json (template, not real secrets)' }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal

$info = Get-Item $zip
Write-Host ("ZIP {0} bytes" -f $info.Length)
Write-Host ("entries {0}" -f (Get-ChildItem $stage -Recurse -File).Count)
Write-Host ("zip present in stage: {0}" -f (Test-Path (Join-Path $stage 'CIPAdmin-deploy.zip')))

Remove-Item $stage -Recurse -Force
