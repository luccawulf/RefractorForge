#!/usr/bin/env bash
# Install (or update) the RefractorForge relay as a systemd service.
#
# Run as root on the server, with the published linux build in a folder:
#
#   sudo ./deploy-relay.sh /home/relay-linux-x64 'the-join-password'
#
# Idempotent: safe to re-run to push a new build. It stops the service first, which makes the relay flush
# its session, so an update never loses the edits made since the last debounced save.
set -euo pipefail

SRC="${1:-}"
PASS="${2:-}"
PORT="${RF_PORT:-7777}"
BACKUP_EVERY="${RF_BACKUP_EVERY:-15}"
KEEP_BACKUPS="${RF_KEEP_BACKUPS:-192}"
BACKUP_MAX_MB="${RF_BACKUP_MAX_MB:-512}"
# Level archives are hundreds of MB, so the store belongs on the disk that has room, not the system one.
BASE_STORE="${RF_BASE_STORE:-/data/refractorforge/base}"
# A directory of maps, one folder each. Set it and the relay hosts many maps behind the one port, with each
# editor choosing which to work on after it connects. Unset falls back to the single session in $STATE.
MAPS="${RF_MAPS:-/data/refractorforge/maps}"

APP=/opt/refractorforge-relay
# Backups live under the state folder, so it has to be on a disk that can hold RF_BACKUP_MAX_MB of them.
STATE="${RF_STATE:-/var/lib/refractorforge/session}"
UNIT=/etc/systemd/system/refractorforge-relay.service
SVC=refractorforge-relay

die() { echo "error: $*" >&2; exit 1; }

[ -n "$SRC" ] || die "usage: deploy-relay.sh <published build folder> [join password]"
[ -x "$SRC/RefractorForge.Server" ] || [ -f "$SRC/RefractorForge.Server" ] \
  || die "no RefractorForge.Server in $SRC"
[ "$(id -u)" = 0 ] || die "run as root"

echo "==> stopping $SVC if it is running (this flushes the session)"
systemctl stop "$SVC" 2>/dev/null || true

echo "==> service account and folders"
id -u refractorforge >/dev/null 2>&1 || \
  useradd --system --home "$APP" --shell /usr/sbin/nologin refractorforge
mkdir -p "$APP" "$STATE" "$BASE_STORE" "$MAPS"

echo "==> copying the build into $APP"
cp -a "$SRC"/. "$APP"/
chmod +x "$APP/RefractorForge.Server"
chown -R refractorforge:refractorforge "$APP" "$STATE" "$BASE_STORE" "$MAPS"

# The password is only ever written to the unit file, which is root-owned and mode 0600. It reaches the relay
# as the RF_PASS environment variable and never as an argument: systemd expands ${RF_PASS} in ExecStart before
# exec, so a --pass in the unit would still put the secret in argv, where `ps` shows it to every user.
if [ -z "$PASS" ] && [ -f "$UNIT" ]; then
  PASS="$(sed -n 's/^Environment=RF_PASS=//p' "$UNIT" | head -1)"
  echo "==> reusing the join password already in $UNIT"
fi
[ -n "$PASS" ] || die "no join password given and none in $UNIT; pass one as the second argument"

echo "==> writing $UNIT"
cat > "$UNIT" <<UNITEOF
[Unit]
Description=RefractorForge collaboration relay
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=refractorforge
Group=refractorforge
WorkingDirectory=$APP
Environment=RF_PORT=$PORT
Environment=RF_PASS=$PASS
Environment=RF_BACKUP_EVERY=$BACKUP_EVERY
Environment=RF_KEEP_BACKUPS=$KEEP_BACKUPS
Environment=RF_BACKUP_MAX_MB=$BACKUP_MAX_MB
Environment=RF_BASE_STORE=$BASE_STORE
Environment=RF_MAPS=$MAPS
ExecStart=$APP/RefractorForge.Server \${RF_PORT} --save $STATE --backup-every \${RF_BACKUP_EVERY} --keep-backups \${RF_KEEP_BACKUPS} --backup-max-mb \${RF_BACKUP_MAX_MB} --base-store \${RF_BASE_STORE} --maps \${RF_MAPS}
KillSignal=SIGTERM
TimeoutStopSec=20
Restart=on-failure
RestartSec=5
StateDirectory=refractorforge
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$STATE $BASE_STORE $MAPS

[Install]
WantedBy=multi-user.target
UNITEOF
chmod 600 "$UNIT"

echo "==> enabling and starting"
systemctl daemon-reload
systemctl enable "$SVC" >/dev/null
systemctl restart "$SVC"

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then
  ufw allow "$PORT"/tcp >/dev/null && echo "==> opened $PORT/tcp in ufw"
fi

sleep 2
systemctl --no-pager --lines=0 status "$SVC" || true
echo
echo "state folder : $STATE  ($(df -h "$STATE" | tail -1 | awk "{print \$4}") free on its disk)"
echo "backups      : every $BACKUP_EVERY min of activity, keeping $KEEP_BACKUPS, capped at $BACKUP_MAX_MB MB"
echo "base archives: $BASE_STORE"
echo "maps         : $MAPS"
echo "join from the editor: Collab > Join  <this server's ip>:$PORT"
echo "follow the log: journalctl -u $SVC -f"
