#!/bin/sh
# postinstall.sh - Runs after the Debian/RPM package is installed
set -e

SERVICE_USER="ash-server"
INSTALL_DIR="/opt/ash-server"

# Create a dedicated system user if not already present
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /sbin/nologin "$SERVICE_USER"
fi

# Ensure ownership of the installation directory is set to the system user
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

# Invoke the self-contained C# service installer to register the systemd service
# Run it in the directory context of the installation folder
cd "$INSTALL_DIR"
./ash-server install-service

# Enable and start the registered systemd service
systemctl daemon-reload
systemctl enable ash-server
systemctl start ash-server
