# Reproducible publish for the Zombies Declassified installer/updater.
#
#   .\build.ps1 -Tag beta2            # -Tag = the pack release the exe belongs to
#   .\build.ps1 -Tag beta2 -Out C:\somewhere
#
# The installer's own version comes from updater.version next to this script (bump it for
# every build that ships). Produces, under -Out (default: .\publish):
#   ZombiesDeclassified-Updater.exe   self-contained single-file win-x64
#   updater-version.json              {"version": "<updater version>", "tag": "<tag>"}
#   SHA256SUMS.txt                    hashes of both
#
# Not trimmed and not NativeAOT (both carry antivirus false-positive reports); plain
# self-contained is the profile that clears. The SDK is pinned by global.json.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Out = (Join-Path $PSScriptRoot "publish")
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$Version = (Get-Content (Join-Path $PSScriptRoot "updater.version") -Raw).Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "updater.version must read major.minor.patch, got '$Version'" }

New-Item -ItemType Directory -Force $Out | Out-Null
dotnet publish -c Release -r win-x64 --nologo -v quiet `
    -p:Version=$Version -p:InformationalVersion=$Version -p:IncludeSourceRevisionInInformationalVersion=false -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Get-ChildItem $Out -Filter *.pdb | Remove-Item -Force

@{ version = $Version; tag = $Tag } | ConvertTo-Json -Compress |
    Set-Content -Path (Join-Path $Out "updater-version.json") -Encoding ascii -NoNewline

$exe = Join-Path $Out "ZombiesDeclassified-Updater.exe"
$rows = foreach ($f in @($exe, (Join-Path $Out "updater-version.json"))) {
    "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLower(), (Split-Path $f -Leaf)
}
$rows | Set-Content -Path (Join-Path $Out "SHA256SUMS.txt") -Encoding ascii

Write-Output ("published {0} ({1:N1} MB) as installer {2} for {3}" -f $exe, ((Get-Item $exe).Length / 1MB), $Version, $Tag)
Get-Content (Join-Path $Out "SHA256SUMS.txt")
