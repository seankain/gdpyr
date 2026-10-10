# Cover

Status: **built.** The map has cover on it: 18 low walls and 8 blocks across the ground between
the spawn and the barracks. Under `--bot-ai htn` (the default) the ground bots fight from behind
low walls. The RTS units do too, defending or attacking, and their builders hide behind cover when
shot at. The computer strategist's builder lays sandbag walls in front of the squads it keeps
holding ground. Legacy bots (`--bot-ai legacy`, the RL baseline) are unchanged apart from the
level geometry they now walk round.

Scope: what makes a place cover, how each AI finds and uses it, the level, and what was measured.
Nothing new goes on the wire: the boxes are scene geometry every peer loads, the sandbag walls were
already replicated, and a bot crouching is an ordinary `InputFrame`.

---

## 1. Why

In bot-only rounds both sides settle into one picture ([`HTN_BOTS.md`](HTN_BOTS.md) §6, H6). The
strategist's units rally round the barracks, and the ground bots wait at the edge of its defended
ring. The map in between was a flat floor with nothing to stand behind, so every fight was decided
by who saw whom first in the open. A level with things in it gives both sides positions worth
holding and moving between. The AI only uses those positions if it knows where they are.

---

## 2. What cover is

### 2.1 Physical: it already was

A box on the world layer stops rounds, bodies and sight lines for everybody. Bullets are resolved
against world geometry by a physics ray, line of sight is a physics ray, and the navigation mesh is
baked from world colliders ([`NETCODE.md`](NETCODE.md) §10.5). A crouched player behind a
sandbag wall was already hidden from a rifleman in front of it (`sandbag_cover.json`). Nothing in
the damage model changes.

### 2.2 For the planners: the cover board

`CoverBoard` (`Scripts/Sim/Cover.cs`, engine-free, fixed-size, allocation-free) holds every
cover box on the field as an oriented box:

- **The map's boxes**: every `CSGBox3D` in the `cover` group, read once when the map is up
  (`UnitManager.CollectCover`), by its centre, size and yaw. A box lower than 0.9 m hides nothing
  worth planning for and is not taken. 96 fit (`LevelCapacity`).
- **Finished sandbag walls**, either side's, mirrored from the structure slots once a tick
  (`UnitManager.SyncStructureCover`). A site still going up, rubble, and a pillbox or tower,
  which shoot whoever stands behind them, are not cover.

A **spot** is a place to stand behind one box against one threat. `TryFindSpot` walks the faces
of each box within reach that are turned away from the threat. On each face it tries slots 1.3 m
apart, a body's radius plus 0.2 m off the face. It keeps a slot when all of these hold:

| Test | |
|---|---|
| It is in reach | within `SearchMeters` of the seeker, measured flat |
| It is in the fight | between `MinThreatMeters` and `MaxThreatMeters` from the threat |
| It hides the body | the box stands across the segment from the threat's eye to the seeker's chest (`ProtectMeters` above its feet) |
| It can be fired from | for a spot to fight from: the box is at least 0.15 m below the seeker's eye, and the segment from the eye to the threat's chest clears its top by that much |
| It is free | no other box's footprint holds the body, and nobody has claimed a spot within 1.1 m |

Best is the shortest walk, with a metre of falling back from the threat charged at half a metre
(`RetreatWeight`). Ties go to the lower box and the earlier slot, so a query has one answer.
`FindSpots` takes the best spots one after another, which is how a place is asked how many bodies
it can hide: a 4 m wall hides three from one threat. `Protects` asks whether a box beside a body
still hides it, which is what keeps a spot. It measures the body up from the ground the box
stands on, not from the height of the point it is given. A spot snapped to the navigation mesh
comes back 0.2 m above the floor, which put a standing chest level with a 1.2 m wall's top.
Before this was found, every spot a ground bot snapped failed the test (§9).

One fact, `CoverState`, carries it into both domains: **None** (no spot in reach), **Near** (a spot
it is not standing in), **In** (within 1.0 m of its spot, held to 1.6 m, and the box still hides it).

### 2.3 Low walls and blocks

The threat is a standing body: eye at 1.5 m, chest at 1.0 m (`UnitManager.CoverThreat*Meters`).
Against that, a box is one of two things:

| | Low wall (map: 1.2 m; sandbags: 1.1 m) | Block (map: 2.8–3.0 m) |
|---|---|---|
| Standing behind it | the chest is hidden, the head is not; a rifleman's eye (1.5 m) fires over it | everything is hidden; nothing fires over it |
| Crouched behind it | hidden completely | hidden completely |
| Used by the AI as | a fighting position | a place for a builder under fire to hide |

---

## 3. Exposure: aiming at what shows

Without one more rule, low cover would be absolute against the bots. A ground bot's sensor drew its
line to a unit's middle, and a unit's rounds went at a player's chest. A body behind a low wall was
therefore invisible to the one and soaked every round of the other into the wall. Under `htn`:

- **A ground bot sees a unit by its head** when the line to its middle is blocked and the head
  shows (`GroundSensor.Scan(exposedHeads: true)`). It **aims at the head** while that holds
  (`BotPilot.TryResolveTarget`). This costs one extra ray, and only while the middle is hidden.
- **A unit aims at a player's head** when its target's chest is hidden and the head shows
  (`Unit.AimsHigh`, decided on the unit's scan; `UnitManager.AimsHigh`). That is two more rays
  per scan for a planning unit.

A head **shows** when a point 0.2 m below the eye can be seen (`Exposure.Below`), not just the eye.
A crouched player's eye is about 1.05 m, a few centimetres under a sandbag wall's 1.1 m top. A
line that grazes the wall reaches only the crown, and aiming at the eye along it sent some rounds
over the wall into the head. `sandbag_cover` caught that: the crouched player took a hit in one
seed. With the margin, a standing head (eye 1.5 m) behind a 1.1–1.2 m wall shows and a crouched
one does not.

The shooter's error cone does the rest: at 30 m a rifleman's 2.5° is about 1.3 m, so most rounds
aimed at a head over a wall hit the wall or go over. Cover reduces exposure; it does not make a
body invisible. The legacy bots and an agent's observation (`AgentGroundView`) keep the old rule,
so neither the RL baseline nor a policy's inputs change.

The strategist's fog (`VisibilityService`) still tests the chest, as it always has: a standing
player behind a low wall is hidden from the strategist's map even while a unit is firing at their
head. That is the anti-cheat rule of [`NETCODE.md`](NETCODE.md) §6.2, left as it was.

---

## 4. The level

`Scenes/Test.tscn` has 26 cover boxes under the `CSGCombiner3D`, so they are world-layer collision
and baked into the navigation mesh (150 vertices before, 492 after). They are in the `cover`
group, which is how the board finds them; a map marks its cover the same way.

| | Count | Size | Where |
|---|---|---|---|
| Low walls (`CoverLow01`–`18`) | 18 | 4–5 m × 1.2 m × 0.8 m | round the three nodes, along the arc where the ground bots wait outside the barracks' ring, across the middle the strategist's squads come through, and two near the ground spawn |
| Blocks (`CoverBlock01`–`08`) | 8 | 2.5–4 m across, 2.8–3.0 m tall | between the nodes and inside the ring, breaking the long sight lines |

They are kept out of the lanes the scenarios shoot along: x = −90, −60, −30, −10, 0, 8 and 30
over the ranges those scenarios use, the range markers on x = 0, the spawn area, the barracks'
door and gun lines, and the technical's route in `htn_recon_finds_contact`.

They are also kept off **the edge of the barracks' ring**. A ground bot never walks into the ring:
`OutsideDefences` sends one that strays inside straight back out. The barracks' defences reach
about 107 m, so a bot is turned back inside about 115 m. A wall on that edge, across a route that
hugs it, has a navigation detour that dips inside the ring at one end. The bot walks the detour,
is sent back out, and walks it again for the rest of the round. The first layout had a wall at
115 m, across the route from the middle node to the far one, and `htn_ground_denies_node` caught
it: the far node's denier oscillated against it for a minute (§9). So nothing stands between
105 m and 122 m of the barracks. The three walls on the arc where the bots wait stand at 123 m,
each lying across the line to the barracks, so its far side is cover from units coming out of it.

---

## 5. Ground bots

`GroundFact.Cover` is sensed on the bot's 10-tick scan (`BotPilot.SenseCover`). The threat is the
unit it is shooting at when bullets hurt it, else the nearest such unit its scan found. The search
uses a standing body's numbers, within 25 m of the bot and 8–55 m from the threat
(`GroundPlanTraits.Cover*Meters`). A spot it already has is kept while it is in reach, in the
fight's range and its wall still hides it. A remembered fight (a ghost) keeps it too, and no fight
lets it go. Bots share their spots through `BotDirector.GroundCover`, so two never take the same
one. A spot inside an enemy barracks' ring is refused, since `OutsideDefences` would never let the
bot walk to it. Whether it has reached its spot is re-banded every tick.

In the domain ([`HTN_BOTS.md`](HTN_BOTS.md) §5.1), Attack gains a method above "press with allies"
and "engage":

```
attack                         [Contact≠none]
├─ engage armour               …
├─ fight from cover            [ThreatKind=infantry ∧ Contact=visible ∧ Cover≠none]
│                                TakeCover (→ Cover=in) {Cover≠none, Contact≠none, infantry}
│                                → HoldCover [Cover=in] {Cover=in, Contact≠none, infantry}
├─ press with allies           …
├─ engage                      …
└─ investigate ghost           …
```

`TakeCover` runs to the spot, firing on the move. `HoldCover` stands in it, so the brain does not
strafe out from behind the wall. A bot shoved off its spot steps back in. When the magazine runs
low and the fight is out of sight, it reloads there and crouches below the wall while it does
(`BotSituation.InCover`, `BotBrain`). Crouched, the fight goes out of sight; the team still
remembers it, and `HoldCover` holds through a ghost. A spot coming into reach pre-empts a fight in
the open (P2). Retreat still pre-empts holding cover, and a ghost alone or armour is never
something to take cover from.

---

## 6. Units

`UnitFact.Cover` is sensed on the unit's scan (`UnitManager.SenseCover`) and re-banded every tick.
What it is about depends on the order:

| Order | Cover from | Spot |
|---|---|---|
| Build | the nearest contact the side remembers within the unit's sensor | anything that hides it, low or high, within 15 m |
| Attack, Defend, none, with a target | its target's feet | a low wall it can fire over, within 10 m, 6 m to its engage range from the target, inside its leash when it has one |
| Defend, nothing to shoot | the ring's threat, as its posts face it: the nearest remembered contact within 80 m of the anchor, else the ground spawns | a low wall across its ring, nearest its own post first, no more than 2.6 m outside the ring (as far as a wall laid for the ring puts it), so a squad holding a node stays inside the capture radius; it becomes the unit's post |

Units share their spots through `Unit.CoverSpot`, and keep one while its box still hides them
from the threat. In the domain ([`HTN_BOTS.md`](HTN_BOTS.md) §5.2):

```
build                          [Order=Build]
├─ hide behind cover           [UnderFire ∧ Cover≠none]  TakeCover → the spot {UnderFire, Cover≠none}
├─ take cover                  [UnderFire ∧ FriendNear]  TakeCover → the nearest friend (as before)
└─ work
attack                         [Order=Attack]
├─ wait for squad
├─ engage from cover           [Contact=own ∧ Cover≠none]  HoldCover {Contact=own, Cover≠none, Order}
├─ engage focus                …
defend zone                    [Order∈{Defend,None}]
├─ engage from cover           [Contact=own ∧ InLeash ∧ Cover≠none]  HoldCover {…, InLeash}
├─ engage in leash             …
└─ hold post                   the post, now behind a wall when there is one
```

`HoldCover` walks to the spot firing (it does not stop to fight on the way) and stands to fight once
in it. A move order and a patrol are never second-guessed for cover. Supply trucks take no part.

---

## 7. The computer strategist digs in

Under `htn` the commander's builder lays sandbags for the squads it keeps holding ground
(`BotStrategist.DigInSquads`, the rule in `DigIn`, `Scripts/Sim/StrategistBrain.cs`). Those squads
are every squad reinforcing a zone, and the garrison once the side has a pillbox. A barracks has
guns and a mortar of its own, and a node has nothing until a pillbox goes up. The builder's first
trip to a node is also often what takes it: `htn_strategist_defends_node` lost a node in one seed
when the garrison went first. For each squad, once a decision, the builder does the following:

1. It skips the zone if the side remembers an enemy within 35 m: that is a fight, not a building
   site. A second squad within 16 m of one already counted is skipped too.
2. It faces the ring's threat as the units' posts do. It counts the spots behind cover from that
   threat across the ring (`CoverBoard.FindSpots`), and walls still going up count three each.
3. It wants a wall for every three defenders without a spot, at most two
   (`DigIn.WallsWanted`).
4. The first wall goes on the ring's rounded bearing, 1.6 m outside the ring, front to the threat.
   The second goes beside it, 0.2 m away, so two make one 8.2 m wall (`DigIn.Layout`). A
   placement the server refuses is left for 30 s.

It runs before the node fortification, after a site nobody is building. A wall is 25 points and
five seconds of one builder. If the points are not there, it saves them as `Fortify` does. A
sandbag site left unfinished is not sent another builder, as other sites are. One is left
unfinished when its builder was killed on the way, usually into the fight it was for. In one
bot-only round, before this rule, four more builders (240 points) died one after another walking
the same way to finish a 25-point wall. The
units on that ring move their posts behind the wall (§6), and `DigInTests` checks the geometry
end to end: two walls laid by the rule give a four-man ring four spots against its threat.

At the barracks on the test map the garrison's anchor is the rally point (100, −86), facing the
spawns on the 315° bearing. The walls go up at about (96, −82), clear of the door's 10 m.

Legacy's strategist does not dig in (D5).

---

## 8. Numbers

| Knob | Value | Where |
|---|---|---|
| A spot's slots along a face | 1.3 m apart, at most 8 | `CoverBoard.SlotSpacingMeters`, `MaxSlotsPerFace` |
| Off the face | body radius + 0.2 m | `CoverBoard.GapMeters` |
| Claimed | within 1.1 m | `CoverBoard.ClaimMeters` |
| Fire clearance over a wall | 0.15 m | `CoverBoard.FireClearanceMeters` |
| In a spot | 1.0 m, held to 1.6 m | `CoverBands` |
| Ground bot's search | 25 m; 8–55 m from the threat | `GroundPlanTraits` |
| Unit's search in a fight | 10 m; 6 m to its engage range | `UnitManager.UnitCoverSearchMeters`, `CoverMinThreatMeters` |
| Builder's search | 15 m | `UnitManager.BuilderCoverSearchMeters` |
| Defended ring's search | across the ring + 5 m, at most 2.6 m outside the ring | `UnitManager.PostCoverSearchMeters`, `PostCoverSlackMeters` |
| Dig-in | a wall per 3 defenders short, ≤ 2, 1.6 m outside the ring, not within 35 m of an enemy; the garrison after the first pillbox | `DigIn`, `BotStrategist.DigInSquads` |

What it costs: the board answers from boxes, so a spot costs no ray. Exposure costs one ray more
per bot per tick while its target's middle is hidden, and one per unit scan. Both are on scans the
game already makes.

---

## 9. Verified

- **`dotnet test`**: 1444 passed, 0 failed (1382 before). `CoverTests` covers the board, spots,
  claims, firing over, blocks, a turned wall, determinism, the bands and zero allocation.
  `DigInTests` covers the rule and a laid ring end to end. `GroundHtnTests`, `UnitHtnTests` and
  `BotBrainTests` cover every new row, the pre-emptions, and every new operator ending when its
  premise goes. The debugger's schema and family tables carry the new fact and goals.
- **`dotnet build Gdpyr.csproj`**: 0 warnings, 0 errors.
- **`htn_strategist_digs_in.json`** (new): passes under `htn` on both seeds, two walls finished at
  the barracks each round; fails under `--bot-ai legacy` (no wall near the barracks).

---

## 10. Not done

- **The mortar ignores cover**, by design: it needs no line of sight
  ([`NETCODE.md`](NETCODE.md) §10.4). Cover does not open the barracks' ring, and the ground bots
  still wait outside it (`OutsideDefences`).
- **No peeking round a block.** A block is somewhere to hide, not to fight from.
- **The fog still tests the chest** (§3).
- **A structure on the edge of the barracks' ring can still trap a bot's path** (§4). That is a
  weakness of `OutsideDefences`, not of cover, and `Fortify`'s structures at the middle node,
  which is 119 m from the barracks, could always do it. The map's cover is kept off that edge,
  but nothing stops the strategist building there.
