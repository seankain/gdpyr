# Weapon audio

What is heard when a weapon fires and when a round goes past the listener, and how the
placeholders give way to recordings. Presentation only: nothing here feeds the simulation or goes
on the wire, and a peer that played no sound at all would still agree with the server about every
hit.

The engine-free half is `Scripts/Sim/WeaponSounds.cs` (kinds, the numbers each placeholder is made
and played with, and the synthesis that makes it) and `Scripts/Sim/Flyby.cs` (when a round has gone
past the ear), tested by `Tests/WeaponSoundTests.cs`. When a sound is played is
`Scripts/Match/CombatManager.Sounds.cs`; what plays it is `Scripts/Effects/WeaponAudio.cs`.

## 1. Shots

Every weapon names what firing it sounds like in its own resource: `WeaponDefinition.Shot`, a
`ShotKind`, set in the weapon files under `Weapons/`.

| Weapon | `Shot` |
|---|---|
| pistol | `pistol` |
| rifle | `rifle` |
| DMR, the marksman | `dmr` |
| the heavy gunner's LMG | `machine_gun` |
| the technical's HMG, the 4x4's gun, the deployed heavy gun, the barracks turret | `heavy_machine_gun` |
| launcher | `launcher` |
| the tank's cannon | `cannon` |
| the barracks mortar, the carried mortar launcher | `mortar` |
| hammer, hand grenade, FPV drone, ram | none |

A test holds every weapon file to this table, and to the rule that a weapon is heard firing exactly
when its round is heard going by (§2).

## 2. Rounds going past

Every round names what it sounds like going past in its resource: `ProjectileDefinition.Flyby`, a
`FlybyKind`, set in the round files under `Weapons/projectiles/`.

| Round | `Flyby` | Heard within | Not before it has flown |
|---|---|---|---|
| `pistol_9mm`, `rifle_556`, `dmr_762` | `bullet` — a crack and a short whine | 4 m | 10 m |
| `hmg_127` | `heavy_bullet` — a heavier crack, a lower and longer whine | 6 m | 12 m |
| `grenade_40mm`, `cannon_76`, `mortar_81`, `mortar_81_launcher` | `shell` — a rush of air, no crack | 10 m | 15 m |
| `hand_grenade`, `fpv_drone` | none | | |

The reach and the distance are `Flyby.RadiusOf` and `Flyby.MinTravelOf`. A test fails when a round
file is added without a line in its table.

### 2.1 When a shot is heard

From events the simulation already has; nothing was added to any message.

| Peer | Whose shot | From | Played |
|---|---|---|---|
| any that plays sound | the local player's own | the tick their client predicts it (`PredictShot`, a mounted or seated gun's `FireMounted`) — on a listen host or offline, the authority's spawn | at the ear, flat (`AudioStreamPlayer`), 6 dB under the 3D volume (`WeaponSounds.OwnShotDb`) |
| a client | anybody else's | the server's spawn message (`ApplySpawn`) | in 3D at the muzzle |
| the authority (listen host, offline) | anybody else's | `SpawnProjectile` | in 3D at the muzzle |

A client hears its own shot when it predicts it, with the tracer, and not again when the server's
spawn arrives — adopted or not. Every own shot with a sound is predicted; a hand grenade, a drone and
a swing have none. Players, bots, the strategist's units, barracks defences and structures all fire
through the same spawn, so all of them are heard.

The fog: a client that plays the strategist is not sent a spawn outside its side's sight
([`NETCODE.md`](NETCODE.md) §6.2), so it does not hear it. A strategist on the authority is held to
the same rule (`Visibility.Covers`), as its explosions are ([`EFFECTS.md`](EFFECTS.md) §2.2).

### 2.2 When a round is heard going past

After every tick's projectiles have flown — on a client, after the server's hits have stopped
theirs; on the authority, after it has resolved them — each round's chord for the tick is tested
against the ear, the position of the camera being drawn from (`Flyby.Passes`):

- the closest point of the chord to the ear is within the kind's reach (§2);
- the round reaches that point on this chord: it was coming towards the ear when the chord began,
  and level with it or going away when it ended (`0 < t ≤ 1` along the chord). Chords meet end to
  end, so exactly one of them owns the moment of closest approach and a round is heard once, however
  many ticks it takes to pass;
- it has flown at least the kind's distance from the muzzle by then: nearer, the shot is what is
  heard, and a teammate firing past a shoulder is not a near miss;
- it is not the local player's own round, and it is still flying at the end of the chord — on the
  authority, a round that struck something on the chord has been stopped, so one that hit a wall
  short of the ear is not heard on the far side of it.

The flyby is played in 3D where the round came closest, so it is heard on the side it went past.
Each round is heard going past once (`RecentRounds`, the last 64 ids). Nothing is heard by a
strategist, whose camera is above the field rather than in it.

## 3. The placeholders

Until a weapon or a round has a recording (§4), it plays a placeholder made when it is first needed
by `WeaponSounds.Synthesize`: 16-bit mono PCM at 44.1 kHz, handed to an `AudioStreamWav` in memory —
no file, nothing to import. The same kind always makes the same samples.

| Part | What it is | Used by |
|---|---|---|
| crack | bright noise, gone in milliseconds | shots, bullets going past |
| boom | a low tone that starts sharp and falls to its pitch as it dies | shots |
| tail | duller noise that comes in a moment later and lingers: the space round the shooter | shots |
| whiz | band-limited noise that swells and fades | rounds going past |
| tone | a tone falling through the whiz: the Doppler shift of a round going away | rounds going past |

Every number is one `SoundVoice` in `WeaponSounds`, beside how far it carries:

| Kind | Length | Full volume within (`UnitSize`) | Heard to (`MaxDistance`) | Volume |
|---|---|---|---|---|
| `pistol` | 0.45 s | 12 m | 250 m | −4 dB |
| `rifle` | 0.7 s | 18 m | 400 m | −2 dB |
| `dmr` | 1.0 s | 24 m | 500 m | 0 dB |
| `machine_gun` | 0.6 s | 20 m | 400 m | −2 dB |
| `heavy_machine_gun` | 0.9 s | 28 m | 500 m | 0 dB |
| `launcher` | 0.5 s | 10 m | 200 m | −4 dB |
| `cannon` | 2.2 s | 40 m | 900 m | +3 dB |
| `mortar` | 1.2 s | 30 m | 600 m | 0 dB |
| flyby `bullet` | 0.35 s | 3 m | 40 m | −3 dB |
| flyby `heavy_bullet` | 0.5 s | 4 m | 60 m | 0 dB |
| flyby `shell` | 0.8 s | 6 m | 80 m | 0 dB |

Past `UnitSize`, Godot's inverse-distance attenuation and its distance low-pass take over. Each play's
pitch is moved at random by up to a few per cent (`SoundVoice.PitchJitter`), so a burst is not one
sample played over and over.

`WeaponAudio` keeps three pools: 32 3D players for everyone else's shots, 8 flat players for the
local player's own, and 16 3D players for flybys. Past a pool's size the oldest sound in it is cut
short. A player is taken back when it stops, or a tenth of a second after its stream should have
ended, whichever is first. Every sound goes to a bus named `Weapons` when the project has one, and
to `Master` until then.

## 4. Replacing them with recordings

Each weapon resource has a `ShotSound` and each round resource a `FlybySound` (both under *Sound* in
the inspector). Either takes any `AudioStream` — a WAV, an Ogg Vorbis, or an `AudioStreamRandomizer`
over several takes — and is played in place of the placeholder with no other change. Leave it empty
for the placeholder.

```
[ext_resource type="AudioStream" path="res://Audio/Weapons/rifle_shot.wav" id="3_shot"]

[resource]
...
Shot = 2
ShotSound = ExtResource("3_shot")
```

- Mono. A shot is played in 3D at the muzzle for everyone else and flat at the ear for whoever fired
  it; a flyby in 3D where the round came closest. A stereo file loses its direction in 3D.
- Start a shot on its transient and a flyby on its crack, with no silence before it: the sound is
  started on the tick the round was fired or came closest.
- How far a sound carries and how loud it is stay its kind's (§3), so a weapon given a recording
  keeps a `Shot` other than none, and a round a `Flyby` other than none — a weapon left at none plays
  nothing, recording or not. Tune the recording's own level against the placeholder it replaces.
- Weapons that share a placeholder can each have their own recording: the turret's and the 4x4's
  guns are both `heavy_machine_gun`, and need not sound alike.

## 5. Checking it

- `sound <weapon> [own]` on the `~` console plays a weapon's shot 20 m ahead of the camera, or at
  the ear with `own`; `sound flyby <bullet|heavy_bullet|shell>` plays a round going past 2 m to the
  camera's right. This peer only. A name of two words is typed whole or with an underscore
  (`sound mortar launcher`, `sound 4x4_gun own`).
- The `` ` `` debug HUD's `sounds` row: how many are playing, how many shots and flybys have been
  played, and how many were cut short (anything but 0 there means a pool is small for the fight).
- `dotnet test --filter WeaponSoundTests`: the placeholders' samples, when a round is heard going
  past, and the weapon and round files.
- `./scripts/weapon-audio-checks.sh` (a Godot 4.6 .NET binary on `PATH` or in `$GODOT`): a listen
  host, headless, its own player through the input map — 21 facts: their own rifle heard once a round,
  at the ear, as the rifle's placeholder, and never going past them; a round fired 60 m off heard at
  its muzzle, and going past 2 m off once, where it came closest, 11 ticks later; a rifle round 8 m
  off not heard going past and a tank's shell 8 m off heard; a round fired from 5 m behind past a
  shoulder not heard going past; a hand grenade not heard as a shot; every player taken back; and
  the console's `sound`. Headless audio mixes into nothing, so it says when and where sounds are
  played, not what they sound like.

TODO: listen on a display, as a client and on a listen host — every weapon fired by you and by
someone else near and far, a burst from the LMG and the HMG, rounds fired past you from beyond
10 m, a mortar coming down near you, and the placeholders' levels against each other. Built,
tested and checked headless; never heard.

## 6. Not done

- Sound travelling at the speed of sound: a shot is heard on the tick it was fired however far off.
- Occlusion: a shot behind a wall is as clear as one in the open, and a round's crack is heard on
  the far side of a thin wall it went past.
- On a client, a round is stopped by the server's hit message a link late; a round that struck a
  wall a metre or two short of the ear may be heard going past it first.
- A shell or a mortar round that lands short of the ear is never level with it, so it is not heard
  coming in: there is no incoming whistle.
- Explosions, impacts, ricochets, reloads, an empty magazine's click, swapping weapons, a hammer's
  swing, a grenade's throw, vehicles and footsteps.
- A distant-shot variant: far shots are the near one attenuated and low-passed.
- Demos do not replay projectiles ([`DEMOS.md`](DEMOS.md) §6), so a demo plays no shots.
