# Maps

Status: **built.** There are two kinds of map. A **scene map** inherits `Scenes/Maps/MapBase.tscn`,
which holds everything a round needs, and shows up in the host panel's **Map** picker once it has an
entry in `Scripts/Core/MapCatalog.cs`; `Tests/MapSceneTests.cs` checks the requirements below against
every catalog scene and against the template, and needs no Godot install. A **data map** is a `.map`
file made in the game's map editor, which one scene, `Scenes/Maps/MapHost.tscn`, builds the world
from; it needs no entry and no checkout to play, and a client that lacks it fetches it from its
server (§6, [`LEVEL_EDITOR.md`](LEVEL_EDITOR.md)).

Scope: how a map reaches the map select, what the template holds, what the engine looks for in a map
scene, how to check a new one, and data maps (§6). Hosting and the wire are in [`LAN.md`](LAN.md)
§3.4. Cover is in [`COVER.md`](COVER.md). Nodes and perks are in
[`CONTROL_POINTS.md`](CONTROL_POINTS.md).

Nothing in §1–§5 changes for scene maps. A data map's world has every node §3 names, built from the
file by `LevelBuilder` ([`LEVEL_EDITOR.md`](LEVEL_EDITOR.md) §7.1), and the validator
(`MapValidator`) asks of a file what `MapSceneTests` asks of a scene.

---

## 1. Getting into the map select

The host panel (**Host…** in the main menu) lists `MapCatalog.All` in order. A map is one entry there:

```csharp
new("dunes", "Dunes", "res://Scenes/Maps/Dunes.tscn",
	"two nodes in a valley, the barracks on the ridge"),
```

| Field | Rule |
|---|---|
| `Id` | What the server sends to every client (`NetworkManager.ServerMap`), and how the client finds a scene map in its own catalog. Matched exactly and case-sensitive. **Never change an id once a build that has it is out.** It must be unique (`MapCatalogTests.IdsAreUnique`). A data map is found by its hash, not its id (§6). |
| `Name` | The picker's text. |
| `ScenePath` | `res://…/*.tscn`. The file must exist (`MapCatalogTests.TheScenesAreInTheCheckout`) and must meet §3 (`MapSceneTests`). |
| `Description` | One line under the picker. |

- **Nothing scans a folder.** A scene in `Scenes/Maps/` with no catalog entry is not a map.
- **The first entry is the default** (`MapCatalog.Default`). `--server`, `--listen` and demo playback
  load it; `--map <id>` names another for `--server`, `--listen` and `--practice`
  ([`LEVEL_EDITOR.md`](LEVEL_EDITOR.md) §9.5). Keep the greybox first
  (`MapCatalogTests.TheDefaultIsTheGreybox`) and add new maps after it.
- **The host and every client need a scene map's entry.** A client whose build lacks the host's map
  id refuses it and disconnects instead of guessing. Both machines must run the same commit
  ([`LAN.md`](LAN.md) §3.4, §5). A data map is not held to this: a client that lacks it fetches it
  (§6).
- **After the catalog, the picker lists one's own data maps** — every `user://maps/<name>.map` that
  reads and that the validator finds no error in, as `Name — name, yours` — asked again each time the
  panel opens.
- The template itself has no entry and must not get one (`MapSceneTests.TheTemplateIsNotAMapOfItsOwn`).

---

## 2. The template: `Scenes/Maps/MapBase.tscn`

### 2.1 Making a map from it

In the editor: **Scene → New Inherited Scene…**, pick `Scenes/Maps/MapBase.tscn`, rename the root,
and save it as `Scenes/Maps/<Name>.tscn`. Then add the catalog entry (§1).

An inherited scene can **move and re-tune** the template's nodes and **add children** under them. It
**cannot delete** them. So the template holds only what every map needs, and everything optional
goes in the map (§3.2).

In text form, an inherited map that moves the template's node and adds a second node and a cover
wall looks like this:

```
[gd_scene format=3]

[ext_resource type="PackedScene" uid="uid://dol2o523xdnox" path="res://Scenes/Maps/MapBase.tscn" id="1_base"]
[ext_resource type="Script" uid="uid://cxvwabhyxemfo" path="res://Scripts/Rts/ResourceNode.cs" id="2_node"]

[node name="Dunes" instance=ExtResource("1_base")]

[node name="ResourceNode1" parent="."]
transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, -10, 0, -10)

[node name="ResourceNode2" type="Node3D" parent="." groups=["resource_node"]]
transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 30, 0, 40)
script = ExtResource("2_node")

[node name="CoverLow01" type="CSGBox3D" parent="Geometry/Cover" groups=["cover"]]
transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, -20, 0.6, 20)
use_collision = true
size = Vector3(4, 1.2, 0.8)
```

### 2.2 What it holds

| Node | Type / script | Why every map needs it |
|---|---|---|
| `MapBase` (root) | `Node3D`, `WorldRoot.cs` | Starts the round (§3.1, item 1). |
| `Sun`, `WorldEnvironment` | `DirectionalLight3D`, `WorldEnvironment` | A lit sky. Re-tune per map. |
| `Geometry` | `Node3D`, group `navmesh` | The navigation bake reads everything under it (§3.1, item 3). Put the map's geometry here. |
| `Geometry/Ground` | `StaticBody3D`, 240 × 1 × 240 m box | Its top face is y = 0. |
| `Geometry/Perimeter` | `StaticBody3D`, four 4 m walls | Centred on ±120 m, so the inner faces are at ±119.5 m: the field the code assumes (§3.1, item 7). |
| `Geometry/Cover` | `Node3D`, empty | Where cover boxes go (§3.2). |
| `NavigationRegion3D` | group `navigation_region`, empty `NavigationMesh` | Baked on the server at runtime (§3.1, item 3). |
| `PlayerSpawns/Spawn1`–`Spawn4` | `Marker3D`, group `player_spawn` | Where bodies appear: 1 m above the ground, facing −Z (§3.1, item 2). |
| `WeaponLocker1` | `WeaponLocker.cs` | Kits and resupply for the ground force, 6 m behind the spawn line (§3.1, item 6). |
| `Barracks1` | instance of `Scenes/Environment/Barracks.tscn` | Where the strategist's units come from (§3.1, item 4). |
| `ResourceNode1` | `ResourceNode.cs` | The point both sides fight for (§3.1, item 5). |
| `StrategistAnchor` | `Marker3D`, group `strategist_anchor` | Where the strategist's camera starts (§3.2). |

`Scenes/Environment/Barracks.tscn` is the greybox's barracks as its own scene. It has the `Barracks.cs`
root, the `SpawnPoint` door 6 m out along −Z, an 8 × 6 × 8 m building in group `navmesh`, two guns and a
roof mortar (`DefenseMount`, group `barracks_defense`). The numbers are copied from `Scenes/Test.tscn`.
Instance it again for a second barracks.

### 2.3 The layout, and why it is diagonal

| | Position (x, z) | Facing |
|---|---|---|
| Ground force spawn line | (−90, 90), four markers 4 m apart | north (−Z) |
| Weapon locker | (−90, 96) | its front faces north, toward the spawn line |
| Barracks | (90, −90) | door to the south (+Z); the door is at (90, −84) |
| Resource node | (0, 0) | — |
| Strategist anchor | (45, −45) | — |

A barracks' guns default to 100 m (`DefenseMount.RangeMeters`) and stand 7.1 m off its centre. That
makes its defended radius about 107 m (`Barracks.DefendedRadiusMeters`). A straight north–south layout
in a 240 m field would put the middle of the map under the guns. On the diagonal the node is 127 m from
the barracks and the spawn line is about 254 m from it, so both are outside the ring. The greybox does
the same thing. Move things however the map needs, but keep the nodes and the ground force spawn
outside every barracks' ring. A ground bot's goal is pulled back to the edge of an enemy ring
(`BotPilot.OutsideDefences`), so bots never take a node inside one.

### 2.4 Gully, a map that does not inherit the template

`Scenes/Maps/Gully.tscn` (catalog id `gully`) is a scene of its own with `WorldRoot.cs` on its root,
like the greybox. Its ground is a sculpted terrain, `Scenes/Environment/Gully.tscn` instanced at
scale 100: 200 × 200 m, from 0 m in the gully to 70 m on two flat plateaus, west and east, with the
gully running north–south between them. The template's flat ground and 4 m walls would sit under and
inside it, and an inherited scene cannot delete them, so Gully keeps only what §3.1 asks for.

| | Position (x, y, z) | Why |
|---|---|---|
| `Geometry/Terrain` | the origin, scale 100 | under `Geometry`, so the bake reads its trimesh collider |
| `Geometry/Bridge` | (−2, 70.15, 30) | `Models/Environment/GullyBridge.glb`: the only walkable way between the plateaus |
| `Geometry/Perimeter` | four 0.5 m colliders, inner faces at ±100 m, y −10…110 | there is no ground past ±100 m and no kill plane for a body; 0.5 m so the bake's 0.4 m radius leaves no walkable strip on their tops; up to 110 m so an aircraft at `CrewField`'s 100 m ceiling stays in |
| Ground force spawn line | (−80, 71, 66.6), four markers 4 m apart | west plateau, 1 m above it |
| Weapon locker | (−80, 70, 72.6) | 6 m behind the spawn line |
| Barracks | (82, 70, −80), door south at (82, 70, −74) | east plateau, north-east corner |
| Resource node | (65, 70, 45) | east plateau, at the bridge's east end: 126 m from the barracks, outside its 107 m ring and the bots' 117 m standoff |
| Strategist anchor | (65, 70, −20) | east plateau, between the barracks and the node |

Measured on its navigation mesh: spawn line to node 169 m, over the bridge; barracks door to node
120 m. The gully floor is an island of its own: its walls are steeper than the 45° a body and the
bake walk, so nothing paths into it and a player who drops in cannot climb out.

The terrain mesh `Models/Environment/gully_Landscape.res` carries no material; the scene sets one
from `GullyBaked.png`, `GullyNormals.png` and `GullyRoughness.png`.

---

## 3. What the engine looks for

The simulation runs in autoloads. They find the map's pieces **by group or by script, once, on the
first server tick**. Node names and where a node sits in the tree do not matter, with three exceptions:
the barracks' `SpawnPoint` child, the kiosk's pad children, and the name-sorted orders noted below.
A list sorted by name is an index on the wire, so every peer derives it the same way from the same scene.

### 3.1 Required

`MapSceneTests` checks every item here except the details marked *(not tested)*.

| # | Element | Found by | Rules | If it is missing |
|---|---|---|---|---|
| 1 | **World root** | the root node's script `Scripts/Core/WorldRoot.cs` | Must be on the scene's root. It flips `Session` to in-game when the map enters the tree. | Nothing ticks: no spawns, no round. |
| 2 | **Player spawns** | nodes in group `player_spawn` (any `Node3D`; use `Marker3D`) | Sorted by name. A body is placed at the marker's origin, which is its feet, facing the marker's −Z. Players past the last marker are placed in slots beside the markers (`SpawnLap`). Strategists get a body here too. 4 is enough for 16 players. | A warning, then a fallback ring 4 m around the origin, 2 m up. |
| 3 | **Navigation** | one `NavigationRegion3D` in group `navigation_region` with a `NavigationMesh` resource; the source is everything in or under a node in group `navmesh` | The mesh is baked on the server on the first tick (`UnitManager.BakeNavigation`). The bake settings live in code, not in the resource: cell 0.2 / 0.1 m, agent radius 0.4 m, height 1.8 m, climb 0.5 m, slope 45°. **Only static colliders on layer 1 (World) are read.** Meshes are ignored, so a wall with no collider is invisible to the bake. A CSG node counts if `use_collision` is on (or its `CSGCombiner3D` has it on). | A warning, and units steer straight at their destination through walls. A bake with no geometry logs `navigation bake produced no geometry`. |
| 4 | **Barracks** | nodes with `Scripts/Rts/Barracks.cs` (they add themselves to group `barracks`) | **1 to 4** (`SimConfig.MaxBarracks`); any beyond 4 are dropped with a warning. Sorted by name; the sorted position is the barracks' index on the wire. Each needs a `Marker3D` child named **`SpawnPoint`**: units appear there and walk out along its −Z. The building's collider must be in `navmesh` so paths go round it. `DefenseMount` children are optional, and their reach sets the defended ring (§2.3). | Count 0: a warning, and the strategist cannot build. No `SpawnPoint`: units appear at the barracks' origin, inside the building *(the count is tested; that placement is not)*. |
| 5 | **Resource nodes** | nodes with `Scripts/Rts/ResourceNode.cs` (group `resource_node`) | **1 to 8** (`SimConfig.MaxResourceNodes`); any beyond 8 are dropped with a warning. Sorted by name; the sorted position is the node's index on the wire. Put the origin on the ground. A body counts as on the pad within `CaptureRadiusMeters` measured flat and within 4 m vertically (`CapturePad.VerticalToleranceMeters`). Exports: `CaptureRadiusMeters` 10, `CaptureSeconds` 8, `IncomePoints` 25, `IncomeIntervalSeconds` 5, `Perk` `None` or `Drone` ([`CONTROL_POINTS.md`](CONTROL_POINTS.md) §4). | No capture objective. The strategist lives on baseline income, and ground bots have nothing to take. |
| 6 | **Weapon locker** | nodes with `Scripts/Fps/WeaponLocker.cs` (group `weapon_locker`) | At the ground force's spawn. **Upright and unscaled**: a tilted or scaled locker is refused with an error *(not tested)*. Its front is +Z. It has no collider by design ([`LOADOUT.md`](LOADOUT.md)). | No changing kit mid-life and no resupply. Magazines run dry, and the only way to refill is to die. |
| 7 | **The field** | positions | Gameplay happens inside **±115 m on X and Z** (`Cargo.FieldHalfExtentMeters`). Vehicle exit slots, landing spots, aircraft destinations and drones are kept inside that square whatever the map is. The test checks spawns, the anchor, barracks and their doors, nodes and lockers. Every coordinate must also fit in the wire's ±327.67 m (`SimConfig.MaxPositionMeters`); structures need 5 m inside that, and agent observations scale positions by 256 m *(not tested)*. | Things inside the map are clamped to a square that does not match it. |

### 3.2 Recommended and optional

The template has only the anchor of these. Everything else is added in the map.

| Element | Found by | Rules |
|---|---|---|
| **Strategist anchor** | first node in group `strategist_anchor` | The strategist camera starts looking at it, and its y is the ground height the camera pans over. With none, the camera looks at the origin. |
| **Cover** | `CSGBox3D` nodes in group `cover` | It must be solid: `use_collision` on, or under a `CSGCombiner3D` with it on. It must be in or under a `navmesh` node (in the template, `Geometry/Cover`). A box under 0.9 m tall is ignored; at most 96 are read (`CoverBoard.LevelCapacity`); only yaw is read, not tilt. Any other node type in the group is skipped and logged. Low walls are 1.2 m and blocks 2.8–3.0 m ([`COVER.md`](COVER.md) §2.3). |
| **Barracks defences** | `DefenseMount.cs` children of a barracks (group `barracks_defense`) | Sorted by scene path. `Kind` is 0 for a gun and 1 for a mortar. Range, minimum range, reaction time, traverse and muzzle height are exports. The template's barracks has the greybox's set. |
| **Heavy guns, mortar launchers** | `Scripts/Fps/Emplacement.cs`, `Scripts/Fps/MortarLauncher.cs` (group `emplacement`) | At most 8 (`SimConfig.MaxEmplacements`). Sorted by name. |
| **Ammunition cans** | `Scripts/Fps/AmmoCan.cs` (group `ammo_can`) | At most 12 (`SimConfig.MaxAmmoCans`). Sorted by name. |
| **Vehicle kiosk** | `Scripts/Fps/VehicleKiosk.cs` (group `vehicle_kiosk`) | Only the first by name hands out vehicles. It must be upright and unscaled, like the locker. Its `Marker3D` children **`Pad4x4`** and **`PadHelicopter`** are where each kind is put, facing the marker's −Z. A kind with no pad is not handed out ([`GROUND_VEHICLES.md`](GROUND_VEHICLES.md) §6.6). |
| **Bunker, set pieces** | e.g. `Scenes/Environment/Pillbox.tscn`, `VehicleTestStrip.tscn` | Scenery. A collider in `navmesh` is walked round. |
| **Imported structures** | a `.glb` instanced under `Geometry` | A Blender object named `…-col` imports as a mesh with a trimesh `StaticBody3D` on layer 1, so it is walked on, baked into the navigation mesh and stops bullets; one without the suffix is only drawn. Example: `Models/Environment/GullyBridge.glb`, built by `tools/blender/gully_bridge.py`, whose materials its `.import` maps to `Levels/materials/BridgeGreybox.tres` and `BridgeCable.tres`. |
| **River** | `Scripts/Tools/WaterSurfaceGenerator.cs` on a `Node` under a `Path3D` | Drawn only: no collider, and nothing in the simulation reads it. The ribbon is built from the path's curve when the scene loads, and again in the editor whenever the curve or the path moves; the path's Y is the water level. Material `Levels/materials/RiverWater.tres` (`River.gdshader`). Make it wider than the wet bed, so its edges end inside the banks. |

---

## 4. Checking a map

**Without Godot.** This reads the scene text and follows inherited and instanced scenes:

```bash
dotnet test Tests/Gdpyr.Tests.csproj --filter "FullyQualifiedName~MapSceneTests|FullyQualifiedName~MapCatalogTests"
```

It runs every §3.1 check on the template and on each catalog map. That covers the root script,
spawns, navigation region and source, barracks count and doors, node count, locker, the cover's node
types and count, and positions inside the field. It does not bake, path or play. An instanced
imported model, such as a `.glb`, is read as the one node it is instanced as: the file must be in the
checkout, and nothing inside it is looked at.

**In the engine.** Host from the main menu with the map picked. The server log should show:

| Line | Means |
|---|---|
| `[rts] navigation baked: N vertices` with N > 0 | the bake found geometry |
| `[cover] N boxes from the map` | how many cover boxes were read; `, K in the 'cover' group not read` means some were the wrong type or too low |
| `[match] round live …` | the round started |

None of these should appear: `no nodes in group 'player_spawn'`, `no nodes in group 'barracks'`,
`no NavigationRegion3D in group 'navigation_region'`, `navigation bake produced no geometry`,
`weapon locker … is scaled or tilted`, or `… only the first N are addressable on the wire`.

`Source geometry parsing for navigation mesh baking had to parse RenderingServer meshes at runtime` is
printed when CSG geometry is in the bake, the greybox included. It does no harm.

### 4.1 Measured

Headless server plus one headless client, default bots (6 ground, 1 strategist), the catalog default
pointed at the scene in a scratch copy:

| Scene | Nav vertices | Cover read | What happened |
|---|---|---|---|
| `Scenes/Test.tscn` (greybox, reference) | 565 | 26 | round live; kills, a pillbox placed and destroyed within 70 s |
| `Scenes/Maps/MapBase.tscn` | 30 | 0 | round live; units trained 0 → 9; ground bots took the node at ~40 s, and the strategist retook it at ~90 s; deaths, ticket loss, respawns; a sandbag wall and a pillbox placed |
| a map inheriting it (node moved, second node, second barracks, two cover boxes) | 87 | 2 | round live; 2 barracks, 2 nodes, 2 cover boxes read; units trained 0 → 9; a node captured; combat |
| `Scenes/Maps/Gully.tscn` (§2.4) | 4164 | 0 | `--map gully` and the host panel: round live. Over two 3-minute seeds of HTN bots on both sides: 22–24 units trained, 23–29 kills, the node taken by each side once |

The only new log line on any of them was the CSG warning above, from the inherited map's cover boxes.

Gully also has scenarios: `Tests/Scenarios/gully_spawn_points_shared.json` (six bodies on four
markers stay on the plateau), `gully_strategist_economy.json` and `gully_ground_takes_node.json` (HTN
ground bots cross the bridge and take the node).

---

## 5. Adding a map: the checklist

1. **Scene → New Inherited Scene…** from `Scenes/Maps/MapBase.tscn`. Save it as `Scenes/Maps/<Name>.tscn`
   and rename the root.
2. Build the level under `Geometry`, as static colliders on layer 1. Put cover boxes under
   `Geometry/Cover` (group `cover`).
3. Move the template's spawn group, locker, barracks, node and anchor where the map wants them. Keep
   the play inside ±115 m and the nodes and spawn outside the barracks' ~107 m ring.
4. For more barracks, instance `Scenes/Environment/Barracks.tscn` (up to 4). For more nodes, add
   `Node3D`s with `ResourceNode.cs` (up to 8). Name them so the sorted order is the one you mean.
5. Add the `MapCatalog` entry (§1), after the greybox, with an id that will never change.
6. Run `dotnet test` (§4), then host it from the menu and read the log.

---

## 6. Data maps

A `.map` is the whole map in one file: the ground as a 241 × 241 heightfield over the 240 m field,
1 m between samples; the water level and the texture bands; every object as a prefab id from
`Scripts/Core/PrefabCatalog.cs`, a position, a yaw and its parameters; and the roads. There is no
scene, no script and no float in it, and it is at most 1 MiB ([`LEVEL_EDITOR.md`](LEVEL_EDITOR.md)
§6.8). **A data map is its SHA-256**: the wire names it by its hash, a client's cache keeps it under
it, and `maps/active` and a demo name it by it. A file is never re-encoded, so a map saved again is a
new map with a new hash, and the same bytes under two names are one map.

### 6.1 Where one comes from

| Made by | How |
|---|---|
| The map editor | **Map editor** on the main menu, or `godot --path . -- --edit [name]`: terrain brushes, prefabs, roads and bridges, undo, save and autosave, **Validate**, **Deep** and **Test** ([`LEVEL_EDITOR.md`](LEVEL_EDITOR.md) §8; the keys are in the [README](../README.md)) |
| The generator | `dotnet run --project tools/Gdpyr.MapTool -- generate <file> --seed N --name TEXT`: rolling hills with a round's pieces levelled into them. It made the bundled map, and the maps the fetch and upload checks play |
| A server | a map a client fetched is kept in its cache; a map clients upload to a dedicated server started with `--map-upload-token` is kept in the server's ([`LEVEL_EDITOR.md`](LEVEL_EDITOR.md) §9.5) |

### 6.2 Where it lives

| Place | Holds | Listed in the host panel |
|---|---|---|
| `user://maps/<name>.map` | one's own maps: what the editor saves | yes, when it reads and has no error |
| `user://maps/cache/<sha-256>.map` | maps received: fetched from a server, or uploaded to one | no |
| `res://Maps/<id>.map` | maps bundled with the build, each with a catalog entry | yes, as a catalog map |
| `user://maps/active` | on a dedicated server, the hash of the map it plays when started with no `--map` | — |

`user://` is `~/.local/share/godot/app_userdata/gdpyr/` on Linux. A name is `a-z`, `0-9`, `_` and
`-`, at most 48, and is the only place a typed string becomes a path (`MapNames`).

### 6.3 What it must hold

What `MapValidator` refuses, in the terms of §3.1 — a map with any of these is not listed, hosted,
sent or loaded, and an unfinished map with them is still saved:

| | Error |
|---|---|
| Counts | no spawn line (four `player_spawn` markers 4 m apart); no weapon locker; barracks not 1–4; resource nodes not 1–8; more than 8 heavy guns and mortar launchers, 12 ammunition cans, 96 cover boxes, 16 structures, one vehicle kiosk or one strategist anchor |
| Ids | a prefab id this build's catalog does not have: "needs a newer build" |
| Place | an object outside ±120 m, or a round's piece outside ±115 m (§3.1, item 7); two objects in each other's way |
| Ground | a padded object not on its levelled pad; a round's piece under the water |
| Limits | ground outside −16…+64 m; a number outside its range |

Shown and played: a node or the spawn line inside a barracks' defended ring (§2.3); more than 4 m of
relief inside a node's capture radius; a road steeper than its limit; a bridge end left open; and,
from the editor's **Deep** check, which bakes the navigation mesh, a node that no barracks door or
spawn line has a path to. The list in full is [`LEVEL_EDITOR.md`](LEVEL_EDITOR.md) §6.9.

Three things differ from a scene map. The navigation bake's walkable slope is 40°, not §3.1's 45°,
since a body stalls on a face of 43° (LEVEL_EDITOR LE-D23). The world's nodes are named by their
order in the file — `Barracks1`, `ResourceNode3`, `AmmoCan07` — so the name-sorted indices of §3 are
the same on every peer. And a structure in the file — a pillbox, a sandbag wall, a sniper tower —
stands when each round starts, the strategist's and as destructible as one a builder made.

### 6.4 Checking one

**Without Godot:**

```bash
dotnet run --project tools/Gdpyr.MapTool -- validate f.map   # 0 plays, 1 refused (with each reason), 2 not a file
dotnet run --project tools/Gdpyr.MapTool -- info f.map       # what is in it, and its hash
```

**In the editor:** **Validate** lists every issue, and a click on one flies to it; **Deep** also bakes
and asks for every path; **Test** (`F5`) saves and plays the map in a second process.

**In the engine:** `godot --path . -- --practice --map f.map`, or `--server`/`--listen` with the
same `--map`. The log says `[map] playing … from <path>, sha-256 <hash>`, then the lines §4 lists. A
file that does not read or does not validate is a launch error, exit 1, with every reason.

### 6.5 Bundling one with the build

1. Put the file in `Maps/` as `<id>.map`, the id a map's name (§6.2). `.gitattributes` marks `*.map`
   binary, and the export preset's `include_filter` takes `*.map` into the pack.
2. Add the entry after the scene maps:
   ```csharp
   MapInfo.Data("ridge", "Ridge", BundledDirectory + "ridge.map", "<64 hex digits>",
   	"a data map: …"),
   ```
   The hash is the file's: `dotnet run --project tools/Gdpyr.MapTool -- hash Maps/ridge.map`.
3. `dotnet test`. `BundledMapTests` holds each bundled map's file to its entry's hash, reads and
   validates it with no warning, and holds it to what a round needs. It also holds that there is
   exactly one bundled map (`ThereIsOne`), so a second changes that test.

Bundling is only for a map every build should carry. A map a server plays does not need it: a client
fetches it (LAN.md §3.4), and a dedicated server is given one with `scripts/deploy-map.sh`
([`DEPLOYMENT.md`](DEPLOYMENT.md) §3.1).
