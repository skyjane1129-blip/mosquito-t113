# Snapshot of everything the demo server cannot recreate: the SQLite database (captures, devices, commands,
# learned cell table, analyses) and the local configuration. Photos in local-storage are copied only with
# -IncludePhotos (they are large and already on the board's TF card as well).
# Output: artifacts\backups\<yyyyMMdd-HHmmss>\ ; keeps the newest 14 snapshots.
param([switch]$IncludePhotos, [int]$Keep = 14)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$api = Join-Path $root 'src\Mosquito.Cloud.Api'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$target = Join-Path $root "artifacts\backups\$stamp"
New-Item -ItemType Directory -Force -Path $target | Out-Null

# Consistent copy of the live SQLite file through the SQLite backup API (safe while the API is writing).
$sqlite = Get-ChildItem "$env:USERPROFILE\.nuget\packages\sqlitepclraw.lib.e_sqlite3\*\runtimes\win-x64\native\e_sqlite3.dll" -ErrorAction SilentlyContinue | Select-Object -Last 1
$db = Join-Path $api 'data\mosquito-cloud.db'
if (Test-Path $db) {
    $copied = $false
    try {
        $client = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.data.sqlite.core\8.*\lib\net8.0\Microsoft.Data.Sqlite.dll" | Select-Object -Last 1
        $core = Get-ChildItem "$env:USERPROFILE\.nuget\packages\sqlitepclraw.core\2.*\lib\netstandard2.0\SQLitePCLRaw.core.dll" | Select-Object -Last 1
        $prov = Get-ChildItem "$env:USERPROFILE\.nuget\packages\sqlitepclraw.provider.e_sqlite3\2.*\lib\net6.0\SQLitePCLRaw.provider.e_sqlite3.dll" | Select-Object -Last 1
        if ($client -and $core -and $prov -and $sqlite) {
            $env:PATH = "$($sqlite.DirectoryName);$env:PATH"
            Add-Type -Path $core.FullName; Add-Type -Path $prov.FullName; Add-Type -Path $client.FullName
            [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_e_sqlite3]::new())
            $source = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Mode=ReadOnly")
            $dest = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$(Join-Path $target 'mosquito-cloud.db')")
            $source.Open(); $dest.Open(); $source.BackupDatabase($dest); $dest.Close(); $source.Close()
            $copied = $true
        }
    } catch { Write-Warning "SQLite backup API unavailable ($($_.Exception.Message)); falling back to file copy" }
    if (-not $copied) {
        Copy-Item $db (Join-Path $target 'mosquito-cloud.db')
        foreach ($suffix in '-wal', '-shm') { if (Test-Path "$db$suffix") { Copy-Item "$db$suffix" (Join-Path $target "mosquito-cloud.db$suffix") } }
    }
}
foreach ($cfg in 'appsettings.json', 'appsettings.Local.json') {
    $p = Join-Path $api $cfg
    if (Test-Path $p) { Copy-Item $p (Join-Path $target $cfg) }
}
if ($IncludePhotos) { Copy-Item (Join-Path $api 'local-storage') (Join-Path $target 'local-storage') -Recurse }

$size = [math]::Round(((Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Output "backup -> $target ($size MB)"
Get-ChildItem (Join-Path $root 'artifacts\backups') -Directory | Sort-Object Name -Descending | Select-Object -Skip $Keep |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force; Write-Output "pruned $($_.Name)" }
