# Reproducible publish for the Zombies Declassified installer/updater.
#
#   .\build.ps1 -Tag beta2            # stamps the exe with the release tag
#   .\build.ps1 -Tag beta2 -Out C:\somewhere
#
# Produces, under -Out (default: .\publish):
#   ZombiesDeclassified-Updater.exe   self-contained single-file win-x64
#   updater-version.json              {"version": "<tag>"} - the self-update
#                                     pointer the exe reads from the release
#   SHA256SUMS.txt                    hashes of both, for the release notes
#
# Deliberately NOT trimmed and NOT NativeAOT (both carry live antivirus
# false-positive reports); plain self-contained is the profile that clears.
# The SDK is pinned by global.json next to the csproj, so the same tag from
# the same source builds the same exe on any machine with that SDK.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Out = (Join-Path $PSScriptRoot "publish")
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# .NET's Version property wants a numeric prefix; "beta2" -> 0.2.0 with the
# tag kept in the informational version the exe displays.
$numeric = "0.0.0"
if ($Tag -match '(\d+)(?:\.(\d+))?(?:\.(\d+))?') {
    $numeric = "{0}.{1}.{2}" -f $Matches[1], ($(if ($Matches[2]) { $Matches[2] } else { 0 })), ($(if ($Matches[3]) { $Matches[3] } else { 0 }))
    if ($Tag -match '^beta') { $numeric = "0.$numeric" -replace '\.0$', '' ; if (($numeric -split '\.').Count -lt 3) { $numeric = "$numeric.0" } }
}

New-Item -ItemType Directory -Force $Out | Out-Null
dotnet publish -c Release -r win-x64 --nologo -v quiet `
    -p:Version=$numeric -p:InformationalVersion=$Tag -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# only the exe ships; publish also drops a .pdb we do not want on the release page
Get-ChildItem $Out -Filter *.pdb | Remove-Item -Force

@{ version = $Tag } | ConvertTo-Json -Compress |
    Set-Content -Path (Join-Path $Out "updater-version.json") -Encoding ascii -NoNewline

$exe = Join-Path $Out "ZombiesDeclassified-Updater.exe"
$rows = foreach ($f in @($exe, (Join-Path $Out "updater-version.json"))) {
    "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLower(), (Split-Path $f -Leaf)
}
$rows | Set-Content -Path (Join-Path $Out "SHA256SUMS.txt") -Encoding ascii

Write-Output ("published {0} ({1:N1} MB) as version {2} / {3}" -f $exe, ((Get-Item $exe).Length / 1MB), $numeric, $Tag)
Get-Content (Join-Path $Out "SHA256SUMS.txt")
