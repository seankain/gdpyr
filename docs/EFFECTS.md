# Explosion effects

What draws an explosion, when, and how the placeholder gives way to art. Presentation only:
nothing here feeds the simulation or goes on the wire, and a peer that drew no explosions at all
would still agree with the server about every hit.

The engine-free half is `Scripts/Sim/Explosions.cs` (kinds, numbers, curves, the round ledger,
the pool), tested by `Tests/ExplosionTests.cs`. The half that draws is `Scripts/Effects/`.

## 1. What is drawn

| Event | Kind (`ExplosionKind`) | Where | Reach ring |
|---|---|---|---|
| a hand grenade's fuse runs out | `hand_grenade` | where it lies, or in the air | 7 m |
| a launcher round hits | `launcher_grenade` | the hit point | 5 m |
| a tank's main-gun round hits | `shell` | the hit point | 4.5 m |
| a barracks mortar round hits | `shell` | the hit point | 8 m |
| an FPV drone goes off | `drone` | where it was | 4 m |
| a technical, tank, supply truck or dropship is destroyed | `vehicle` | the middle of the hull | none |
| a crew vehicle — the ground force's 4x4 or helicopter — is destroyed ([`GROUND_VEHICLES.md`](GROUND_VEHICLES.md) GV-D12) | `vehicle` | the middle of the hull | none |

The reach ring is the round's own `ExplosionRadiusMeters`: it sweeps out to how far the blast did
damage, which is what tells a player why they died at 6 m from a grenade. A wreck does no blast
damage, so it has none.

Rounds that only stop — rifle, DMR, pistol, the heavy guns, the marksman — and swings keep the
small impact marker they always had (`ProjectileView.Impact`).

## 2. Where it comes from

### 2.1 Hits

The server's hit message already says whether a round went off (`HitFlags.Exploded`,
[`NETCODE.md`](NETCODE.md) §7); nothing was added to it. Which explosion that is comes from the
round's resource: `ProjectileDefinition.Explosion`, set in the five explosive files under
`Weapons/projectiles/`. A test holds every round file to it: a round names an explosion exactly
when it has a blast radius and damage.

`Explosions.ForHit` decides: nothing for a hit that did not go off, a grenade that has only
landed, or a swing; the round's kind for one that went off; and `Explosions.Unidentified` (the
launcher's, without a ring) when this peer cannot say which round it was.

A hit names its round by id alone, and on a client the round is often gone by then — its copy of a
hand grenade runs out its fuse on the client's own clock, a round trip before the server's word
arrives. So a client notes the weapon of every explosive round it sees fired, by id
(`RecentRounds`, 64 entries), and looks the hit up there. An explosion with no id is a drone's: the
only thing that goes off without being a projectile.

### 2.2 The authority's own screen

A hit is sent to clients by RPC and the authority never receives its own, so before this a listen
host or an offline round drew no impact markers and no blasts at all — only a drone's. Every hit
now goes out through `CombatManager.PublishHit`, which broadcasts it and draws it here, under the
broadcast's fog rule: a strategist on the host is not shown a hit its side's sensors do not cover.
A side effect: a listen host and an offline round now show the bullet impact markers a client
always has.

### 2.3 Vehicles

`Unit.PresentDestroyed`, called where a unit stops being alive: on the authority in
`ApplyDamage`, on a client in `ApplyRemoteState` when a record turns it from alive to dead — never
on the first record about a unit, so a peer that joins late does not see old wrecks go up. Sized
by the hull against the tank's (`Explosions.HullScale`), debris in the vehicle's colour, a fire and
a column of black smoke for about eight seconds — on the floor, if there is one within the hull's
half-height and 2.5 m; a dropship killed at altitude gets the fireball, debris and smoke, and no
fire.

`CrewVehicle.PresentDestroyed` does the same for a crew vehicle, through the same
`ExplosionEffects.SpawnWreck`: on the authority from `CombatManager.DamageCrewVehicle`, on a client
when a vehicle record first marks it destroyed — never on a new puppet's first record. It is sized
by the hull's flat radius, stood on the bottom of its lowest box, in the vehicle's colour; the hull
is drawn burnt for the two seconds before it is taken away, and a helicopter wrecked with no floor
within the hull's half-height and 2.5 m gets no fire, as a dropship does not.

## 3. The placeholder

`PlaceholderExplosion` draws every kind from primitive meshes and one light:

| Part | Says |
|---|---|
| fireball, white-hot to dull red | something went off here, and how big |
| flash (`OmniLight3D`) | lights the surroundings for a fifth of a second |
| reach ring | how far the blast did damage |
| smoke puffs | where it was, for a second or two after; a column over a wreck |
| debris | thrown out and falling; a wreck's in its own colour |
| scorch on the floor | where it was, for eight to fourteen seconds |
| fire (wrecks) | a vehicle died here |

Each kind's sizes, durations and colours are one `ExplosionProfile` in `Explosions.Profile`; how
each part changes with age is the curves beside it (`Fade`, `FireballGrowth`, `Flash`, `Reach`,
`FireballColor`, `PuffDelay`). Tune them there; the tests hold the orderings that matter — a
grenade's fireball smaller than a launcher's, smaller than a shell's, smaller than a wreck's —
and that every profile fits the meshes the placeholder builds.

## 4. Replacing it with art

`ExplosionEffects` keeps a pool per kind — 10 each of grenades, launcher rounds and shells, 4
drones, 6 wrecks — made on first need and reused; past a kind's capacity the oldest of that kind
is cut short. Playing one allocates nothing once its pool is warm.

A kind is drawn by `res://Effects/explosion_<kind>.tscn` when that file exists, with `<kind>` the
names in §1, and by the placeholder until then. The scene's root must be a `Node3D` whose script
implements `IExplosionEffect`:

| Member | Contract |
|---|---|
| `Play(in ExplosionRequest)` | start from the beginning and show it, whatever it was doing — a pooled effect can be cut short for a new one |
| `Advance(float delta)` | move it on; return false once it has finished and hidden itself |
| `Stop()` | hide it at once |

The request carries the kind, the point, the blast's reach (0: none), a scale (wrecks), the floor
under the point if one was found, the debris tint (a vehicle's colour) and a random seed. A scene
whose root is not a `Node3D` implementing the interface is reported once with a warning, and the
placeholder is drawn instead. No other code changes to swap one kind's art.

## 5. Checking it

- `explode <hand_grenade|launcher_grenade|shell|drone|vehicle>` on the `~` console draws one where
  the camera looks, on this screen only: a round's with that round's reach, `vehicle` a tank's
  wreck standing there.
- The `` ` `` debug HUD's `explosions` row: how many are playing, how many have been drawn, and
  how many were cut short (anything but 0 there means a pool is small for the fight).
- `dotnet test --filter ExplosionTests`.

TODO: check on a display, as a client and on a listen host — every kind with `explode`, a grenade
thrown, a launcher and a tank round on a wall and on the floor, a tank, a 4x4, and a dropship and
a helicopter (on the ground and in the air) destroyed, and the strategist's camera above all of it.
Built and compiled
headless, and the curves and rules tested; never looked at.

## 6. Not done

- Sound and camera shake. Weapon shots and rounds going past are heard ([`AUDIO.md`](AUDIO.md)); explosions are not.
- Structures knocked down — the pillbox, the sniper tower, a sandbag wall. `Structure` has the
  same edge (`ApplyDamage` returning true on the authority, the `Destroyed` flag arriving in
  `ApplyRemoteState` on a client) and could call `ExplosionEffects` as `Unit` does.
- Demos do not replay projectiles or hit effects ([`DEMOS.md`](DEMOS.md) §6), so a demo shows no
  rounds going off. Wrecks come from unit records, which playback applies through the same client
  path; not checked.
