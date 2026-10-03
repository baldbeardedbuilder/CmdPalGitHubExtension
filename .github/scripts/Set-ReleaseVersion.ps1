param(
    [Parameter(Mandatory)]
    [string] $Tag,

    [Parameter(Mandatory)]
    [string] $ManifestPath
)

$match = [regex]::Match($Tag, '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')
if (-not $match.Success) {
    throw "Release tag '$Tag' is invalid. Stable releases must use vMAJOR.MINOR.PATCH."
}

foreach ($component in $match.Groups[1..3]) {
    $number = [uint32] 0
    if (-not [uint32]::TryParse($component.Value, [ref]$number) -or $number -gt 65535) {
        throw "Release tag '$Tag' exceeds the maximum MSIX version component of 65535."
    }
}

$version = "$($match.Groups[1].Value).$($match.Groups[2].Value).$($match.Groups[3].Value).0"
$manifest = [System.Xml.XmlDocument]::new()
$manifest.PreserveWhitespace = $true
$manifest.Load((Resolve-Path $ManifestPath))
$identity = $manifest.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]')
if (-not $identity) {
    throw "Package identity was not found in '$ManifestPath'."
}

$identity.SetAttribute('Version', $version)
$manifest.Save((Resolve-Path $ManifestPath))

if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$version" -Encoding utf8
}

Write-Host "Release package version: $version"
