#!/usr/bin/env bash
# publish-all.sh — Build self-contained single-file binaries for all targets.
# Usage: bash build/publish-all.sh [version]
set -euo pipefail

VERSION="${1:-1.0.0}"
OUTDIR="build/dist"
PROJ="ash-server-cs.csproj"

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

    # Zip the output
    zipname="ash-server-${VERSION}-${rid}.zip"
    zippath="$OUTDIR/$zipname"
    rm -f "$zippath"
    (cd "$out" && zip -r "../../$zipname" .)
    mv "$OUTDIR/$zipname" "$zippath" 2>/dev/null || true
    echo "  -> $zippath"
done

echo ""
echo "All targets built successfully."
echo "Artifacts in: $OUTDIR"
