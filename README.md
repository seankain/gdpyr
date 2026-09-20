# gdpyr

Greybox prototype of an asymmetric 6v2 FPS-vs-RTS round. Godot 4.6, C# (`net8.0`),
dedicated-server authoritative.

- [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) — scope, architecture, milestones
- [`docs/NETCODE.md`](docs/NETCODE.md) — tick model, message set, ballistics, fog of war
- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) — AWS EC2 dedicated-server runbook

## Layout

```
Scripts/Core/   Bootstrap (CLI args, engine settings), LaunchOptions
Scripts/Net/    TransportFactory, NetworkManager
Scripts/Sim/    Engine-free simulation code; unit-tested without Godot
Scripts/Fps/    Character controller, movement FSM, weapons, viewmodel
Scripts/Ui/     Pause menu, reticle, debug panel
Scenes/         Greybox map, player, weapon, pause menu
Tests/          xUnit over the engine-free sources — `dotnet test`, no Godot needed
```

## Running

Godot consumes its own arguments first, so the game's arguments go after a bare `--`.

| Command | Mode |
|---|---|
| `godot --path .` | offline, no networking (also what F5 in the editor does) |
| `godot --path . -- --listen [port]` | authority plus a local player, for solo testing |
| `godot --path . -- --client <host[:port]>` | connect to a server |
| `godot --path . --headless -- --server [port]` | headless authority, no local player |

Default port is 7777/UDP. A dedicated-server export with no mode flag defaults to
`--server` rather than to offline.

Both ends print a tick counter once a second, which is the cheapest way to see
whether they agree on time:

```
[net] server tick 600 | peers 1
[net] client tick 603 | server tick 600 | drift 3
```

Drift is expected to be non-zero and unmanaged until M1 replaces the beacon with
`SimClock` (see [`docs/NETCODE.md`](docs/NETCODE.md) §2).

## Building

```bash
dotnet build Gdpyr.csproj     # game assembly (needs no Godot install)
dotnet test                   # Tests/Gdpyr.Tests.csproj
./scripts/export-server.sh    # headless Linux server -> build/server/
./scripts/deploy.sh           # rsync to EC2 + restart the systemd unit
```
