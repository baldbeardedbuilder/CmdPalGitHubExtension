param(
    [Parameter(Mandatory)]
    [string] $BundlePath,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $Publisher
)

$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $makeappx) {
    throw 'makeappx.exe was not found in the Windows SDK.'
}

$unpackPath = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
New-Item -Path $unpackPath -ItemType Directory | Out-Null
try {
    & $makeappx.FullName unpack /p $BundlePath /d $unpackPath /o
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx unpack failed with exit code $LASTEXITCODE."
    }

    [xml] $bundleManifest = Get-Content (Join-Path $unpackPath 'AppxMetadata\AppxBundleManifest.xml') -Raw
    $identity = $bundleManifest.SelectSingleNode('//*[local-name()="Identity"]')
    if ($identity.Name -ne 'BaldBeardedBuilder.GitHubforCommandPalette' -or
        $identity.Publisher -ne $Publisher -or
        $identity.Version -ne $Version) {
        throw 'Store bundle identity, publisher, or version does not match the release contract.'
    }

    $architectures = @($bundleManifest.SelectNodes('//*[local-name()="Package" and @Type="application"]') |
        ForEach-Object { $_.Architecture } | Sort-Object -Unique)
    if ($architectures.Count -ne 2 -or $architectures[0] -ne 'arm64' -or $architectures[1] -ne 'x64') {
        throw "Store bundle must contain exactly x64 and arm64 packages; found: $($architectures -join ', ')."
    }
}
finally {
    Remove-Item $unpackPath -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Validated Store bundle identity and architecture set: $BundlePath"
