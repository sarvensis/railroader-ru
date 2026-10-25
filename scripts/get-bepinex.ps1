<#
    Скачивает BepInEx в vendor/bepinex (если его там ещё нет) и проверяет контрольную сумму архива.
        pwsh scripts/get-bepinex.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$Version = '5.4.23.5'
$Sha256 = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'
$Url = "https://github.com/BepInEx/BepInEx/releases/download/v$Version/BepInEx_win_x64_$Version.zip"

$vendor = Join-Path $root 'vendor'
$target = Join-Path $vendor 'bepinex'
$marker = Join-Path $target 'BepInEx/core/BepInEx.dll'
if (Test-Path $marker) {
    Write-Host "BepInEx уже лежит в $target"
    return
}

New-Item -ItemType Directory -Force -Path $vendor | Out-Null
$zip = Join-Path $vendor "BepInEx_win_x64_$Version.zip"
if (-not (Test-Path $zip)) {
    Write-Host "Скачиваю $Url"
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
}
$actual = (Get-FileHash -Algorithm SHA256 $zip).Hash
if ($actual -ne $Sha256) {
    Remove-Item $zip -Force
    throw "Контрольная сумма BepInEx не совпала: $actual (ожидалось $Sha256)"
}
Expand-Archive -Path $zip -DestinationPath $target -Force
Write-Host "BepInEx $Version распакован в $target"
