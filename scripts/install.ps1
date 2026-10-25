<#
    Установка русификатора Railroader.
    Запуск: двойной щелчок по INSTALL.bat или
        powershell -ExecutionPolicy Bypass -File install.ps1 [-GamePath "D:\Games\Railroader"]
#>
param(
    [string]$GamePath
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$PluginName = 'RailroaderRu'

. (Join-Path $here 'common.ps1')

Write-Host ''
Write-Host '=== Русификатор Railroader ===' -ForegroundColor Cyan
Write-Host ''

if (-not $GamePath) { $GamePath = Find-Game }
while (-not $GamePath -or -not (Test-Path (Join-Path $GamePath 'Railroader.exe'))) {
    if ($GamePath) { Write-Host "В папке «$GamePath» нет Railroader.exe." -ForegroundColor Yellow }
    else { Write-Host 'Не удалось автоматически найти игру в библиотеках Steam.' -ForegroundColor Yellow }
    $GamePath = Read-Host 'Введите путь к папке игры (где лежит Railroader.exe), или пустую строку для выхода'
    if (-not $GamePath) { exit 1 }
    $GamePath = $GamePath.Trim('"', ' ')
}
Write-Host "Папка игры: $GamePath"

if (Get-Process -Name 'Railroader' -ErrorAction SilentlyContinue) {
    Write-Host 'Игра сейчас запущена. Закройте Railroader и запустите установку снова.' -ForegroundColor Red
    exit 1
}

# 1. BepInEx — ставим, только если его ещё нет (чтобы не сломать чужие моды).
$bepCore = Join-Path $GamePath 'BepInEx\core\BepInEx.dll'
if (Test-Path $bepCore) {
    Write-Host 'BepInEx уже установлен — оставляю как есть.'
} else {
    Write-Host 'Устанавливаю BepInEx...'
    foreach ($item in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
        $src = Join-Path $here $item
        if (Test-Path $src) { Copy-Item $src -Destination $GamePath -Force }
    }
    $coreDst = Join-Path $GamePath 'BepInEx\core'
    New-Item -ItemType Directory -Force -Path $coreDst | Out-Null
    Copy-Item (Join-Path $here 'BepInEx\core\*') -Destination $coreDst -Recurse -Force
}

# 2. Плагин. Файл untranslated.txt (если вы собирали непереведённые строки) сохраняем.
$pluginSrc = Join-Path $here "BepInEx\plugins\$PluginName"
$pluginDst = Join-Path $GamePath "BepInEx\plugins\$PluginName"
$keep = Join-Path $pluginDst 'untranslated.txt'
$backup = $null
if (Test-Path $keep) {
    $backup = Join-Path $env:TEMP "railroaderru-untranslated-$([guid]::NewGuid()).txt"
    Copy-Item $keep $backup
}
if (Test-Path $pluginDst) { Remove-Item $pluginDst -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pluginDst | Out-Null
Copy-Item (Join-Path $pluginSrc '*') -Destination $pluginDst -Recurse -Force
if ($backup) { Move-Item $backup $keep -Force }

Write-Host ''
Write-Host 'Готово! Русификатор установлен.' -ForegroundColor Green
Write-Host 'Запустите игру через Steam. Первый запуск с BepInEx может быть чуть дольше обычного.'
Write-Host 'Настройки: BepInEx\config\ru.railroader.localization.cfg (появится после первого запуска).'
Write-Host ''
