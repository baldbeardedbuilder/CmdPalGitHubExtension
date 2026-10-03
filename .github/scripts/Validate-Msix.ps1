param(
    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $Publisher,

    [switch] $RequireSignature
)

$architecture = switch ($RuntimeIdentifier) {
    'win-x64' { 'x64' }
    'win-arm64' { 'arm64' }
    default { throw "Unsupported runtime identifier '$RuntimeIdentifier'." }
}

$project = Join-Path $env:GITHUB_WORKSPACE 'GitHubExtension'
$packagePattern = "_$([regex]::Escape($Version))_$architecture\.msix$"
$packages = @(Get-ChildItem (Join-Path $project "AppPackages\$RuntimeIdentifier") -Filter '*.msix' -File -Recurse |
    Where-Object { $_.Name -match $packagePattern })
if ($packages.Count -ne 1) {
    throw "Expected one $RuntimeIdentifier MSIX package, found $($packages.Count)."
}

$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $makeappx) {
    throw 'makeappx.exe was not found in the Windows SDK.'
}

$unpackPath = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
New-Item -Path $unpackPath -ItemType Directory | Out-Null
try {
    & $makeappx.FullName unpack /p $packages[0].FullName /d $unpackPath /o
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx unpack failed with exit code $LASTEXITCODE."
    }

    [xml] $packageManifest = Get-Content (Join-Path $unpackPath 'AppxManifest.xml') -Raw
    $identity = $packageManifest.SelectSingleNode('//*[local-name()="Identity"]')
    if ($identity.Name -ne 'BaldBeardedBuilder.GitHubforCommandPalette' -or
        $identity.Publisher -ne $Publisher -or
        $identity.Version -ne $Version -or
        $identity.ProcessorArchitecture -ne $architecture) {
        throw "Package manifest identity, publisher, version, or architecture does not match the release contract: $($packages[0].Name)."
    }

    $executablePath = Join-Path $unpackPath 'BaldBeardedBuilder.GitHubExtension.exe'
    if (-not (Test-Path $executablePath)) {
        throw 'The packaged extension executable was not found.'
    }

    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath).FileVersion
    if ($fileVersion -ne $Version) {
        throw "Packaged executable version '$fileVersion' does not match '$Version'."
    }

    if ($RequireSignature) {
        $signature = Get-AuthenticodeSignature $packages[0].FullName
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -ne $Publisher) {
            throw "Package signature is invalid or its signer does not match the package publisher: $($packages[0].Name)."
        }
    }

    $managedAssembly = Get-ChildItem (Join-Path $project 'bin') -Filter 'BaldBeardedBuilder.GitHubExtension.dll' -File -Recurse |
        Where-Object { $_.FullName.Contains("\Release\") -and $_.FullName.Contains("\$RuntimeIdentifier\") -and $_.FullName -notmatch '\\(publish|ref)\\' } |
        Select-Object -First 1
    if (-not $managedAssembly) {
        throw "The $RuntimeIdentifier managed build output was not found."
    }

    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($managedAssembly.FullName).Version.ToString()
    if ($assemblyVersion -ne $Version) {
        throw "Managed assembly version '$assemblyVersion' does not match '$Version'."
    }
}
finally {
    Remove-Item $unpackPath -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Validated $RuntimeIdentifier package $Version ($($packages[0].Name))."
