# Упаковка папки в zip с «/» в путях. Compress-Archive и ZipFile в Windows PowerShell 5.1 пишут «\»,
# а такой архив на Linux (CI) распаковывается в файлы с обратными слэшами в именах.

function New-ZipFromDirectory([string]$Source, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path $Destination) { Remove-Item $Destination -Force }
    $sourceFull = (Resolve-Path $Source).Path.TrimEnd('\', '/')
    $archive = [System.IO.Compression.ZipFile]::Open($Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $sourceFull -Recurse -File -Force) {
            $entry = $file.FullName.Substring($sourceFull.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
    }
}
