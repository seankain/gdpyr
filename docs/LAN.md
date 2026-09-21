# gdpyr — LAN hosting with a dev build

One room, one checkout per machine, no export and no cloud. [`DEPLOYMENT.md`](DEPLOYMENT.md) is the
other end of the same problem — an exported dedicated server on a box with a public address, which
is what every milestone is verified against. This page is the short path: an evening of play, or a
two-machine check of something you can watch happen, entirely out of `godot --path .`.

Know what a LAN costs you before you trust it: sub-millisecond RTT hides every bug that latency
causes. Playing on one is fine; *verifying* netcode on one is not, and §6 says what to do instead.

---

## 1. What "a dev build" is here

The repository ships one export preset — `Linux Server` in `export_presets.cfg`, `runnable=false`,
dedicated-server. **There is no client preset**, so every machine that plays runs the project from a
checkout. That is the dev build: a Godot 4.6 .NET editor binary pointed at `--path .`.

Per machine:

| | |
|---|---|
| Godot | 4.6, the .NET build (the plain build cannot run C#) |
| .NET SDK | 8.0 — the project targets `net8.0` |
| Source | the checkout, at the **same commit** as everyone else (§5) |

```bash
git rev-parse --short HEAD            # everyone reads the same thing (§5)
dotnet build Gdpyr.csproj             # needs no Godot install
godot --headless --path . --import    # fresh checkout only: no .godot/ import cache yet
```

The editor builds the assembly when you press F5; launching from the command line does not, so a
stale or missing build is a stale or missing build. `scripts/export-server.sh` runs the same
`--import` first, for the same reason.

Godot consumes its own arguments first, so every game flag goes after a bare `--`. The project
renders on GL Compatibility, which is why a modest laptop is a usable seat.

---

## 2. Pick a host

| Host | Command | Notes |
|---|---|---|
| Dedicated, headless | `godot --headless --path . -- --server 7777` | A spare machine, or a second process on someone's. No local player. The fair option. |
| Listen | `godot --path . -- --listen 7777` | Authority plus a local player. One machine fewer, two asymmetries below. |

Two things about `--listen` are worth knowing before you pick it:

- **The host owes itself no lag compensation.** `NetworkManager.LagCompensationTicks` returns 0 for
  the listen host's own character; its input never crosses a jitter buffer and it never mispredicts,
  because there is nothing to predict ([`NETCODE.md`](NETCODE.md) §3, §4.3). Host advantage here is
  the design, not a rumour.
- **The host's process is not framerate-capped.** `Bootstrap` sets `Engine.MaxFps = 60` only for
  `LaunchMode.Server`. A listen host renders as fast as the GPU allows and the authority shares that
  CPU with it.

Running the authority as its own headless process removes both, even when it runs on the same
machine as a player and the client dials `127.0.0.1`. It costs one process.

---

## 3. Host it

```bash
dotnet build Gdpyr.csproj
godot --headless --path . -- --server 7777 --bots 6:1
```

The listener is already reachable from the room: `TransportFactory.CreateServer` leaves ENet's bind
address at its wildcard default and nothing in the project calls `SetBindIp`. There is no
"bind to the LAN" flag and none is needed — what stands between the room and the host is the host's
firewall.

### 3.1 The host's address

```bash
ip -4 -brief addr           # Linux
ipconfig getifaddr en0      # macOS (the interface that carries the LAN)
ipconfig                    # Windows -> IPv4 Address
```

Take the private address on the interface that carries the LAN — `192.168.x.x`, `10.x.x.x`, or
`172.16–31.x.x`. Machines with Docker, WSL or a VM bridge have several; the one on the same /24 as
your friends' machines is the one to hand out.

### 3.2 Firewall

One inbound rule, UDP only. ENet has no TCP channel to open.

```bash
sudo ufw allow from 192.168.0.0/16 to any port 7777 proto udp    # Ubuntu
sudo firewall-cmd --add-port=7777/udp                            # Fedora (--permanent + --reload to keep)
```

```powershell
New-NetFirewallRule -DisplayName "gdpyr" -Direction Inbound `
  -Protocol UDP -LocalPort 7777 -Profile Private -Action Allow
```

On Windows the first bind normally raises the Defender prompt — but a prompt dismissed once leaves a
*block* rule behind and never asks again, so check the inbound rules rather than waiting for it. On
macOS, allow the Godot binary in System Settings → Network → Firewall → Options.

Two hosts on one LAN want two ports: `--server 7777` and `--server 7778`, one rule each.

### 3.3 Seats, caps and bots

| Limit | Value | Where |
|---|---|---|
| Simultaneous clients | 16 | `TransportFactory.MaxClients` |
| Roster slots in a snapshot (people **and** bots) | 16 | `SnapshotCodec.MaxPlayers` |
| Strategists at once | 2 | `TeamService.MaxStrategists` |
| The round the design is scoped to | 6v2 | [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) |

The strategist cap is the only one enforced against people: a third `F2` lands on the ground
(`TeamService.Assign`), it does not error. Nothing caps the ground force below the transport's 16.

Bots default to 6 ground and 1 strategist from `Match/default_gamemode.tres`, and the flags override
what they name and nothing else:

| Flag | Ground target | Strategist target |
|---|---|---|
| *(none)* | 6 | 1 |
| `--bots 8` | 8 | 1 (game mode) |
| `--bots 6:2` | 6 | 2 |
| `--bots 0` | 0 | 1 (game mode) |
| `--no-bots` | 0 | 0 |

`--no-bots` is the one that means "nobody but us"; `--bots 0` leaves the strategist bot in its chair.
Either way bots only hold seats nobody wants and get up when a person takes one
(`BotFillPolicy.Plan`), and an empty server runs none at all — a host with nobody connected is
supposed to look idle.

Latecomers need no lobby: a peer that connects mid-round is spawned and sent the existing roster
(`PlayerManager.OnPeerJoined`), and walks in where it stands.

---

## 4. Join from the other machines

```bash
dotnet build Gdpyr.csproj
godot --path . -- --client 192.168.1.20         # port defaults to 7777
godot --path . -- --client 192.168.1.20:7778    # or name one
```

A hostname works wherever the machine can resolve it (`hostbox.local` on a network with mDNS); an IP
removes the question. Don't make people type either one twice — drop a wrapper next to the checkout:

```bash
#!/usr/bin/env bash
# play.sh — join the host, from this checkout
cd "$(dirname "$0")" || exit 1
dotnet build Gdpyr.csproj || exit 1
exec "${GODOT4:-godot}" --path . -- --client "${1:-192.168.1.20}"
```

`.vscode/launch.json` already carries **Launch (Listen Server)**, **Launch (Headless Server)** and
**Launch (Client -> localhost)**, all of which run `$GODOT4` after the `build` task; copying the
client one and changing the address is the editor-side equivalent.

In the round: `F1` ground, `F2` strategist, `` ` `` net HUD, `Esc` for pause and quit. The rest of
the controls are in the README.

---

## 5. One commit, every machine

**Nothing checks that the two ends were built from the same source.** There is no version in the
handshake, and `SimConfig` is explicit about why that matters: *"Server and clients must be built
from the same values. Every one of these is a protocol constant, not a tuning knob: changing one
changes the meaning of the bytes on the wire or of a recorded tick index."*

So a mismatched client connects — successfully — and then disagrees. What that looks like:

- mispredictions per second that are not zero while walking in a straight line, and a mean
  prediction error that climbs past the healthy couple of centimetres ([`DEPLOYMENT.md`](DEPLOYMENT.md) §4)
- shots that land on nothing, units drawn where they are not, a round that ends on one screen only
- a decode that throws outright, if a codec's layout moved

`git rev-parse --short HEAD` on every machine before the first round, and `dotnet build` after every
pull. It is thirty seconds against an evening of blaming the Wi-Fi.

---

## 6. Verify the link

The host logs, and on a dev build the boot line always says `dedicated server: False` — that tag
comes from the export preset, not from `--server`:

```
[boot] gdpyr Server on port 7777 | bots 6 ground, 1 strategist | tick 60 Hz | dedicated server: False
[net] listening on UDP 7777 (Server)
[net] peer 1234567890 connected (1 total)
[net] tick 1800 | peers 3 | in 9.2 KB/s | out 22.1 KB/s
```

The heartbeat is one line every five seconds, and a client prints its own version of it — RTT, clock
lead, buffer depth, mispredictions per second — which is worth having on screen when the client is
the machine you are debugging from.

If the host says it is listening and a client still cannot reach it:

```bash
ss -ulnp | grep 7777                     # bound, and by whom?
sudo tcpdump -i any -n udp port 7777     # do the client's packets arrive at all?
```

`tcpdump` is decisive here exactly as it is on EC2: packets arriving while the game fails is a game
problem; packets not arriving is the firewall, the address, or client isolation on the access point.

### Reading the HUD on a LAN

`` ` `` opens the debug panel ([`NETCODE.md`](NETCODE.md) §8). Healthy, wired:

| Number | Expect |
|---|---|
| RTT | ~1 ms or below |
| Input buffer depth | 2–4, steady |
| Clock nudges / resyncs | rare / none |
| Mean prediction error | under 2 cm |
| Mispredictions/s | ~0 while walking straight |

Wi-Fi shows up as jitter rather than as latency: buffer depth wandering, nudges that never settle.
Fine for playing, misleading for measuring.

### A LAN hides the bugs the game ships into

At sub-millisecond RTT the lag-compensation rewind rounds to zero ticks, interpolation always has
two snapshots to work with, and the jitter buffer never has to defend itself. Nothing latency-shaped
can reproduce. When the point is netcode rather than an evening of play, shape the client's link and
watch the same HUD: [`DEPLOYMENT.md`](DEPLOYMENT.md) §4 has the `tc netem` lines (40 ms delay with
10 ms jitter ≈ an 80 ms broadband round trip), and Clumsy does the job on Windows.

---

## 7. When it does not come up

| Symptom | Cause | Fix |
|---|---|---|
| `gdpyr: unrecognized argument '…'` then the usage block, exit 1 | a typo, or a game flag Godot already ate | every game flag goes after a bare `--` |
| Godot's own "unknown command line argument" | the flag landed before the `--` | same |
| `[net] could not listen on UDP 7777: …`, exit 1 | port already bound — usually a previous run | `ss -ulnp \| grep 7777`, kill it or pick another port |
| `[net] connection failed: no answer from the server.` | firewall, wrong address, host not running, or the client is on another subnet / a guest SSID with client isolation | §3.2, then `tcpdump` on the host |
| `[net] could not open a socket to …` on the client | the client's own socket never opened; the process **continues offline** (`NetworkManager.StartClient` resets `Mode`) | read the log before wondering why the map is empty |
| Connected, but nothing else is on the map | the host was started `--no-bots` and you are first | wait for people, or restart the host with bots (§3.3) |
| Everything connects and then disagrees | mismatched builds | §5 |
| `--agent-api … is not loopback and has no --agent-token` | refusing to expose the agent channel | §8 |

Both halves on one machine, which is also the smallest possible repro for anything netcode-shaped:

```bash
godot --headless --path . -- --server 7777 &
godot --path . -- --client 127.0.0.1
```

---

## 8. The agent channel stays off

`--agent-api` is not part of a LAN party. The socket can spawn players, issue orders and reset
rounds, so it binds `127.0.0.1` by default and a non-loopback bind without `--agent-token` is a
fatal start-up error, not a warning ([`AGENT_API.md`](AGENT_API.md) §4.1). "The LAN" is also
whatever else is on the Wi-Fi.

If a harness genuinely has to drive the host from another machine, in order of preference:

1. Run it on the host and leave the channel on loopback.
2. Forward it: `ssh -L 7900:127.0.0.1:7900 host` — no bind change, no firewall rule.
3. Bind it wide and defend it, which is two decisions and not one:

```bash
godot --headless --path . -- --server 7777 \
  --agent-api 0.0.0.0:7900 --agent-token "$GDPYR_AGENT_TOKEN"
```

...plus an inbound TCP rule for 7900 scoped to that one machine's address.

Note that stepped mode — the one training and deterministic runs use — **refuses to engage while any
non-agent peer is connected** ([`AGENT_API.md`](AGENT_API.md) §5.2). The server people are playing
on is not the server a training run steps, and it says so rather than quietly ruining the round.

---

## 9. When a LAN stops being enough

- **People stop being in the same building** → [`DEPLOYMENT.md`](DEPLOYMENT.md): the same build,
  exported once, on a box with a public address. No player needs port forwarding; the clients dial
  out.
- **You want the numbers to mean something** → also `DEPLOYMENT.md`. Latency, jitter and loss bugs
  do not reproduce on a switch.
- **Handing out an IP gets old** → Steam lobbies and the Steam Datagram Relay are M9, and the
  transport sits behind `Scripts/Net/TransportFactory.cs` for that reason.
