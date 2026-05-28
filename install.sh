#!/usr/bin/env bash
# =============================================================================
# Ash Server — Install from source (Linux / macOS)
# =============================================================================
# One-liner install:
#   git clone https://github.com/ssfdre38/ash-server && sudo bash ash-server/install.sh
#
# Or to just run without installing as a service:
#   git clone https://github.com/ssfdre38/ash-server && bash ash-server/install.sh --run
# =============================================================================
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="/opt/ash-server"
SERVICE_USER="ash-server"
SERVICE_NAME="ash-server"
MODE="${1:-install}"   # install | --run | --uninstall

RED='\033[0;31m'; GREEN='\033[0;32m'; CYAN='\033[0;36m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${CYAN}[ash-server]${NC} $*"; }
ok()    { echo -e "${GREEN}[ash-server]${NC} $*"; }
warn()  { echo -e "${YELLOW}[ash-server]${NC} $*"; }
die()   { echo -e "${RED}[error]${NC} $*" >&2; exit 1; }

# ── Detect OS and architecture ────────────────────────────────────────────────
OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS-$ARCH" in
    Linux-x86_64)  RID="linux-x64"   ;;
    Linux-aarch64) RID="linux-arm64" ;;
    Darwin-x86_64) RID="osx-x64"    ;;
    Darwin-arm64)  RID="osx-arm64"  ;;
    *) die "Unsupported platform: $OS-$ARCH" ;;
esac
info "Detected platform: $RID"

# ── Check for .NET SDK ────────────────────────────────────────────────────────
if ! command -v dotnet &>/dev/null; then
    echo ""
    warn ".NET SDK not found. Install it from: https://dotnet.microsoft.com/download"
    warn "On Ubuntu/Debian:  sudo apt install -y dotnet-sdk-10.0"
    warn "On macOS:          brew install dotnet"
    echo ""
    die "Please install .NET 10 SDK and re-run this script."
fi
DOTNET_VERSION="$(dotnet --version 2>/dev/null || echo 'unknown')"
info ".NET SDK version: $DOTNET_VERSION"

# ── Check for SQLite3 ─────────────────────────────────────────────────────────
if ! command -v sqlite3 &>/dev/null; then
    warn "SQLite3 not found. It is a required dependency."
    if [[ "$OS" == "Linux" ]]; then
        if command -v apt-get &>/dev/null; then
            info "Installing sqlite3 via apt-get…"
            sudo apt-get update && sudo apt-get install -y sqlite3
        elif command -v dnf &>/dev/null; then
            info "Installing sqlite3 via dnf…"
            sudo dnf install -y sqlite
        elif command -v yum &>/dev/null; then
            info "Installing sqlite3 via yum…"
            sudo yum install -y sqlite
        elif command -v pacman &>/dev/null; then
            info "Installing sqlite3 via pacman…"
            sudo pacman -S --noconfirm sqlite
        else
            warn "Could not install sqlite3 automatically. Please install it using your package manager."
        fi
    elif [[ "$OS" == "Darwin" ]]; then
        if command -v brew &>/dev/null; then
            info "Installing sqlite3 via Homebrew…"
            brew install sqlite
        else
            warn "Could not install sqlite3 automatically (Homebrew not found). Please install it manually."
        fi
    fi
else
    info "SQLite3 version: $(sqlite3 --version | head -n 1 | awk '{print $1}')"
fi


# ── Build ─────────────────────────────────────────────────────────────────────
OUT="$REPO_DIR/build/dist/$RID"
info "Building self-contained binary for $RID…"
dotnet publish "$REPO_DIR/ash-server-cs.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$OUT"
BINARY="$OUT/ash-server"
chmod +x "$BINARY"
ok "Build complete: $BINARY"

# ── --run mode: just start in place, no service install ──────────────────────
if [[ "$MODE" == "--run" ]]; then
    info "Starting ash-server in the foreground (Ctrl+C to stop)…"
    cd "$OUT"
    exec ./ash-server
fi

# ── --uninstall mode ──────────────────────────────────────────────────────────
if [[ "$MODE" == "--uninstall" ]]; then
    [[ $EUID -eq 0 ]] || die "Run as root for uninstall: sudo bash $0 --uninstall"
    if [[ "$OS" == "Linux" ]]; then
        systemctl stop    "$SERVICE_NAME" 2>/dev/null || true
        systemctl disable "$SERVICE_NAME" 2>/dev/null || true
        "$INSTALL_DIR/ash-server" uninstall-service 2>/dev/null || true
    elif [[ "$OS" == "Darwin" ]]; then
        "$INSTALL_DIR/ash-server" uninstall-service 2>/dev/null || true
    fi
    rm -f "/usr/local/bin/ash-server"
    warn "Files preserved at $INSTALL_DIR (database, config). Remove manually if needed."
    ok "Ash Server removed."
    exit 0
fi

# ── Install mode (default) ────────────────────────────────────────────────────
[[ $EUID -eq 0 ]] || die "Run as root for service install: sudo bash $0\n  (or use: bash $0 --run  to start without installing)"

# Delegate to the platform installer bundled in the repo
if [[ "$OS" == "Linux" ]]; then
    bash "$REPO_DIR/build/linux/install.sh" "$BINARY"
elif [[ "$OS" == "Darwin" ]]; then
    bash "$REPO_DIR/build/macos/install.sh" "$BINARY"
else
    die "Unknown OS: $OS"
fi

# ── Check for Tailscale Mesh VPN ──────────────────────────────────────────────
if ! command -v tailscale &>/dev/null; then
    echo ""
    warn "For secure remote access without port-forwarding, we highly recommend Tailscale."
    read -p "Would you like to install Tailscale securely now? (y/n): " -r response
    if [[ "$response" =~ ^[Yy]$ ]]; then
        info "Installing Tailscale..."
        if [[ "$OS" == "Linux" ]]; then
            curl -fsSL https://tailscale.com/install.sh | sh
            ok "Tailscale installed! Run 'sudo tailscale up' to join your secure tailnet."
        elif [[ "$OS" == "Darwin" ]]; then
            if command -v brew &>/dev/null; then
                brew install tailscale
                ok "Tailscale installed! Run 'tailscale up' to join your secure tailnet."
            else
                warn "Homebrew not found. Please install Tailscale manually from: https://tailscale.com"
            fi
        fi
    fi
fi

