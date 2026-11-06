#!/usr/bin/env bash
# NodeWatch installer for Ubuntu 24.04 (run it from the extracted release folder):
#   sudo ./install.sh
# Installs or upgrades: /opt/nodewatch (program), /etc/nodewatch (config, kept on upgrade),
# the nodewatch system user, the systemd unit and the polkit rule that allows restarting the chain unit only.
# Environment: NODEWATCH_CHAIN_UNIT (default: Restart.Unit of the existing config, else mosaic-chain-mainnet.service),
#              NODEWATCH_SKIP_APT=1 (do not install the .NET runtime with apt).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR=/opt/nodewatch
CONF_DIR=/etc/nodewatch
STATE_DIR=/var/lib/nodewatch
USER_NAME=nodewatch
DEFAULT_UNIT=mosaic-chain-mainnet.service

# The chain unit: NODEWATCH_CHAIN_UNIT, else Restart.Unit of the existing config (upgrade), else the mainnet default.
CONF_UNIT=""
if [[ -f "$CONF_DIR/appsettings.json" ]]; then
    CONF_UNIT="$(sed -n 's/^[[:space:]]*"Unit"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$CONF_DIR/appsettings.json" | head -n 1)"
fi
CHAIN_UNIT="${NODEWATCH_CHAIN_UNIT:-${CONF_UNIT:-$DEFAULT_UNIT}}"

say()  { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mHIBA:\033[0m %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "rootként futtasd: sudo ./install.sh"
[[ -f "$HERE/app/nodewatch.dll" ]] || die "nincs app/nodewatch.dll a(z) $HERE mappában – a kiadási csomagból futtasd (build.sh készíti)."
[[ "$CHAIN_UNIT" =~ ^[A-Za-z0-9@._-]+\.service$ ]] || die "érvénytelen unitnév: $CHAIN_UNIT"

# 1. .NET runtime (Ubuntu 24.04 archive package)
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft\.NETCore\.App 10\.'; then
    if [[ "${NODEWATCH_SKIP_APT:-0}" == 1 ]]; then
        die "nincs .NET 10 runtime (NODEWATCH_SKIP_APT=1 miatt nem telepítem)"
    fi
    say ".NET 10 runtime telepítése (apt install dotnet-runtime-10.0)"
    apt-get update -q
    DEBIAN_FRONTEND=noninteractive apt-get install -y -q dotnet-runtime-10.0
fi
[[ -x /usr/bin/dotnet ]] || die "/usr/bin/dotnet nem található (a systemd unit ezt használja)"

# 2. System user (no login, no home)
if ! id -u "$USER_NAME" >/dev/null 2>&1; then
    say "rendszerfelhasználó létrehozása: $USER_NAME"
    useradd --system --user-group --home-dir "$STATE_DIR" --no-create-home --shell /usr/sbin/nologin \
        --comment "NodeWatch watchdog" "$USER_NAME"
fi

# 3. Program files: replace the whole folder, so no old file stays behind
say "program telepítése: $APP_DIR"
rm -rf "$APP_DIR.new"
cp -r "$HERE/app" "$APP_DIR.new"
chown -R root:root "$APP_DIR.new"
find "$APP_DIR.new" -type d -exec chmod 0755 {} +
find "$APP_DIR.new" -type f -exec chmod 0644 {} +
rm -rf "$APP_DIR.old"
[[ -d "$APP_DIR" ]] && mv "$APP_DIR" "$APP_DIR.old"
mv "$APP_DIR.new" "$APP_DIR"
rm -rf "$APP_DIR.old"

cat > /usr/local/bin/nodewatch <<'EOF'
#!/bin/sh
# NodeWatch command line. The config is readable by root and the nodewatch group only:
#   sudo -u nodewatch nodewatch check
export DOTNET_EnableDiagnostics=0 DOTNET_CLI_TELEMETRY_OPTOUT=1
exec /usr/bin/dotnet /opt/nodewatch/nodewatch.dll "$@"
EOF
chmod 0755 /usr/local/bin/nodewatch

# 4. Config: created once, never overwritten
install -d -m 0750 -o root -g "$USER_NAME" "$CONF_DIR"
install -m 0640 -o root -g "$USER_NAME" "$HERE/appsettings.example.json" "$CONF_DIR/appsettings.example.json"
FIRST_INSTALL=0
if [[ ! -f "$CONF_DIR/appsettings.json" ]]; then
    install -m 0640 -o root -g "$USER_NAME" "$HERE/appsettings.example.json" "$CONF_DIR/appsettings.json"
    if [[ "$CHAIN_UNIT" != "$DEFAULT_UNIT" ]]; then
        sed -i "s/${DEFAULT_UNIT//./\\.}/$CHAIN_UNIT/g" "$CONF_DIR/appsettings.json"
    fi
    FIRST_INSTALL=1
    say "alap konfiguráció: $CONF_DIR/appsettings.json (szerkeszteni kell)"
else
    say "a meglévő konfigurációt megtartom: $CONF_DIR/appsettings.json"
    if [[ -n "$CONF_UNIT" && "$CONF_UNIT" != "$CHAIN_UNIT" ]]; then
        warn "a konfigurációban Restart.Unit = $CONF_UNIT, de a polkit szabály $CHAIN_UNIT unitra szól – így az újraindítás nem fog menni"
    fi
fi
install -d -m 0750 -o "$USER_NAME" -g "$USER_NAME" "$STATE_DIR"
# Files left by a manual run as another user would block the service (it must rewrite and delete them).
chown -R "$USER_NAME:$USER_NAME" "$STATE_DIR"

# 5. systemd unit and polkit rule
install -m 0644 -o root -g root "$HERE/nodewatch.service" /etc/systemd/system/nodewatch.service
[[ -d /etc/polkit-1/rules.d ]] || install -d -m 0755 /etc/polkit-1/rules.d
sed "s/${DEFAULT_UNIT//./\\.}/$CHAIN_UNIT/" "$HERE/50-nodewatch.rules" > /etc/polkit-1/rules.d/50-nodewatch.rules
chmod 0644 /etc/polkit-1/rules.d/50-nodewatch.rules
systemctl daemon-reload
systemctl enable --quiet nodewatch.service
say "systemd unit és polkit szabály telepítve (a nodewatch csak a(z) $CHAIN_UNIT unitot indíthatja újra)"

VERSION="$(/usr/local/bin/nodewatch version 2>/dev/null || echo '?')"
if [[ $FIRST_INSTALL == 1 ]] || grep -q 'IDE_JON_A_CHAT_ID' "$CONF_DIR/appsettings.json"; then
    cat <<EOF

NodeWatch $VERSION telepítve, de még nem fut. Következő lépések:
  1. Titkok (pl. Telegram bot token), soronként egy fájl:
       sudo install -m 0640 -o root -g nodewatch /dev/null $CONF_DIR/telegram.token
       sudo nano $CONF_DIR/telegram.token
  2. Beállítások:     sudo nano $CONF_DIR/appsettings.json
  3. Ellenőrzés:      sudo -u nodewatch nodewatch check
  4. Próbariasztás:   sudo -u nodewatch nodewatch test-alert
  5. Indítás:         sudo systemctl start nodewatch
  6. Napló:           journalctl -u nodewatch -f
EOF
else
    systemctl restart nodewatch.service
    say "NodeWatch $VERSION újraindítva. Napló: journalctl -u nodewatch -f"
fi
