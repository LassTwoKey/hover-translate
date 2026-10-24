<#
.SYNOPSIS
  Собирает HoverTranslate целиком: тесты, publish, портативный zip, установщик.
.DESCRIPTION
  Результат — в artifacts\dist: HoverTranslate-Setup-<версия>.exe, HoverTranslate-<версия>-portable-win-x64.zip
  и SHA256SUMS.txt. Нужны готовые данные в data\ (tools\DataBuilder) и Inno Setup 6 (для установщика).
.EXAMPLE
  .\build.ps1
  .\build.ps1 -SkipTests -SkipInstaller
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$publishDir = Join-Path $repo 'artifacts\publish'
$distDir = Join-Path $repo 'artifacts\dist'

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Invoke-Native([scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "Команда завершилась с кодом ${LASTEXITCODE}: $command" }
}

if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw 'Версия не найдена в Directory.Build.props; укажите -Version.' }
}

Step "Версия $Version"

# Данные собираются отдельно (tools\DataBuilder) и в git не лежат: без них приложение не запустится.
$required = @(
    'data\dict.db',
    'data\models\opus-mt-en-ru\encoder_model.onnx',
    'data\models\opus-mt-en-ru\decoder_model.onnx',
    'data\models\opus-mt-en-ru\decoder_with_past_model.onnx',
    'data\models\opus-mt-en-ru\source.spm',
    'data\models\opus-mt-en-ru\target.spm',
    'data\models\opus-mt-en-ru\vocab.json'
)
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $repo $file))) {
        throw "Нет $file. Соберите данные: tools\DataBuilder\README.md."
    }
}

# Модели запасного OCR необязательны: без них приложение работает на одном Windows OCR.
foreach ($file in 'data\models\ppocr\det.onnx', 'data\models\ppocr\rec.onnx') {
    if (-not (Test-Path (Join-Path $repo $file))) {
        Write-Warning "Нет ${file}: запасное распознавание (PaddleOCR) не попадёт в сборку. tools\DataBuilder\export_ppocr.py"
    }
}

if (-not $SkipTests) {
    Step 'Тесты'
    Invoke-Native { dotnet test --solution (Join-Path $repo 'HoverTranslate.sln') -c $Configuration }
}

Step 'Publish (self-contained, win-x64, ReadyToRun)'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Invoke-Native {
    dotnet publish (Join-Path $repo 'src\HoverTranslate.App') -c $Configuration -r win-x64 --self-contained `
        -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -p:Version=$Version -o $publishDir
}
# Без HoverTranslate.pri (скомпилированный XAML) любое окно приложения падает с XamlParseException.
if (-not (Test-Path (Join-Path $publishDir 'HoverTranslate.pri'))) { throw 'В publish нет HoverTranslate.pri: окна приложения не откроются.' }

New-Item -ItemType Directory -Force $distDir | Out-Null
Get-ChildItem $distDir -File | Where-Object { $_.Name -like "*$Version*" -or $_.Name -eq 'SHA256SUMS.txt' } | Remove-Item -Force

Step 'Портативный zip'
$zip = Join-Path $distDir "HoverTranslate-$Version-portable-win-x64.zip"
$staging = Join-Path $repo 'artifacts\portable\HoverTranslate'
if (Test-Path (Split-Path $staging)) { Remove-Item (Split-Path $staging) -Recurse -Force }
$appDir = Join-Path $staging 'app'
New-Item -ItemType Directory -Force $appDir | Out-Null
# Сотни файлов программы прячем в app\, чтобы в корне архива не искать exe среди них.
Copy-Item (Join-Path $publishDir '*') $appDir -Recurse
Copy-Item (Join-Path $repo 'NOTICE.txt'), (Join-Path $repo 'README.md') $staging
# Запускатель: имя только из ASCII (Compress-Archive в Windows PowerShell 5.1 плохо кодирует имена файлов).
@('@echo off', 'start "" "%~dp0app\HoverTranslate.exe" %*', 'exit /b') |
    Set-Content (Join-Path $staging 'HoverTranslate.cmd') -Encoding ascii
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal
Remove-Item (Split-Path $staging) -Recurse -Force

if (-not $SkipInstaller) {
    Step 'Установщик (Inno Setup)'
    $iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 не найден: winget install JRSoftware.InnoSetup (или -SkipInstaller).' }

    Invoke-Native {
        & $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$distDir" "/DRepoDir=$repo" `
            (Join-Path $repo 'installer\HoverTranslate.iss')
    }
}

Step 'Контрольные суммы'
$sums = Get-ChildItem $distDir -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
$sums | Set-Content (Join-Path $distDir 'SHA256SUMS.txt') -Encoding ascii

Write-Host "`nГотово:" -ForegroundColor Green
Get-ChildItem $distDir -File | ForEach-Object { "{0,10:N1} МБ  {1}" -f ($_.Length / 1MB), $_.Name }
