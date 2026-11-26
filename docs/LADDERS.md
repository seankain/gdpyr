# Ladders

Ladders a ground-force body climbs, and a test building on the greybox map to try them on: a
two-storey block with a scaffold along one face, four ladders, windows and a roof to fight from.
It is there to test whether height makes the levels better before other maps get any.

| Part | File |
|---|---|
| the rules: when a ladder is taken, how a body moves on it, when it lets go | `Scripts/Sim/Ladders.cs`, tested by `Tests/LadderTests.cs` |
| a ladder on a map | `Scripts/Fps/Ladder.cs` |
| the movement state | `Scripts/Fps/PlayerClimbingState.cs`, the last child of `PlayerStateMachine` in `Scenes/fps_controller.tscn` |
| the test building | `Scenes/Environment/ScaffoldBuilding.tscn`, instanced in `Scenes/Test.tscn` as `ScaffoldBuilding` |
| a ground bot on a ladder | `Scripts/Bots/BotPilot.cs` (`ClimbDirection`, `OffLadders`, `NewNavigationAgent`) |
| the engine check | `tools/EngineChecks/LadderChecks.cs`, run by `scripts/ladder-checks.sh` |

## 1. Climbing

- **Walk into a ladder to take it.** From the ground, or coming down from a jump or a fall, within
  0.9 m of its face and walking at it no more than 60° off square. Walking past one along a wall
  does not take it, and neither does a jump on its way up.
- **Walk towards it to climb, away from it to come down,** at 3 m/s times the kit's speed scale
  (`fps_controller.MoveSpeedScale`, so a heavy kit or a carried gun climbs slower). It is the
  walk that decides, not the look: facing the ladder, forward climbs and back comes down; backing
  down it from the top, forward comes down. Strafing does nothing: the body is held on the rungs'
  middle, 0.55 m off the face (the capsule's radius and 5 cm).
- **At the top, keep walking into it** and the body is lifted 10 cm over the landing and carried
  onto it, 0.65 m past the face, where it lets go.
- **From the top, walk off the landing towards the ladder** — within 0.6 m of its face — and it
  is taken from above: the body is drawn out over the edge and goes down.
- **At the foot,** standing on the ground and not climbing, it lets go.
- **Jump** to push off: 3.5 m/s out and 3 m/s up. With the walk still into the ladder, it is
  caught again on the way down.
- Crouching and sliding do not take a ladder. Weapons work as they do on the ground.

There is no fall damage (`docs/GROUND_VEHICLES.md`), so a missed rung is a drop, not a death.

## 2. The rules

`Scripts/Sim/Ladders.cs` is engine-free. A ladder is a `LadderSpan`: the middle of its face at its
foot, the face's level normal out towards the climber, its width, and its height to the landing.
The landing is on the far side of the face from the climber, so a body goes over the top the way it
faces. A point is measured off a ladder as a `LadderPlace`: `Out` from the face (negative is over
the landing), `Side` along the rungs, `Up` from the foot.

| Function | What it decides |
|---|---|
| `TryGrab` | which ladder, if any, the walk takes this tick; the nearer of two in reach |
| `Holds`, `Holding` | whether the body is still on one, found again every tick from the feet |
| `Climb` | the whole velocity on it: up or down by the walk's `Intent`, never above the landing's 10 cm, held on its line, and over the top |
| `LetsGoAtFoot` | the let-go at the bottom |
| `JumpOff` | the push off |
| `Steer`, `WithoutGrab` | a bot's: §5 |

A ladder is found from the feet's position by these functions, never from an `Area3D`. An area's
overlaps belong to the physics server and are brought up to date once a physics frame, and a
reconciliation replays many ticks in one frame (`docs/NETCODE.md` §3.2). The state holds nothing
of its own either: the ladder a climber is on is the one whose reach the feet are in. Restoring a
position restores everything a replayed tick needs, so a client's prediction climbs exactly as the
server does.

## 3. Putting a ladder on a map

Add a `Node3D` with `Scripts/Fps/Ladder.cs`. Stand it at the middle of the ladder's foot and turn
it, about the vertical only, so it looks the way its climber looks: at the wall, the scaffold or
the hatch edge it is fixed to. Its +Z is then the face's normal. Then set:

| Export | Default | |
|---|---|---|
| `Height` | 3.5 | from the foot to the landing; the landing must be beyond the face, at this height |
| `Width` | 0.5 | between the stiles |
| `StilesAboveMeters` | 1 | how far the drawn stiles stand above the landing |
| `Solid` | true | a 8 cm box behind the face, foot to landing, on the world layer: bodies, rounds and the navigation mesh go round the rungs. Off for a ladder fixed flat to a wall, which already does that |
| `LinkOutMeters` | 1.8 | where its navigation link starts, in front of the foot; it must be on walkable ground |
| `LinkOverMeters` | 1.6 | where the link ends, past the face on the landing |

What has to be true of the geometry:

- the landing is level with `Height` and reaches at least 0.7 m past the face, so the body is on it
  when it lets go;
- above the landing there is a gap at least 1.3 m wide in any rail or parapet, centred on the
  ladder, for the capsule to go through;
- in front of the face there is room for the capsule, 1.05 m deep and 1 m wide, all the way up;
- a ladder through a hatch stands at the hatch's edge, the climber under the hatch and the floor
  on the other side of the face.

The node adds the rest itself: its span to `Ladder.All`, the box when `Solid`, the navigation link,
and the drawn stiles and rungs (not on a dedicated server). Nothing is baked into the scene, and
the span is taken once, when the node enters the tree: a ladder does not move.

## 4. The movement state

`PlayerClimbingState` is the eighth state of the FSM, after falling. The FSM's index of a state is
what a snapshot, a demo record and the agent observation carry, so the seven before it keep the
indices they had (`Tests/LadderSceneTests.cs`).

### 4.1 Taking a ladder

Idle, walking and sprinting ask `fps_controller.TryGrabLadder` after they move and before their
own transitions; jumping asks it too, which only takes a ladder on the way down, and so does
falling. Crouching and sliding do not. True hands the body to the climbing state.

### 4.2 On the ladder

Each tick the state finds the ladder from the feet (`TryHoldLadder`). Off every ladder — over the
landing, or off an end of the rungs — it runs the ground's tick and hands over to idle. A jump
press pushes off into falling. Otherwise `Ladders.Climb` sets the velocity, with no gravity, and
`MoveAndSlide` moves the body; standing on the ground at the foot and not climbing hands over to
idle.

A remote body on a ladder is drawn standing: the humanoid rig has no climbing clip yet
(`Scripts/Characters/`), and `fps_controller.ReadPose` gives it neither a crouch nor the air.

## 5. Ground bots

Each ladder's `NavigationLink3D` joins in front of its foot to over its landing, both ways, on
navigation layer 2 (`Ladder.NavigationLayer`). A ground bot's agent paths on layers 1 and 2
(`BotPilot.NewNavigationAgent`); a unit's agent stays on layer 1, so a unit never paths up a
ladder it cannot climb and is sent to the nearest ground instead.

A bot climbs with the same input a person does — the walk — and its pilot steers it so:

- **on a ladder**, it walks into the ladder (up) when the next corner of its path is nearer the
  landing's height than the foot's, and away from it (down) otherwise (`Ladders.Steer`). By height
  rather than by the level line to the corner, which swings round as the bot passes under or over
  it. On a ladder it does not strafe, whatever it is fighting, and with nowhere to be it comes
  down;
- **off a ladder**, when its next corner is within a metre of its feet's height — the path does not
  go up or down a ladder here — the part of its walk that would take a ladder is taken out
  (`Ladders.WithoutGrab`), so a bot passing a ladder's foot or walking along its landing does not
  catch on it.

The stuck check measures in three dimensions, so a bot going up a ladder is not taken for stuck.

## 6. The agent observation

`gdpyr-agent-obs-4` carries the movement state as a seven-wide one-hot
(`AgentObservation.MovementStates`). Climbing is index 7, outside it: a climbing body's one-hot is
all zeros, and its position and velocity say it is going up. Widening the one-hot changes the
vector's length, so it is a schema change of its own, left for when a policy needs to know.

## 7. The test building

`Scenes/Environment/ScaffoldBuilding.tscn`, at (−40, 0.5, 100) in the greybox, about 60 m east of
the ground force's spawn, turned so its scaffold faces north, towards the field. In its own frame
(+Z is the scaffold's side):

| Level | Height | Reached by |
|---|---|---|
| ground floor | 0 | a door in its −X end wall, which faces east on the map |
| first lift (the scaffold's lower deck) | 3.5 m | `LadderScaffold`, up the scaffold's outer face through a gap in its rail; a door into the first floor |
| first floor | 3.5 m | `LadderHatch`, inside, up through a hatch in the corner; the door from the first lift |
| roof | 7 m, a 1 m parapet | `LadderEndWall`, 7 m up the +X end wall (facing the spawn, west on the map) through a gap in the parapet; `LadderToRoof`, from the first lift where the top lift stops short, leaning on the wall |
| top lift (the scaffold's upper deck) | 7 m | a gap in the roof's parapet |

Each kind of ladder is there once: open-backed on a scaffold, through a hatch, fixed to a wall,
and from one floor to the next leaning on a wall. Every level joins every other, by more than one
way for most. The walls have windows on both floors, and the lifts have guard rails at 0.5 and
1 m.

The geometry is two CSG combiners in group `navmesh`, the building and the scaffold, with
collision on; the navigation mesh is baked from them as from the rest of the map.

## 8. Checks

`dotnet test` covers the rules (`Tests/LadderTests.cs`) and the scenes (`Tests/LadderSceneTests.cs`).
`scripts/ladder-checks.sh` runs the game:

```bash
./scripts/ladder-checks.sh
```

```bash
SHOTS=/some/dir ./scripts/ladder-checks.sh listen
```

- **listen** — a listen host on the greybox. The four ladders are where the scene puts them; each
  is walked into, climbed, gone over onto its landing, taken from the landing and come down to its
  foot; a jump pushes off the wall ladder, and with the walk still into it catches it again;
  walking past a ladder does not take it, and strafing does not move a body off one. A ground
  bot's agent finds a path from the ground to every level and a unit's to none. The player, steered
  by the bot's own ladder steering along a bot agent's path, goes from the ground to the roof, down
  to the first floor, and out onto the ground beyond the scaffold.
- **net** — a dedicated server and a client. The client climbs the 7 m wall ladder, goes over onto
  the roof, takes it again and comes down, predicted; the server has the body where the client does
  at the top and at the foot, and the mispredictions are counted.

With `SHOTS`, the listen part runs on a display and saves views of the building and of each kind of
ladder first.
