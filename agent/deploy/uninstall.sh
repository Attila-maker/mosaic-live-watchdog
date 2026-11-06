#!/usr/bin/env bash
# Removes NodeWatch. The config (/etc/nodewatch) and the state (/var/lib/nodewatch) are kept,
# unless --purge is given. The .NET runtime stays installed.
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "rootként futtasd: sudo ./uninstall.sh [--purge]" >&2; exit 1; }

systemctl disable --now nodewatch.service 2>/dev/null || true
rm -f /etc/systemd/system/nodewatch.service /etc/polkit-1/rules.d/50-nodewatch.rules /usr/local/bin/nodewatch
systemctl daemon-reload
rm -rf /opt/nodewatch

if [[ "${1:-}" == "--purge" ]]; then
    rm -rf /etc/nodewatch /var/lib/nodewatch
    userdel nodewatch 2>/dev/null || true
    echo "NodeWatch eltávolítva (konfigurációval és állapottal együtt)."
else
    echo "NodeWatch eltávolítva. Megmaradt: /etc/nodewatch, /var/lib/nodewatch, a nodewatch felhasználó (--purge törli)."
fi
