# gdpyr — Dedicated Server Deployment (AWS EC2)

The playtest strategy: one headless Godot dedicated server on an EC2 instance with an Elastic IP.
Clients connect directly to `<elastic-ip>:7777` over UDP. No player needs port forwarding, NAT
punch-through, or a relay — the server has a public address and clients dial out.

Every milestone from M0 onward is verified against this server, not against localhost. Latency,
jitter and packet-loss bugs do not reproduce on loopback.

---

## 1. One-time AWS setup

### Instance

| Setting | Value | Why |
|---|---|---|
| AMI | Ubuntu Server 24.04 LTS, **x86_64** | arm64 (`t4g`) is cheaper, but confirm a Linux arm64 .NET export template exists before betting on it. x86_64 removes the question. |
| Type | `t3.small` (2 vCPU / 2 GB) | Ample for 8 players + 50 units. `t3.micro` also works; see the CPU-credit note in §5. |
| Storage | 16 GB gp3 | Build is a few hundred MB. |
| Key pair | new, save the `.pem` | Used by `scripts/deploy.sh`. |
| Region | nearest your playtest group | Single biggest latency lever you control. |

### Security group

Two inbound rules. That is the whole firewall.

| Type | Protocol | Port | Source |
|---|---|---|---|
| SSH | TCP | 22 | **your IP only** |
| Custom UDP | UDP | 7777 | `0.0.0.0/0` |

ENet is UDP-only — there is no TCP game rule to add. Security groups are stateful, so no outbound
rule is needed for replies.

**There is deliberately no rule for the agent channel.** `--agent-api` opens a TCP socket that can
spawn players, issue orders and reset rounds — on a box with a public Elastic IP that is remote
control of the game server. `deploy/gdpyr-server.service` never passes the flag, a non-loopback bind
without `--agent-token` is a fatal start-up error, and training runs on a local machine
([`AGENT_API.md`](AGENT_API.md) §4.1, [`TRAINING.md`](TRAINING.md) §8). Two inbound rules stays two
inbound rules.

**There is no rule for LAN discovery either, and no reason for one.** The server browser in the
main menu is a UDP broadcast ([`LAN.md`](LAN.md) §4), and a broadcast does not leave the subnet — a
box with an Elastic IP could never appear in it. `deploy/gdpyr-server.service` passes
`--no-advertise` so it does not bind a port to answer a question nobody out here can ask. Players
reach this server the way this page has always said: its address, typed into the menu's address box
or passed to `--client`.

**Maps need no rule either.** A client that lacks the server's map fetches it over the game's own
UDP 7777 as it joins, and an upload to a server started with `--map-upload-token` comes the same way
(§3.1). A map put on the box by `scripts/deploy-map.sh` goes over the SSH rule.

**If and only if you use a custom Network ACL:** NACLs are *stateless*. You need inbound UDP 7777
**and** outbound UDP 1024–65535. The default VPC NACL allows everything and needs no change. This is
the single most common "the port is open but nothing connects" cause on AWS.

### Elastic IP

Allocate one and associate it with the instance so the address survives stop/start. AWS bills public
IPv4 at roughly $0.005/hr (~$3.65/mo), whether or not the instance is running — this is unavoidable
and it is the cost of a stable address.

### Bootstrap the box

```bash
ssh -i gdpyr.pem ubuntu@<elastic-ip>

sudo adduser --system --group --home /var/lib/gdpyr gdpyr
sudo mkdir -p /opt/gdpyr
sudo chown ubuntu:ubuntu /opt/gdpyr          # deploy user writes here
sudo chown gdpyr:gdpyr  /var/lib/gdpyr       # Godot user data / logs

# Godot's Linux binary dlopens its windowing libs, but minimal images are often missing these.
sudo apt-get update
sudo apt-get install -y libfontconfig1 libfreetype6

# C# builds need the .NET runtime unless your export bundles it (§2).
sudo apt-get install -y dotnet-runtime-8.0
```

If the server binary refuses to start, `ldd /opt/gdpyr/gdpyr-server` names the missing library.

---

## 2. Export

Godot 4 needs **no separate server binary**. The `Linux Server` preset is versioned in
`export_presets.cfg`; open the project in the editor once so Godot validates and normalises it
(Project → Export). Its settings:

- Platform **Linux**, architecture **x86_64**.
- Tick **Export as dedicated server**. This adds the `dedicated_server` feature tag, forces headless
  at runtime, and lets you strip client-only resources.
- Resources tab → Export Mode **Strip Visuals** (textures and materials become placeholders that keep
  only their dimensions, so scene references still resolve server-side).
- Guard client-only code with `OS.HasFeature("dedicated_server")`.

**What a C# export actually produces** — not a single file:

```
build/server/
  gdpyr-server                              # executable (PCK embedded)
  data_gdpyr-server_linuxbsd_x86_64/        # .NET assemblies + game dll
```

Both must ship. `scripts/deploy.sh` rsyncs the whole directory, which handles this.

**.NET runtime**: unlike GDScript, a C# build depends on the .NET runtime on the server. Whether the
export bundles it depends on your export settings — verify empirically by running the binary on the
clean box. If it fails looking for `hostfxr` or a runtime, `dotnet-runtime-8.0` (installed in §1)
resolves it. The project targets `net8.0`.

```bash
./scripts/export-server.sh          # writes build/server/
```

First export on a fresh checkout or in CI needs the asset import to have run — the script does
`godot --headless --import` first for this reason.

---

## 3. Deploy

```bash
export GDPYR_HOST=ubuntu@<elastic-ip>
export GDPYR_SSH_KEY=~/.ssh/gdpyr.pem

./scripts/export-server.sh && ./scripts/deploy.sh
```

`deploy.sh` rsyncs `build/server/` to `/opt/gdpyr`, restarts the systemd unit, and fails loudly if
the service does not come back up.

Install the unit once:

```bash
scp -i $GDPYR_SSH_KEY deploy/gdpyr-server.service $GDPYR_HOST:/tmp/
ssh -i $GDPYR_SSH_KEY $GDPYR_HOST \
  'sudo mv /tmp/gdpyr-server.service /etc/systemd/system/ &&
   sudo systemctl daemon-reload &&
   sudo systemctl enable --now gdpyr-server'
```

### 3.1 The map it plays

The server plays a scene map from its build, or a **data map** — a `.map` file made in the game's
map editor ([`MAPS.md`](MAPS.md) §6, [`LEVEL_EDITOR.md`](LEVEL_EDITOR.md) §9.5). A data map is not
part of the export. It goes to the box on its own, and players who have not got it fetch it from the
server as they join, so no player needs a new build or checkout for it.

```bash
./scripts/deploy-map.sh ridge.map                 # copy it; the server plays what it played
./scripts/deploy-map.sh ridge.map --activate      # copy it, make it the map, restart the unit
```

`deploy-map.sh` uses the same `GDPYR_HOST` and `GDPYR_SSH_KEY` as `deploy.sh`. It validates the
file first with `tools/Gdpyr.MapTool`, by `dotnet run` — so the .NET SDK where it runs, or
`GDPYR_MAPTOOL` naming a built `gdpyr-map` — and a file that is not a map, or does not play, is not
copied (exit 1). Then:

| Step | Where |
|---|---|
| The map is copied, as the unit's user, through a file beside it and a rename, and hashed again where it landed | `/var/lib/gdpyr/.local/share/godot/app_userdata/gdpyr/maps/<name>.map` — the unit's `HOME` is `/var/lib/gdpyr`, its one writable directory, and that is Godot's `user://maps` under it |
| With `--activate`, its SHA-256 is written there as `active`, 64 hex digits and a line's end | `…/maps/active` |
| The unit is restarted, and asked 3 s later whether it is up; if not, the script fails and prints the last 40 lines of its journal | `systemctl restart gdpyr-server` |

The name it is kept under is the file's less `.map`, or `--name <name>`: `a-z`, `0-9`, `_` and `-`,
at most 48. `GDPYR_MAP_DIR`, `GDPYR_MAP_USER` and `GDPYR_SERVICE` change the directory, its owner and
the unit, for a box set up otherwise.

**A server started with no `--map` plays the map `maps/active` names**, and the catalog's default
when there is no such file. The unit passes no `--map` for that reason. It says so as it starts:

```
[map] this server's maps are in /var/lib/gdpyr/.local/share/godot/app_userdata/gdpyr/maps/; active names af682abc8f1a…
[map] playing Ridge (…/maps/ridge.map, af682abc) from …/maps/ridge.map, sha-256 af682abc8f1a…
```

(LE8's check, with the home shortened; the hashes are 64 digits in the log.) With no `maps/active` the
first line ends `no active there: the catalog's default`.

**A `maps/active` that is not a hash, or names a map the server has not got or cannot play, is a
fatal start-up error**, exit 1 with its reason, as a bad `--map` is. The box does not come up on the
default map and say nothing: it does not come up, and `deploy-map.sh` says so. Put it right by
running `deploy-map.sh` again with a map that plays, or by deleting the file to go back to the
default. (Under the unit's `Restart=always` systemd starts it again, to refuse again; nothing in this
repository has been run under the unit — LEVEL_EDITOR LE8.) A scene map has no hash, so deleting `maps/active` is the one way
back to one.

#### Publishing from the editor, which this unit does not do

A server started with `--map-upload-token <token>` also takes maps from the game's map editor —
**Publish…**: an address, the token, and *Make it the map the server plays next* — over the game's
own UDP port, and lets a client that holds the token name its next map
([`NETCODE.md`](NETCODE.md) §12.2). **`deploy/gdpyr-server.service` does not pass it, and that is
deliberate**: with it, anybody who holds the token can write map files to this box and change the map
it plays; the link is not encrypted, so a proof overheard can have tokens tried against it at
leisure; and the token is on the process's command line, where the box's other users can read it.
Use it on a server you run by hand for a session — 16 characters or more, random, a fresh one each
time — and give maps to this one with `deploy-map.sh`, over ssh.

**Told its next map, the server plays its round out.** When the round in progress ends and its
intermission is over, it closes its link a second after its last word — every player is dropped,
and nothing tells them why — and exits 0. The unit's `Restart=always` starts it again on the new map
three seconds later, and players join again. It goes at once when no round is in progress or nobody
is connected. A server nothing restarts — one run by hand, which it knows by systemd's
`INVOCATION_ID` being absent and `GDPYR_SUPERVISED=1` not set — answers `ok_stays_down` and stays
down. One started with `--map` answers `pinned` and keeps playing its map, since it would play that
map again whenever it started. A request that names the map being played is `playing`, and calls off
a change asked for before.

---

## 4. Verify

On the server:

```bash
systemctl status gdpyr-server
journalctl -u gdpyr-server -f            # live logs
ss -ulnp | grep 7777                     # is it actually bound?
sudo tcpdump -i any -n udp port 7777     # do client packets arrive?
```

`tcpdump` is the decisive test. If packets arrive and the client still fails, the problem is in the
game. If they do not arrive, it is the security group or a NACL — not Godot.

On the client:

```bash
gdpyr --client <elastic-ip>:7777
```

For friends on Windows, ship a desktop shortcut with the argument already appended; do not make them
type an IP.

### Reading the net HUD

Press `` ` `` on a client to open the debug panel. It carries the numbers from
[`NETCODE.md`](NETCODE.md) §8: RTT and jitter, the input clock's lead over the server-tick estimate,
the server-side input buffer depth, clock nudges and resyncs, mispredictions per second, mean and
max prediction error, bytes in and out, and the server's physics frame time.

A healthy connection reads: buffer depth 2–4, nudges climbing slowly and resyncs not at all, mean
prediction error under 2 cm, mispredictions per second near zero while walking in a straight line.

The server has no HUD and logs a line every five seconds instead:

```
[net] tick 1800 | peers 3 | in 9.2 KB/s | out 22.1 KB/s
```

### Injecting latency

Real links are the point — but reproducing a *specific* one is how you tell a netcode bug from a bad
afternoon. On a Linux client, against the interface that carries the game traffic:

```bash
sudo tc qdisc add dev eth0 root netem delay 40ms 10ms distribution normal   # 80 ms RTT, 20 ms jitter
sudo tc qdisc change dev eth0 root netem delay 40ms 10ms loss 2%            # add packet loss
sudo tc qdisc del dev eth0 root                                             # back to normal
```

`netem` shapes egress only, so 40 ms here is ~80 ms round trip. On Windows, Clumsy does the same
job. Watch the HUD while it is on: the input buffer should settle back into 2–4 within a second or
two of a change, and prediction error should stay flat.

---

## 5. Operating notes

**Cap the framerate.** Headless Godot runs its main loop as fast as the CPU allows and will hold a
core at 100%, which drains `t3`/`t4g` CPU credits within hours and then throttles you mid-playtest.
Set `Engine.MaxFps = 60` in the server bootstrap path. Confirm with `top` that the process sits well
under 100% of one core.

**Bandwidth is not a concern at this scale.** Using the 25 KB/s per-client downstream budget from
[`NETCODE.md`](NETCODE.md) §7: 8 players ≈ 200 KB/s ≈ 0.7 GB/hr. AWS's 100 GB/mo free egress covers
roughly 140 hours of full-lobby play.

**Cost**, approximate and region-dependent: `t3.small` on-demand ≈ $15/mo running continuously, plus
≈ $3.65/mo for the public IPv4, plus a little EBS. Stop the instance between playtests and compute
drops to near zero; the IPv4 and EBS charges continue.

**`export_presets.cfg` is tracked** (M0 removed it from `.gitignore`) so the server preset is
versioned and reproducible. It holds no secrets for this project — the credential risk in that file
is Android keystore passwords, which do not apply here.

**Parallel test servers**: run a second unit on port 7778 with its own `ExecStart` and security-group
rule when you want to A/B two builds in one session.

---

## 6. When this stops being enough

- **Friends find the IP-and-shortcut flow annoying** → Steam lobbies + Steam Datagram Relay (M9).
  The transport sits behind `Scripts/Net/TransportFactory.cs`, so this is one factory method.
- **Players spread across continents** → a second instance in another region. Nothing in the design
  assumes one server.
- **You want zero-touch deploys** → the same two scripts from a GitHub Actions job; no other change.
