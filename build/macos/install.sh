#!/usr/bin/env bash
# =============================================================================
# Ash Server — macOS Installer
# =============================================================================
# Usage:
#   sudo bash build/macos/install.sh [binary_path]
#
# What this script does:
#   1. Installs the binary to  /usr/local/opt/ash-server/
#   2. Copies default config/assets
#   3. Creates a symlink       /usr/local/bin/ash-server
#   4. Runs  ash-server install-service  (writes launchd plist)
#   5. Loads the daemon
# =============================================================================
set -euo pipefail

INSTALL_DIR="/usr/local/opt/ash-server"
BIN_LINK="/usr/local/bin/ash-server"
SERVICE_NAME="com.ash-server"
PLIST_PATH="/Library/LaunchDaemons/${SERVICE_NAME}.plist"

RED='\033[0;31m'; GREEN='\033[0;32m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[ash-server]${NC} $*"; }
ok()    { echo -e "${GREEN}[ash-server]${NC} $*"; }
die()   { echo -e "${RED}[error]${NC} $*" >&2; exit 1; }

# ── Root check ────────────────────────────────────────────────────────────────
[[ $EUID -eq 0 ]] || die "Run as root: sudo bash $0"

# ── Uninstall mode ────────────────────────────────────────────────────────────
if [[ "${1:-}" == "--uninstall" ]]; then
    info "Uninstalling Ash Server…"
    launchctl unload -w "$PLIST_PATH" 2>/dev/null || true
    "$INSTALL_DIR/ash-server" uninstall-service 2>/dev/null || true
    rm -f "$BIN_LINK"
    echo ""
    echo "Files preserved at $INSTALL_DIR (database, config)."
    echo "Remove manually:  sudo rm -rf $INSTALL_DIR"
    ok "Daemon removed."
    exit 0
fi

# ── Locate binary ─────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Auto-detect source directory (where assets and binary reside)
SRC_DIR="$SCRIPT_DIR"
if [[ -d "$SCRIPT_DIR/../../wwwroot" ]]; then
    SRC_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
elif [[ -d "$SCRIPT_DIR/../wwwroot" ]]; then
    SRC_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
fi

BINARY="${1:-$SRC_DIR/ash-server}"
[[ -f "$BINARY" ]] || die "Binary not found: $BINARY\nUsage: sudo bash $0 /path/to/ash-server"

info "Installing Ash Server from: $BINARY"

# ── Check for SQLite3 ─────────────────────────────────────────────────────────
if ! command -v sqlite3 &>/dev/null; then
    info "SQLite3 not found. Installing it via Homebrew…"
    if command -v brew &>/dev/null; then
        if [[ -n "${SUDO_USER:-}" ]]; then
            sudo -u "$SUDO_USER" brew install sqlite
        else
            brew install sqlite
        fi
    else
        warn "Could not install sqlite3 automatically (Homebrew not found). Please install the 'sqlite' package manually."
    fi
fi


# ── Install files ─────────────────────────────────────────────────────────────
info "Installing to $INSTALL_DIR…"
mkdir -p "$INSTALL_DIR"

# Binary
install -m 755 "$BINARY" "$INSTALL_DIR/ash-server"

# macOS: clear quarantine bit so the binary runs without Gatekeeper prompts
xattr -d com.apple.quarantine "$INSTALL_DIR/ash-server" 2>/dev/null || true

# appsettings.json (only if not already present)
if [[ -f "$SRC_DIR/appsettings.json" && ! -f "$INSTALL_DIR/appsettings.json" ]]; then
    install -m 644 "$SRC_DIR/appsettings.json" "$INSTALL_DIR/appsettings.json"
fi

# wwwroot (static web UI)
if [[ -d "$SRC_DIR/wwwroot" ]]; then
    cp -r "$SRC_DIR/wwwroot" "$INSTALL_DIR/"
fi

# personality (default starter files — don't overwrite customised files)
if [[ -d "$SRC_DIR/personality" ]]; then
    mkdir -p "$INSTALL_DIR/personality"
    # -n = no-clobber (skip files that already exist)
    cp -rn "$SRC_DIR/personality/." "$INSTALL_DIR/personality/" 2>/dev/null || true
fi

# Symlink so 'ash-server' works anywhere in the terminal
ln -sf "$INSTALL_DIR/ash-server" "$BIN_LINK"

# ── Register and start daemon ─────────────────────────────────────────────────
info "Registering launchd daemon…"
"$INSTALL_DIR/ash-server" install-service

ok "Ash Server installed and running."
echo ""
echo "  Web UI:    http://localhost:18799"
echo "  Admin:     http://localhost:18799/admin.html"
echo "  Logs:      tail -f /var/log/ash-server/stdout.log"
echo "  Config:    $INSTALL_DIR/config.json"
echo "  Stop:      sudo launchctl stop $SERVICE_NAME"
echo "  Remove:    sudo bash $0 --uninstall"
echo ""

# ── Check for Tailscale Mesh VPN ──────────────────────────────────────────────
if ! command -v tailscale &>/dev/null; then
    echo ""
    warn "For secure remote access without port-forwarding, we highly recommend Tailscale."
    read -p "Would you like to install Tailscale securely now? (y/n): " -r response
    if [[ "$response" =~ ^[Yy]$ ]]; then
        info "Installing Tailscale..."
        if command -v brew &>/dev/null; then
            if [[ -n "${SUDO_USER:-}" ]]; then
                sudo -u "$SUDO_USER" brew install tailscale
            else
                brew install tailscale
            fi
            ok "Tailscale installed! Run 'tailscale up' to join your secure tailnet."
        else
            warn "Homebrew not found. Please install Tailscale manually from: https://tailscale.com"
        fi
    fi
fi

