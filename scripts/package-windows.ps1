param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$stage = Join-Path $output ('windows-' + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $output "CSharpClipboard-$Version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { throw "Package already exists: $zip" }
dotnet restore (Join-Path $root 'src\CSharpClipboard.App') -r win-x64 --disable-parallel
if ($LASTEXITCODE -ne 0) { throw 'Windows restore failed.' }
dotnet publish (Join-Path $root 'src\CSharpClipboard.App') -c Release -r win-x64 --self-contained true --no-restore -m:1 -p:Version=$Version -p:DebugType=None -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item -LiteralPath (Join-Path $root 'docs\Windows-Package-Readme.txt') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'docs\ThirdPartyNotices.md') -Destination $stage
$assets = Get-Content -Raw (Join-Path $root 'src\CSharpClipboard.App\obj\project.assets.json') | ConvertFrom-Json -AsHashtable
foreach ($library in $assets.libraries.Values) {
    if ($library.type -ne 'package') { continue }
    foreach ($cache in $assets.packageFolders.Keys) {
        $packageRoot = Join-Path $cache $library.path
        if (-not (Test-Path -LiteralPath $packageRoot)) { continue }
        $noticeDirectory = Join-Path $stage ('notices\' + $library.path.Replace('/', '-'))
        New-Item -ItemType Directory -Force -Path $noticeDirectory | Out-Null
        Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object { $_.Name -match '(?i)(license|notice|copying)|\.nuspec$' -and $_.Extension -notin '.dll','.pdb','.png' } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeDirectory $_.Name) -Force
        }
    }
}
$runtime = Get-ChildItem (Join-Path $HOME '.nuget\packages\microsoft.netcore.app.runtime.win-x64') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if ($runtime) { Copy-Item -LiteralPath (Join-Path $runtime.FullName 'LICENSE.TXT'),(Join-Path $runtime.FullName 'THIRD-PARTY-NOTICES.TXT') -Destination $stage }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($zip + '.sha256', "$hash  $([IO.Path]::GetFileName($zip))`n")
Write-Output $zip
