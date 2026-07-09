#!/bin/sh
# preremove.sh - Runs before the Debian/RPM package is removed
set -e

SERVICE_NAME="haven-server"
INSTALL_DIR="/opt/haven-server"

# Stop and disable the systemd service if running
systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || true
systemctl disable "$SERVICE_NAME" >/dev/null 2>&1 || true

# Invoke the self-contained C# service installer to unregister the systemd service
if [ -f "$INSTALL_DIR/haven-server" ]; then
    cd "$INSTALL_DIR"
    ./haven-server uninstall-service >/dev/null 2>&1 || true
fi

# Reload systemd configuration
systemctl daemon-reload
