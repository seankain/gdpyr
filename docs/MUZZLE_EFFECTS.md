# Muzzle effects

What is drawn at a gun when it fires — a flash, a puff of smoke and a spent case thrown out of
the side — for whom, and what it costs. Presentation only: nothing here feeds the simulation or
goes on the wire, and a peer that drew none of it would still agree with the server about every
hit.

The engine-free half is `Scripts/Sim/Muzzles.cs` (the parts, the profiles, the ranges, where a
body's muzzle is, which way a case is thrown and how it falls, and how each part changes with
age), tested by `Tests/MuzzleTests.cs`. Where a shot's muzzle is found is
`Scripts/Match/CombatManager.Muzzles.cs`; what draws it is `Scripts/Effects/MuzzleEffects.cs` and
`Scripts/Effects/MuzzleFlash.cs`.

## 1. What is drawn

What a weapon draws is its shot's: the same `WeaponDefinition.Shot` kind that says what it
sounds like ([`AUDIO.md`](AUDIO.md) §1). A weapon heard firing is seen firing, and one that is
not — the hammer, a hand grenade, the FPV drone, the ram — draws nothing; a test holds every
weapon file to that.

| Kind | Flash | Smoke | Case |
|---|---|---|---|
| `pistol` | small | one wisp | 9 mm |
| `rifle` | | one puff | 5.56 mm |
| `dmr` (the DMR, the marksman) | | two puffs | 7.62 mm |
| `machine_gun` (the heavy gunner's LMG) | | one puff | 5.56 mm, thrown down |
| `heavy_machine_gun` (the technical's, the 4x4's, the deployed heavy gun, the turret) | large | two puffs | 12.7 mm, thrown down |
| `launcher` | short and wide | three puffs | none: the 40 mm case stays in the launcher |
| `cannon` (the tank) | metres long | a cloud | none: it falls inside the turret |
| `mortar` (the barracks', the carried one) | | a cloud | none |

## 2. For whom

### 2.1 Everyone else's shots: near the camera only

The rule that keeps a fight cheap: someone else's shot is drawn only when its muzzle is within a
few metres of the camera, and each part to its own reach (`MuzzleRanges`):

| Part | Drawn within | Why that far |
|---|---|---|
| flash | 10 m | it tells you where a shot came from, and it is the cheapest — two quads and a light for three frames |
| smoke | 8 m | a translucent puff a few centimetres across is lost past that |
| case | 5 m | a case is a few centimetres long, and the one part with a ray cast |

A slow, big gun stretches all three by its kind's `RangeScale`: the heavy machine gun 1.5×, the
mortar 2.5×, the tank's cannon 3× — a cannon's smoke is metres across and it fires three times a
minute. A shot outside every reach costs one squared distance and is counted, not drawn.

Distances are from the camera being drawn from, whoever's it is — a player's head, the spectator's
free camera, the strategist's above the field (from which nothing is near enough). On the
authority — a listen host, an offline round — a strategist does not see a shot its side's sensors
do not cover, as a client playing one is not sent it ([`AUDIO.md`](AUDIO.md) §2.1).

Where the muzzle is, from the events the simulation already has (a client's spawn message, the
authority's `SpawnProjectile`), and drawn from where the shooter is drawn now rather than where the
server fired it:

| Shooter | Its round leaves from | The muzzle is drawn |
|---|---|---|
| a player with a weapon in their hands, on foot or a passenger | their eye | held ahead of it (`Muzzles.HeldMuzzle`): `HoldReach` along the line of fire, `HoldDrop` below it, `HoldSide` to its right |
| a player behind a mounted gun, or a vehicle's gunner | the gun's muzzle | there |
| a unit on foot — a rifleman, a heavy gunner, a sniper, a builder | its eye | held ahead of it, as a player's |
| a vehicle unit — a technical, a tank, a supply truck | the middle of its hull | at the hull's edge on the line of fire (`Muzzles.HullMuzzle`) |
| a barracks defence, a sniper tower, a pillbox | its muzzle | there |

The flash follows the shooter's node for its few frames, so it does not hang in the air behind a
body that is moving. Smoke and cases are left in the world where they were thrown; a case leaves
with the shooter's velocity added to its own.

### 2.2 The local player's own shots: always, from the viewmodel

The local player's own shots are drawn whole wherever the camera is, on the tick their client
predicts them — with the tracer and the sound, and not again when the server's spawn arrives. On a
listen host or offline, from the authority's spawn.

A weapon in their hands is drawn from the end of the first-person viewmodel's barrel
(`WeaponInit.TryGetMuzzle`), and the flash follows the viewmodel as the sights come up. Where the
barrel ends is the weapon's `ViewmodelMuzzle` when it has one (§4); until then, the face of the
mesh's bounds the camera looks out of, three-quarters of the way up it, where a bore is on most
guns (`Muzzles.BarrelEnd`). With a scope's eyepiece up the viewmodel is out of shot, and the flash
is drawn where a body would hold the muzzle, following the camera. A mounted or seated gun is drawn
from its own muzzle.

Two things differ from anyone else's, both for the eye half a metre behind the muzzle: the flash
is drawn 1.6× its size (`Muzzles.OwnFlashScale` — the gun hides the half of it below the barrel),
and the smoke at 35 % of its opacity (`Muzzles.OwnSmokeAlpha` — with the sights up it is on the line
of sight).

## 3. The placeholders

Everything is made in code: two meshes, a light and three textures drawn into memory when the
effects are first made — nothing to import.

| Part | Drawn as |
|---|---|
| flash | two quads crossed along the barrel (the flame from the side) and a four-pointed star facing the camera (the flame end on), white-hot in the middle to orange at the edge; an `OmniLight3D` without shadows on what is round the muzzle |
| smoke | camera-facing soft discs that leave down the barrel's line, are slowed by the air within a quarter second, climb, grow to three times their size and thin out |
| case | a six-sided brass cylinder, tumbling, that falls, bounces off the floor under the port up to three times — keeping 35 % of its speed into the floor and 55 % along it — and lies on its side until it shrinks away |

The numbers per kind, all in one `MuzzleProfile` each in `Muzzles`:

| Kind | Flash, length × width | For | Light, energy / reach | Smoke | Case | Held, ahead / below / right | Reach |
|---|---|---|---|---|---|---|---|
| `pistol` | 0.12 × 0.09 m | 40 ms | 1.5 / 3 m | 1 × 4 cm, 0.6 s | 19 × 9.9 mm, 3 s | 0.5 / 0.12 / 0 m | 1× |
| `rifle` | 0.22 × 0.12 m | 50 ms | 2 / 4 m | 1 × 6 cm, 0.8 s | 45 × 9.6 mm, 4 s | 0.7 / 0.15 / 0.12 m | 1× |
| `dmr` | 0.28 × 0.15 m | 50 ms | 2.5 / 5 m | 2 × 8 cm, 1 s | 71 × 12 mm, 4 s | 0.85 / 0.15 / 0.12 m | 1× |
| `machine_gun` | 0.24 × 0.14 m | 50 ms | 2.2 / 4.5 m | 1 × 7 cm, 0.9 s | 45 × 9.6 mm, 4 s | 0.75 / 0.25 / 0.15 m | 1× |
| `heavy_machine_gun` | 0.45 × 0.3 m | 60 ms | 3.5 / 7 m | 2 × 12 cm, 1.3 s | 99 × 20 mm, 5 s | mounted | 1.5× |
| `launcher` | 0.15 × 0.18 m | 40 ms | 1.5 / 3 m | 3 × 10 cm, 1.4 s | none | 0.6 / 0.18 / 0.12 m | 1× |
| `cannon` | 1.6 × 1.1 m | 90 ms | 8 / 14 m | 6 × 45 cm, 2.5 s | none | at the hull's edge | 3× |
| `mortar` | 0.35 × 0.4 m | 60 ms | 4 / 6 m | 4 × 22 cm, 2 s | none | mounted | 2.5× |

Smoke radii are where a puff starts. Each flash's size is varied by up to a fifth either way, so a
burst is not one picture repeated. How each part changes with age is the curves beside the
profiles (`FlashScale`, `FlashLight`, `SmokeScale`, `SmokeOpacity`, `SmokeOffset`, `CasingScale`),
and a case's flight is `Muzzles.StepCasing`.

### 3.1 What it costs

| | |
|---|---|
| a shot out of reach | one squared distance |
| a flash | a pooled node of three quads and a light, for two to six frames; 12 at once, past which the oldest is cut short |
| smoke | every puff in one `MultiMesh`: one draw call; 128 at once |
| cases | every case in one `MultiMesh`: one draw call; 96 at once — three magazines; one ray cast down from the port when a case is thrown, for the floor it lands on |
| a dedicated server | nothing: the effects are never made |

Nothing is allocated once the pools are warm. Past a capacity the oldest flash, puff or case is
taken for the newest and counted; the debug HUD shows the count (§5).

## 4. Replacing them with art

Each weapon resource has a `ViewmodelMuzzle` (under *Muzzle* in the inspector): where the end of
the barrel is on its viewmodel `Mesh`, in the mesh's own space. Leave it at zero to take it from the
mesh's bounds, which is right for a greybox box; set it for a modelled weapon whose bore is not
three-quarters of the way up the front of its bounds. The greybox rifle's flash with the sights up
shows over the top of its box rather than out of a barrel, because the box is the barrel.

The flash, the smoke and the case themselves have no art path yet (§6).

## 5. Checking it

- `muzzle <weapon> [own]` on the `~` console draws a weapon's muzzle effects 3 m ahead of the
  camera, firing across the view from left to right, or with `own` from your own weapon in your
  hands; it says which parts were drawn. `muzzle range` says how far each part is drawn for
  others' shots, and `muzzle range <flash> <smoke> <cases>` sets it, for this peer, until it
  leaves. A name of two words is typed whole or with an underscore, as for `sound`.
- The `` ` `` debug HUD's `muzzle` row: flashes, puffs and cases up now, how many shots were drawn
  and how many were too far off to draw anything, and how many were cut short (anything but 0
  there means a capacity is small for the fight).
- `dotnet test --filter MuzzleTests`: the profiles and the weapon files, the ranges, where a muzzle
  is held, the viewmodel's barrel from its bounds, a case's ejection and flight, and the curves.
- `./scripts/muzzle-effects-checks.sh` (a Godot 4.6 .NET binary on `PATH` or in `$GODOT`): a listen
  host, headless, its own player through the input map — 30 facts: their own rifle draws a flash at
  the end of the viewmodel's barrel and following it, a puff and a case a round, the case out to the
  right and at rest on the floor, one set a round through a burst, and the same with the sights up
  and with the pistol; a rifle round fired 3 m from the camera draws every part, 6 m flash and smoke,
  9 m the flash, 30 m nothing; a rifleman 4 m off flashes ahead of its eye, following it, and a tank
  at the edge of its hull with smoke 9 m off and no case; a hand grenade draws nothing; the console's
  `muzzle`; everything taken back. With `SHOTS=<dir>` it runs on a display instead — `$DISPLAY`, or
  `xvfb-run` — with the Compatibility renderer, and saves a screenshot at each step with time slowed
  so a flash is still up to be caught.

TODO: look at on a display at full speed, as a client and on a listen host, under the project's
own renderer — your own rifle, pistol, DMR and launcher from the hip and with the sights up, a
teammate and a strategist's rifleman beside you, a heavy gunner's burst, the mounted heavy gun, a
tank and a mortar, and cases on a floor and a slope. Built, tested and checked headless; looked at
only in stills from the Compatibility renderer on software GL under Xvfb.

## 6. Not done

- No art path for the parts themselves: the flash's textures, the smoke's and the case's mesh are
  made in code. A scene per kind, as explosions have ([`EFFECTS.md`](EFFECTS.md) §4), would be the
  way in.
- A third-person body holds no weapon, so another player's or a unit's muzzle is put ahead of its
  eye by the kind's hold, not found on a hand or a gun: the first shot of a burst, before the arms
  have come up into the aim, flashes ahead of a head over arms that are still down.
- A vehicle unit's muzzle is its hull's edge on the line of fire, not a modelled barrel.
- A case lands on the floor found straight under the port when it is thrown: it passes through
  walls, and on a slope or a step lies at the height it was found at.
- No sound of a case landing, no belt links from belt-fed guns, no heat haze, no muzzle smoke
  lingering after a long burst beyond each puff's own life.
- Demos do not replay projectiles ([`DEMOS.md`](DEMOS.md) §6), so a demo draws no muzzle effects.
