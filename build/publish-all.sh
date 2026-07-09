#!/usr/bin/env bash
# publish-all.sh — Build self-contained single-file binaries for all targets.
# Usage: bash build/publish-all.sh [version]
set -euo pipefail

VERSION="${1:-1.0.0}"
OUTDIR="build/dist"
PROJ="haven-server-cs.csproj"

TARGETS=(
    "win-x64"
    "linux-x64"
    "linux-arm64"
    "osx-x64"
    "osx-arm64"
)

for rid in "${TARGETS[@]}"; do
    out="$OUTDIR/$rid"
    echo ""
    echo "=== Publishing $rid ==="

    dotnet publish "$PROJ" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -p:AssemblyVersion="$VERSION" \
        -p:FileVersion="$VERSION" \
        -p:InformationalVersion="$VERSION" \
        -o "$out"

    # Ensure executable bit + include install scripts + normalise Plugins casing
    [[ -f "$out/haven-server" ]] && chmod +x "$out/haven-server"
    mkdir -p "$out/build/linux"
    cp "$(dirname "$0")/linux/install.sh" "$out/build/linux/install.sh"
    chmod +x "$out/build/linux/install.sh"
    if [[ -d "$(dirname "$0")/macos" ]]; then
        mkdir -p "$out/build/macos"
        cp "$(dirname "$0")/macos/install.sh" "$out/build/macos/install.sh" 2>/dev/null || true
        chmod +x "$out/build/macos/install.sh" 2>/dev/null || true
    fi
    if [[ -d "$out/Plugins" && ! -d "$out/plugins" ]]; then
        mv "$out/Plugins" "$out/plugins"
    fi

    # Zip the output
    zipname="haven-server-${VERSION}-${rid}.zip"
    zippath="$OUTDIR/$zipname"
    rm -f "$zippath"
    (cd "$out" && zip -r "../../$zipname" .)
    mv "$OUTDIR/$zipname" "$zippath" 2>/dev/null || true
    echo "  -> $zippath"
done

echo ""
echo "All targets built successfully."
echo "Artifacts in: $OUTDIR"
