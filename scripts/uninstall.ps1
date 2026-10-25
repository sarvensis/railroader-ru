<#
    Удаление русификатора Railroader.
    Запуск: двойной щелчок по UNINSTALL.bat или
        powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-GamePath "..."] [-RemoveBepInEx]
#>
param(
    [string]$GamePath,
    [switch]$RemoveBepInEx
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$PluginName = 'RailroaderRu'

. (Join-Path $here 'common.ps1')

if (-not $GamePath) { $GamePath = Find-Game }
while (-not $GamePath -or -not (Test-Path (Join-Path $GamePath 'Railroader.exe'))) {
    $GamePath = Read-Host 'Введите путь к папке игры (где лежит Railroader.exe), или пустую строку для выхода'
    if (-not $GamePath) { exit 1 }
    $GamePath = $GamePath.Trim('"', ' ')
}

if (Get-Process -Name 'Railroader' -ErrorAction SilentlyContinue) {
    Write-Host 'Игра сейчас запущена. Закройте Railroader и запустите удаление снова.' -ForegroundColor Red
    exit 1
}

$pluginDir = Join-Path $GamePath "BepInEx\plugins\$PluginName"
if (Test-Path $pluginDir) {
    Remove-Item $pluginDir -Recurse -Force
    Write-Host 'Русификатор удалён.' -ForegroundColor Green
} else {
    Write-Host 'Русификатор не найден — нечего удалять.'
}
$cfg = Join-Path $GamePath 'BepInEx\config\ru.railroader.localization.cfg'
if (Test-Path $cfg) { Remove-Item $cfg -Force }

# BepInEx удаляем, только если в нём не осталось других модов.
$plugins = Join-Path $GamePath 'BepInEx\plugins'
$others = @()
if (Test-Path $plugins) { $others = @(Get-ChildItem $plugins -Force) }
if (-not $RemoveBepInEx -and $others.Count -eq 0 -and (Test-Path (Join-Path $GamePath 'BepInEx'))) {
    $answer = Read-Host 'Других модов BepInEx не найдено. Удалить и сам BepInEx? (д/н)'
    $RemoveBepInEx = $answer -match '^(д|y|да|yes)'
}
if ($RemoveBepInEx) {
    if ($others.Count -gt 0) {
        Write-Host 'В BepInEx\plugins есть другие моды — BepInEx оставлен.' -ForegroundColor Yellow
    } else {
        foreach ($item in 'BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
            $p = Join-Path $GamePath $item
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
        Write-Host 'BepInEx удалён. Игра вернулась в исходное состояние.' -ForegroundColor Green
    }
}
