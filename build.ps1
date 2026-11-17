[CmdletBinding()]
param(
    [switch] $Publish,
    [switch] $SkipVirtualDisplayDriver
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).ProviderPath
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$platform = switch ($architecture) {
    'X64' { 'x64' }
    'Arm64' { 'ARM64' }
    'X86' { 'x86' }
    default { throw "Unsupported Windows architecture: $architecture" }
}

$outputName = if ($Publish) { 'publish' } else { 'bin' }
$output = Join-Path $projectRoot $outputName
$executable = Join-Path $output 'RemoteScreen.exe'
$driverArchive = $null
if ($platform -eq 'x64' -and -not $SkipVirtualDisplayDriver) {
    $driverVersion = '25.7.23'
    $archiveName = "VDD.Control.$driverVersion.zip"
    $driverArchive = Join-Path $projectRoot $archiveName
    $expectedHash = 'A701F2272E9FCF382849B24F913C6DD07597B3B1116525F2E90182F019609154'
    if (-not (Test-Path -LiteralPath $driverArchive -PathType Leaf)) {
        $download = "$driverArchive.download"
        try {
            $url = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/$driverVersion/$archiveName"
            curl.exe --fail --location --retry 2 --max-time 180 --output $download $url
            if ($LASTEXITCODE -ne 0) {
                throw "Could not download $archiveName."
            }
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) {
                throw "SHA-256 verification failed for $archiveName."
            }
            Move-Item -LiteralPath $download -Destination $driverArchive
        }
        finally {
            if (Test-Path -LiteralPath $download -PathType Leaf) {
                Remove-Item -LiteralPath $download
            }
        }
    }
    elseif ((Get-FileHash -LiteralPath $driverArchive -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "SHA-256 verification failed for $archiveName. Delete the archive and rebuild."
    }
}

foreach ($process in @(Get-Process RemoteScreen -ErrorAction SilentlyContinue | Where-Object Path -eq $executable)) {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            throw "Close Remote Screen before rebuilding (PID $($process.Id))."
        }
    }
}

foreach ($directory in @($outputName, 'obj')) {
    $path = Join-Path $projectRoot $directory
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force
        if (-not $item.PSIsContainer -or
            $item.Parent.FullName -ne $projectRoot -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected build directory: $path"
        }
        if ($directory -eq $outputName -and $driverArchive) {
            foreach ($entry in Get-ChildItem -LiteralPath $item.FullName -Force) {
                if ([IO.Path]::GetDirectoryName($entry.FullName) -ne $item.FullName -or
                    ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw "Unexpected output path: $($entry.FullName)"
                }
                if ($entry.Name -ne 'VirtualDisplayDriver') {
                    Remove-Item -LiteralPath $entry.FullName -Recurse -Force
                }
            }
        }
        else {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
        }
    }
}

if ($driverArchive) {
    $driverStage = Join-Path $projectRoot 'obj\VirtualDisplayDriver'
    $driverOutput = Join-Path $output 'VirtualDisplayDriver'
    Expand-Archive -LiteralPath $driverArchive -DestinationPath $driverStage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Assets\VirtualDisplayDriver.LICENSE.txt') -Destination $driverStage
    foreach ($file in Get-ChildItem -LiteralPath $driverStage -Recurse -File) {
        $relativePath = $file.FullName.Substring($driverStage.Length + 1)
        $destination = Join-Path $driverOutput $relativePath
        # Windows locks the running setup executable. Only replace files that differ.
        if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
            (Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) {
            continue
        }
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}

$rid = "win-$($platform.ToLowerInvariant())"
$projectFile = Join-Path $projectRoot 'RemoteScreen.csproj'
$buildOutput = $output + [IO.Path]::DirectorySeparatorChar
if ($Publish) {
    dotnet publish $projectFile -c Release -p:Platform=$platform -r $rid --self-contained true -o $buildOutput
}
else {
    dotnet build $projectFile -c Release -p:Platform=$platform -r $rid --self-contained false -o $buildOutput
}
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

if (-not $Publish) {
    Start-Process -FilePath $executable -WorkingDirectory $output -WindowStyle Normal
}
