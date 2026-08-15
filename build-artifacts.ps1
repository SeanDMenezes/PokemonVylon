<#
    Publishes the four standalone binaries hosted on the GitHub "tools" release.

    Each target gets its own folder so the PDBs stay beside the executable they belong to.
    Builds are deterministic, so an unchanged source tree reproduces the same hash. Players
    re-download the updater whenever the hash of a "tools" asset changes, so a differing
    hash is a reliable signal that the binary really did change.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$artifacts = [IO.Path]::Combine($root, "artifacts")

$targets = @(
    @{ Project = "Updater\Updater.csproj"
       Rid = "win-x64"
       Folder = "Updater"
       Produced = "Updater.exe"
       Asset = "Updater.exe" }

    @{ Project = "Updater\Updater.csproj"
       Rid = "linux-x64"
       Folder = "Updater-linux-x64"
       Produced = "Updater"
       Asset = "Updater-linux-x64" }

    @{ Project = "UpdaterBootstrap\UpdaterBootstrap.csproj"
       Rid = "win-x64"
       Folder = "UpdaterBootstrap"
       Produced = "UpdaterBootstrap.exe"
       Asset = "UpdaterBootstrap.exe" }

    @{ Project = "PatchBuilderGUI\PatchBuilderGUI.csproj"
       Rid = "win-x64"
       Folder = "PatchBuilderGUI"
       Produced = "PatchBuilderGUI.exe"
       Asset = "PatchBuilderGUI.exe" }
)

foreach ($target in $targets) {
    $outputDir = [IO.Path]::Combine($artifacts, $target.Folder)

    # Publishing over a stale folder can silently emit a framework-dependent build.
    if (Test-Path $outputDir) {
        Remove-Item -Recurse -Force $outputDir
    }

    Write-Host "Publishing $($target.Folder) ($($target.Rid))..." -ForegroundColor Cyan

    dotnet publish ([IO.Path]::Combine($root, $target.Project)) `
        -c $Configuration `
        -r $target.Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $outputDir

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $($target.Project) ($($target.Rid))."
    }

    if ($target.Produced -ne $target.Asset) {
        Move-Item -Force `
            ([IO.Path]::Combine($outputDir, $target.Produced)) `
            ([IO.Path]::Combine($outputDir, $target.Asset))
    }
}

$summary = foreach ($target in $targets) {
    $path = [IO.Path]::Combine($artifacts, $target.Folder, $target.Asset)
    [pscustomobject]@{
        Asset  = $target.Asset
        MB     = [math]::Round((Get-Item $path).Length / 1MB, 1)
        SHA256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLower()
    }
}

Write-Host "`nBuilt:" -ForegroundColor Green
$summary | Format-Table -AutoSize
