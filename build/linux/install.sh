#!/usr/bin/env bash
# =============================================================================
# Ash Server — Linux Installer
# =============================================================================
# Usage:
#   sudo bash build/linux/install.sh [binary_path]
#
# If binary_path is omitted it looks for ash-server in the same directory
# as this script (i.e. the extracted zip layout).
#
# What this script does:
#   1. Creates a dedicated system user  ash-server
#   2. Installs the binary to          /opt/ash-server/
#   3. Copies default config/assets
#   4. Runs  ash-server install-service  (writes systemd unit)
#   5. Enables and starts the service
# =============================================================================
set -euo pipefail

INSTALL_DIR="/opt/ash-server"
SERVICE_USER="ash-server"
SERVICE_NAME="ash-server"

RED='\033[0;31m'; GREEN='\033[0;32m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[ash-server]${NC} $*"; }
ok()    { echo -e "${GREEN}[ash-server]${NC} $*"; }
die()   { echo -e "${RED}[error]${NC} $*" >&2; exit 1; }

# ── Root check ────────────────────────────────────────────────────────────────
[[ $EUID -eq 0 ]] || die "Run as root: sudo bash $0"

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
    info "SQLite3 not found. Installing it…"
    if command -v apt-get &>/dev/null; then
        apt-get update && apt-get install -y sqlite3
    elif command -v dnf &>/dev/null; then
        dnf install -y sqlite
    elif command -v yum &>/dev/null; then
        yum install -y sqlite
    elif command -v pacman &>/dev/null; then
        pacman -S --noconfirm sqlite
    else
        warn "Could not install sqlite3 automatically. Please install the 'sqlite3' package manually."
    fi
fi


# ── Create system user ────────────────────────────────────────────────────────
if ! id "$SERVICE_USER" &>/dev/null; then
    info "Creating system user '$SERVICE_USER'…"
    useradd --system --no-create-home --shell /sbin/nologin "$SERVICE_USER"
fi

# ── Install files ─────────────────────────────────────────────────────────────
info "Installing to $INSTALL_DIR…"
mkdir -p "$INSTALL_DIR"

# Binary
install -m 755 "$BINARY" "$INSTALL_DIR/ash-server"

# appsettings.json.example (only if not already present — don't overwrite user config)
if [[ -f "$SRC_DIR/appsettings.json.example" && ! -f "$INSTALL_DIR/appsettings.json.example" ]]; then
    install -m 644 "$SRC_DIR/appsettings.json.example" "$INSTALL_DIR/appsettings.json.example"
fi

# wwwroot (static web UI)
if [[ -d "$SRC_DIR/wwwroot" ]]; then
    cp -r --no-preserve=ownership "$SRC_DIR/wwwroot" "$INSTALL_DIR/"
fi

# personality (default starter files — don't overwrite customised files)
if [[ -d "$SRC_DIR/personality" ]]; then
    mkdir -p "$INSTALL_DIR/personality"
    cp -rn --no-preserve=ownership "$SRC_DIR/personality/." "$INSTALL_DIR/personality/"
fi

# Fix ownership
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

# ── Register and start service ────────────────────────────────────────────────
info "Registering systemd service…"
"$INSTALL_DIR/ash-server" install-service

info "Enabling and starting $SERVICE_NAME…"
systemctl enable "$SERVICE_NAME"
systemctl start  "$SERVICE_NAME"

ok "Ash Server installed and running."
echo ""
echo "  Web UI:   http://localhost:18799"
echo "  Admin:    http://localhost:18799/admin.html"
echo "  Logs:     journalctl -u $SERVICE_NAME -f"
echo "  Config:   $INSTALL_DIR/config.json"
echo "  Stop:     sudo systemctl stop $SERVICE_NAME"
echo "  Remove:   sudo bash $0 --uninstall"
echo ""

# ── Check for Tailscale Mesh VPN ──────────────────────────────────────────────
if ! command -v tailscale &>/dev/null; then
    echo ""
    warn "For secure remote access without port-forwarding, we highly recommend Tailscale."
    read -p "Would you like to install Tailscale securely now? (y/n): " -r response
    if [[ "$response" =~ ^[Yy]$ ]]; then
        info "Installing Tailscale..."
        curl -fsSL https://tailscale.com/install.sh | sh
        ok "Tailscale installed! Run 'sudo tailscale up' to join your secure tailnet."
    fi
fi


# ── Uninstall mode ────────────────────────────────────────────────────────────
if [[ "${1:-}" == "--uninstall" ]]; then
    info "Uninstalling Ash Server…"
    systemctl stop    "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true
    "$INSTALL_DIR/ash-server" uninstall-service 2>/dev/null || true
    echo ""
    echo "Files preserved at $INSTALL_DIR (database, config)."
    echo "Remove manually:  sudo rm -rf $INSTALL_DIR"
    ok "Service removed."
fi
