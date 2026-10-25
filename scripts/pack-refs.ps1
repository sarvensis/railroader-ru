<#
    Готовит референсные сборки игры для CI: refs/Railroader_Data/Managed и dist/game-refs.zip.
    Сборки урезаются утилитой RuTools make-refs: остаются только типы и сигнатуры, кода игры в них нет.
    Запускать на машине с установленной игрой. Обычно его вызывает update.ps1 из репозитория railroader-ru-refs,
    который кладёт архив в git и публикует его (см. README того репозитория).

        pwsh scripts/pack-refs.ps1 [-GameDir "D:\Games\Railroader"]
#>
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Railroader'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$managed = Join-Path $GameDir 'Railroader_Data/Managed'
if (-not (Test-Path (Join-Path $managed 'Assembly-CSharp.dll'))) { throw "Не найдена игра: $managed" }

# Версия игры — первый заголовок «### Railroader X» в списке изменений.
$notes = Get-Content (Join-Path $GameDir 'ReleaseNotes-Public.md') -Raw
$m = [regex]::Match($notes, '###\s+Railroader\s+([0-9][0-9A-Za-z.\-]*)')
if (-not $m.Success) { throw 'Не удалось определить версию игры по ReleaseNotes-Public.md' }
$gameVersion = $m.Groups[1].Value
Write-Host "Версия игры: $gameVersion" -ForegroundColor Cyan

# Какие сборки нужны — берём из ссылок с $(ManagedDir) в .csproj плагина.
$csproj = [xml](Get-Content (Join-Path $root 'src/RailroaderRu/RailroaderRu.csproj') -Raw)
$names = @($csproj.Project.ItemGroup.Reference | Where-Object { $_.HintPath -like '*$(ManagedDir)*' } | ForEach-Object { $_.Include })

$out = Join-Path $root 'refs/Railroader_Data/Managed'
if (Test-Path (Join-Path $root 'refs')) { Remove-Item (Join-Path $root 'refs') -Recurse -Force }
& dotnet run --project (Join-Path $root 'tools/RuTools/RuTools.csproj') -c Release -- make-refs $managed $out @names
if ($LASTEXITCODE -ne 0) { throw 'make-refs завершился с ошибкой' }
Set-Content -Path (Join-Path $root 'refs/GAME_VERSION') -Value $gameVersion -NoNewline

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist 'game-refs.zip'

. (Join-Path $PSScriptRoot 'zip.ps1')
New-ZipFromDirectory (Join-Path $root 'refs') $zip
Write-Host "Готово: $zip" -ForegroundColor Green

