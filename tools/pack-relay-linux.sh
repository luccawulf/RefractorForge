#!/usr/bin/env bash
# Package the collaboration relay for a Linux VPS: one self-contained binary plus the two files a server
# operator needs (the guide and the systemd unit). Run from the repo root:
#
#     bash tools/pack-relay-linux.sh v0.20.0-beta
#
# The tar is built in TWO passes because the interesting bit is file modes: the binary and the script have to
# come out executable on a machine that never sees Windows ACLs, so `--mode` is applied explicitly rather than
# trusted from the filesystem.
set -euo pipefail

VERSION="${1:?usage: pack-relay-linux.sh <version, e.g. v0.20.0-beta>}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE="$REPO/dist/relay-stage"
OUT="$REPO/dist/RefractorForge.Server-$VERSION-linux-x64.tar.gz"

echo "== publishing the relay for linux-x64 =="
rm -rf "$REPO/src/RefractorForge.Server/bin/Release/net10.0/linux-x64"
dotnet publish "$REPO/src/RefractorForge.Server" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=none --nologo | tail -2

PUB="$REPO/src/RefractorForge.Server/bin/Release/net10.0/linux-x64/publish"
[ -f "$PUB/RefractorForge.Server" ] || { echo "the relay did not publish"; exit 1; }

echo "== staging =="
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp "$PUB/RefractorForge.Server" "$STAGE/"
cp "$REPO/docs/RelayServer.md" "$STAGE/"
cp "$REPO/tools/refractorforge-relay.service" "$STAGE/"
cp "$REPO/LICENSE.txt" "$STAGE/" 2>/dev/null || cp "$REPO/LICENSE" "$STAGE/LICENSE.txt"

echo "== taring =="
rm -f "$OUT"
tar -czf "$OUT" -C "$STAGE" --mode=0755 ./RefractorForge.Server
tar -rzf "$OUT" -C "$STAGE" --mode=0644 ./RelayServer.md ./refractorforge-relay.service ./LICENSE.txt 2>/dev/null \
  || {  # `tar -r` cannot append to a compressed archive on every build; fall back to one pass with 0755.
        rm -f "$OUT"
        tar -czf "$OUT" -C "$STAGE" --mode=0755 ./RefractorForge.Server ./RelayServer.md ./refractorforge-relay.service ./LICENSE.txt
     }

ls -la "$OUT"
tar -tzvf "$OUT"
