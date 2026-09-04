# The central relay server

`RefractorForge.Server` is the collaboration relay as its own program: no editor, no window, no GL, no audio.
It runs on a machine everybody can reach — a VPS, a home server with a public address — and every editor
connects **out** to it (**Collab ▸ Join** `<address>:<port>`). Nobody forwards a port on a home router; only
the server's own firewall needs the port open.

The session lives on the server as the canonical document. With `--save` it is persisted and survives a
restart; edits made while the server is up are written out debounced and on shutdown, with rolling backups.

What travels through it: objects, terrain, materials, foliage, gameplay, water (both bodies' colours and the
shader), sun and lighting, **placed lights**, **lighting bakes**, notes, imported meshes, and the files a decal
or a placed sound generates. A late joiner receives all of it.

## Running it

```
RefractorForge.Server [port] [seed level] [--save <state folder>] [--pass <password>] [--bind <address>]
```

| argument | meaning |
|---|---|
| `port` | TCP port to listen on. Default 7777. |
| `seed level` | A level folder, `.rfa` or `StaticObjects.con` to start the session from. Ignored when a `--save` folder already holds a session — that is resumed instead. With neither, the first editor to connect seeds the session with its own level. |
| `--save <dir>` | Persist the whole session here and resume from it next start. Without it the session is lost when the process stops. |
| `--pass <pw>` | Require this password to join. |
| `--bind <ip>` | Listen on one address only. |

Console commands while it runs: `status`, `list`, `kick <name|id>`, `save`, `quit`.

The editor's own `RefractorForge.exe --relay …` takes the same arguments and runs the same code; it is there for
a mapper who has no server build to hand. For anything that stays up, use the server.

### On Windows

The beta package ships it under `Server\` next to the editor, self-contained (it needs no .NET installed).
Keep it in its own folder: the editor package carries its own runtime and a framework-dependent build dropped
beside it will not start. To build it yourself:

```
dotnet publish src/RefractorForge.Server -c Release -r win-x64 --self-contained -o out/relay-win-x64
```

## On a Linux VPS with systemd

1. Publish for the server's platform and copy it over:

   ```
   dotnet publish src/RefractorForge.Server -c Release -r linux-x64 --self-contained -o out/relay-linux-x64
   scp -r out/relay-linux-x64/* user@server:/opt/refractorforge-relay/
   ```

   (`linux-arm64` for an ARM box. `--self-contained` means the server needs no .NET installed.)

2. A user to run it as, and the state folder:

   ```
   sudo useradd --system --home /opt/refractorforge-relay --shell /usr/sbin/nologin refractorforge
   sudo chown -R refractorforge:refractorforge /opt/refractorforge-relay
   sudo chmod +x /opt/refractorforge-relay/RefractorForge.Server
   ```

3. The unit — `tools/refractorforge-relay.service`. Set the password in its `Environment=RF_PASS=` line, then:

   ```
   sudo cp tools/refractorforge-relay.service /etc/systemd/system/
   sudo systemctl daemon-reload
   sudo systemctl enable --now refractorforge-relay
   journalctl -u refractorforge-relay -f
   ```

4. Open the port in the firewall (`sudo ufw allow 7777/tcp`, or the provider's console).

Each editor then joins `<server ip>:7777` with the password. Stopping the service (`systemctl stop`) flushes the
session first; `systemctl restart` after an update resumes it from `/var/lib/refractorforge/session`.

## Recovering a session

The state folder holds the maps and `.txt` op files, plus `_backups/<timestamp>/` snapshots (the last 12, taken
every few minutes of activity). To roll back, stop the service, copy a snapshot's files over the state folder,
start it again.
