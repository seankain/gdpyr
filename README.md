# gdpyr

Greybox prototype of an asymmetric 6v2 FPS-vs-RTS round. Godot 4.6, C# (`net8.0`),
dedicated-server authoritative.

- [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) — scope, architecture, milestones
- [`docs/NETCODE.md`](docs/NETCODE.md) — tick model, message set, ballistics, fog of war
- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) — AWS EC2 dedicated-server runbook

## Layout

```
Scripts/Core/   Bootstrap (CLI args, engine settings), LaunchOptions, weapon and unit catalogs
Scripts/Net/    TransportFactory, NetworkManager (transport + clocks), PlayerManager (tick loop, roster)
Scripts/Sim/    Engine-free simulation code; unit-tested without Godot
Scripts/Fps/    Character controller, movement FSM, input sampler, weapons, viewmodel
Scripts/Rts/    Units, barracks, orders, the strategist camera and selection
Scripts/Match/  CombatManager (the round), MatchState, TeamService
Scripts/Ui/     Pause menu, reticle, combat HUD, RTS HUD, net debug HUD
Scenes/         Greybox map, player, weapon, pause menu
Units/          UnitDefinition resources
Tests/          xUnit over the engine-free sources — `dotnet test`, no Godot needed
```

The simulation runs in `_PhysicsProcess` at a fixed 60 Hz and reads nothing but the recorded
`InputFrame` for the tick. `Scripts/Fps/LocalInputSampler.cs` is the only place the `Input`
singleton is read, and rendering-only work (viewmodel sway, the correction offset the camera is
drawn with) is the only thing left on the render frame.

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

The server spawns a character per connected peer at the map's `player_spawn` markers; each client
predicts its own and interpolates everyone else.

## Playing either side

`F1` puts you on the ground, `F2` in the strategist's chair. Two strategists at a time; a third
request lands on the ground. A strategist's body leaves the field and the camera moves above the
map:

| | |
|---|---|
| WASD, or the screen edge | pan |
| Q / E, mouse wheel | rotate, zoom |
| left-drag, left-click, `F` | box select, single select, select all |
| right-click | order the selection (or, with nothing selected, move the barracks rally point) |
| `X` `C` `B` `Z` | attack-move · patrol · defend · stop, applied by the next right-click |
| `1`, Backspace | queue an infantryman, cancel the last one |

Right-clicking an enemy player orders an attack on that player rather than on the ground under
them. Units are server-simulated and never predicted; the order marker appears immediately and the
units move a round trip later, which is what an RTS feels like anyway.

Press `` ` `` on a client for the net debug HUD — RTT, clock lead, input buffer depth, mispredictions
per second, prediction error, bytes in/out, server frame time. A headless server has no HUD and logs
a line every five seconds instead:

```
[net] tick 1800 | peers 3 | in 9.2 KB/s | out 22.1 KB/s
```

How to read those numbers, and how to inject latency with `tc netem` to test against something other
than a perfect link, is in [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) §4.

## Building

```bash
dotnet build Gdpyr.csproj     # game assembly (needs no Godot install)
dotnet test                   # Tests/Gdpyr.Tests.csproj
./scripts/export-server.sh    # headless Linux server -> build/server/
./scripts/deploy.sh           # rsync to EC2 + restart the systemd unit
```
