param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$architecture = $RuntimeIdentifier.Substring(4)
$expectedMachine = if ($architecture -eq 'x64') { 'Amd64' } else { 'Arm64' }
$packageDirectory = Join-Path $PSScriptRoot '..\GitHubExtension\AppPackages'
$packages = @(Get-ChildItem $packageDirectory -Filter "*_$architecture.msix" -Recurse)
if ($packages.Count -eq 0) {
    throw "Publish did not produce an $architecture MSIX package."
}

foreach ($package in $packages) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($zip.Entries | Where-Object Name -eq 'BaldBeardedBuilder.GitHubExtension.exe')
        if ($entries.Count -ne 1) {
            throw "Expected one extension executable in $($package.Name)."
        }

        $source = $entries[0].Open()
        $memory = [System.IO.MemoryStream]::new()
        try {
            $source.CopyTo($memory)
            $memory.Position = 0
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($memory)
            try {
                if ($null -ne $pe.PEHeaders.CorHeader) {
                    throw "$($package.Name) contains a managed executable instead of Native AOT."
                }
                if ($pe.PEHeaders.CoffHeader.Machine.ToString() -ne $expectedMachine) {
                    throw "$($package.Name) contains the wrong executable architecture."
                }
                Write-Host "$($package.Name): native $expectedMachine executable verified."
            }
            finally {
                $pe.Dispose()
            }
        }
        finally {
            $source.Dispose()
            $memory.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}
