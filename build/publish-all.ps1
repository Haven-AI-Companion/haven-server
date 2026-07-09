#!/usr/bin/env pwsh
# publish-all.ps1 — Build self-contained single-file binaries for all targets.
# Run from the repo root:  .\build\publish-all.ps1
# Optional: pass -Version to stamp the assembly version.
param(
    [string]$Version = "1.0.0",
    [string]$OutDir  = "build\dist"
)

$ErrorActionPreference = "Stop"
$proj = "haven-server-cs.csproj"

$targets = @(
    @{ rid = "win-x64";     ext = ".exe" },
    @{ rid = "linux-x64";   ext = ""     },
    @{ rid = "linux-arm64"; ext = ""     },
    @{ rid = "osx-x64";     ext = ""     },
    @{ rid = "osx-arm64";   ext = ""     }
)

foreach ($t in $targets) {
    $out = Join-Path $OutDir $t.rid
    Write-Host "`n=== Publishing $($t.rid) ===" -ForegroundColor Cyan

    dotnet publish $proj `
        -c Release `
        -r $t.rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:AssemblyVersion=$Version `
        -p:FileVersion=$Version `
        -p:InformationalVersion=$Version `
        -o $out

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed for $($t.rid)"
        exit 1
    }

    # Zip the output
    $zipName = "haven-server-$Version-$($t.rid).zip"
    $zipPath = Join-Path $OutDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path "$out\*" -DestinationPath $zipPath
    Write-Host "  -> $zipPath" -ForegroundColor Green
}

Write-Host "`nAll targets built successfully." -ForegroundColor Green
Write-Host "Artifacts in: $OutDir"
