param (
    [Parameter(Mandatory = $true)][string] $manifestFile,
    [Parameter(Mandatory = $true)][string] $versionString
)

$ErrorActionPreference = "Stop"
$pattern = '"version_number":\s*"([^"]*)"'
$manifest = Get-Content -Raw -LiteralPath $manifestFile
if ([regex]::Matches($manifest, $pattern).Count -ne 1) {
    throw "Expected exactly one version_number property in '$manifestFile'."
}

$updated = [regex]::Replace($manifest, $pattern, "`"version_number`": `"$versionString`"")
$null = $updated | ConvertFrom-Json
[IO.File]::WriteAllText($manifestFile, $updated, [Text.UTF8Encoding]::new($false))
