<#
    Сборка дистрибутива русификатора: dist/RailroaderRu-<версия>/ и dist/RailroaderRu-<версия>.zip
    Работает в Windows PowerShell 5.1 и в pwsh (Linux, CI).

        powershell -ExecutionPolicy Bypass -File scripts\build.ps1
        pwsh scripts/build.ps1 -GameDir refs -Version 0.2.0            # CI: референсные сборки вместо игры
        pwsh scripts/build.ps1 -GameDir refs -Suffix dev.42 -NoCheck

    -GameDir  папка игры (или refs из pack-refs.ps1) — нужна только для компиляции
    -Version  версия (VersionPrefix); по умолчанию берётся из .csproj
    -Suffix   суффикс для имени архива (dev.42); в саму DLL не попадает
    -NoCheck  не проверять словари (если это делает отдельная задача CI)
#>
param(
    [string]$GameDir,
    [string]$Version,
    [string]$Suffix,
    [switch]$NoCheck
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
function P([string[]]$parts) { $p = $root; foreach ($x in $parts) { $p = Join-Path $p $x }; return $p }

$csproj = P 'src', 'RailroaderRu', 'RailroaderRu.csproj'
if (-not $Version) {
    $Version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
}
$fullVersion = if ($Suffix) { "$Version-$Suffix" } else { $Version }
Write-Host "Сборка русификатора $fullVersion" -ForegroundColor Cyan

# 0. BepInEx (скачивается и проверяется по SHA-256, если его ещё нет)
& (P 'scripts', 'get-bepinex.ps1')

# 1. Компиляция плагина
$buildArgs = @('build', $csproj, '-c', 'Release', '--nologo', '-v', 'q', "-p:VersionPrefix=$Version")
if ($GameDir) { $buildArgs += "-p:GameDir=$((Resolve-Path $GameDir).Path)" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции плагина' }
$dll = P 'src', 'RailroaderRu', 'bin', 'Release', 'RailroaderRu.dll'

# 2. Проверка словарей
if (-not $NoCheck) {
    & dotnet run --project (P 'tools', 'RuTools', 'RuTools.csproj') -c Release -- check (P 'translation', 'translations')
    if ($LASTEXITCODE -ne 0) { throw 'В словарях есть ошибки — см. вывод выше' }
}

# 3. Раскладка файлов: структура повторяет папку игры
$dist = P 'dist', "RailroaderRu-$fullVersion"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$plugin = Join-Path $dist (Join-Path 'BepInEx' (Join-Path 'plugins' 'RailroaderRu'))
$fonts = Join-Path $plugin 'fonts'
New-Item -ItemType Directory -Force -Path $plugin, $fonts | Out-Null

$bep = P 'vendor', 'bepinex'
Copy-Item (Join-Path $bep 'BepInEx') -Destination $dist -Recurse -Force
foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
    $src = Join-Path $bep $f
    if (Test-Path $src) { Copy-Item $src -Destination $dist -Force }
}

Copy-Item $dll -Destination $plugin
Get-ChildItem (P 'fonts') -File | Where-Object { $_.Extension -in '.ttf', '.txt' } | Copy-Item -Destination $fonts
Copy-Item (P 'translation', 'translations') -Destination $plugin -Recurse
Copy-Item (P 'translation', 'StreamingAssets') -Destination $plugin -Recurse
Copy-Item (P 'docs', 'INSTALL.ru.md') -Destination $dist

foreach ($f in 'install.ps1', 'uninstall.ps1', 'common.ps1', 'INSTALL.bat', 'UNINSTALL.bat', 'README.txt') {
    Copy-Item (P 'scripts', $f) -Destination $dist
}

# 4. Архив
. (P 'scripts', 'zip.ps1')
$zip = P 'dist', "RailroaderRu-$fullVersion.zip"
New-ZipFromDirectory $dist $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Готово: $zip ($size МБ)" -ForegroundColor Green
