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
                      [--backup-every <minutes>] [--keep-backups <n>]
```

| argument | meaning |
|---|---|
| `port` | TCP port to listen on. Default 7777. |
| `seed level` | A level folder, `.rfa` or `StaticObjects.con` to start the session from. Ignored when a `--save` folder already holds a session — that is resumed instead. With neither, the first editor to connect seeds the session with its own level. |
| `--save <dir>` | Persist the whole session here and resume from it next start. Without it the session is lost when the process stops. |
| `--pass <pw>` | Require this password to join. |
| `--bind <ip>` | Listen on one address only. |
| `--backup-every <minutes>` | How often to take a timestamped backup. Default 5. A backup is only written when something changed in that interval, so an idle server writes none. |
| `--keep-backups <n>` | How many backups to keep before the oldest is pruned. Default 12. |
| `--backup-max-mb <mb>` | Cap the total size of `_backups/`. Default 512. Oldest snapshots are dropped until it fits; the newest is never dropped. |
| `--base-store <dir>` | Keep the level archive here so a joiner who does not have the map can be given it. Without it the relay can only tell people their archive is the wrong one. |
| `--maps <dir>` | Host MANY maps: one folder per map under here, and each editor chooses which to work on after connecting. A name nobody has used yet creates that map. Without it the relay hosts the single session in `--save`. |

`--backup-every` multiplied by `--keep-backups` is the entire window you can roll back through. The defaults
give about an hour, which is fine while two people are working together and useless on a server people log
into at different hours: someone breaks the terrain on Tuesday night, you notice on Thursday, and the last
good snapshot is long gone. For a team, size the window to outlast the gap between one person breaking
something and the next person seeing it. `--backup-every 15 --keep-backups 192` is two days of solid work
and far more calendar time than that, for a few hundred MB of disk.

Set `--backup-max-mb` as well, because a count is not a bound on disk. A session holding only objects is a
couple of hundred KB, so 192 of them is under 40 MB. The same session once someone syncs terrain and
material maps is megabytes, and 192 of those is gigabytes. The cap is what stops the relay quietly filling
a disk that other things are living on.

The backups live inside the `--save` folder, so that folder has to be on a disk that can hold the cap. Put
both there rather than on the system disk: a relay usually shares a machine with something else, and
the something else is what stops working when the disk fills.

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

   Or run `tools/deploy-relay.sh`, which does all of steps 2 to 4 and is safe to re-run to push a new build:

   ```
   sudo ./deploy-relay.sh /path/to/relay-linux-x64 'the-join-password'
   ```

   It stops the service first, so the relay flushes its session and an update never loses the edits made
   since the last debounced save. It writes the password only into the unit file, which it chmods to 600 —
   never onto a command line, where `ps` would show it to every user on the box.

4. Open the port in the firewall (`sudo ufw allow 7777/tcp`, or the provider's console).

Each editor then joins `<server ip>:7777` with the password. Stopping the service (`systemctl stop`) flushes the
session first; `systemctl restart` after an update resumes it from `/var/lib/refractorforge/session`.

## Recovering a session

The state folder holds the maps and `.txt` op files, plus `_backups/<timestamp>/` snapshots, as many as
`--keep-backups`, taken no more often than `--backup-every` and only when something changed. To roll back:

```
sudo systemctl stop refractorforge-relay
sudo cp /var/lib/refractorforge/session/_backups/<stamp>/* /var/lib/refractorforge/session/
sudo chown -R refractorforge:refractorforge /var/lib/refractorforge
sudo systemctl start refractorforge-relay
```

Stop the service before copying. The relay flushes its own state on shutdown, so restoring underneath a
running server is overwritten the moment anyone makes an edit.

Everyone connected when you roll back is still holding the newer document, and the first edit any of them
makes will push part of it back. Have people disconnect first, then rejoin after the restart.
