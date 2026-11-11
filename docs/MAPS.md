# Maps

Status: **built.** `Scenes/Maps/MapBase.tscn` holds everything a round needs. New maps inherit from
it. A map shows up in the host panel's **Map** picker once it has an entry in
`Scripts/Core/MapCatalog.cs`. `Tests/MapSceneTests.cs` checks the requirements below against every
catalog map and against the template, and needs no Godot install.

Scope: how a map reaches the map select, what the template holds, what the engine looks for in a map
scene, and how to check a new one. Hosting and the wire are in [`LAN.md`](LAN.md) §3.4. Cover is in
[`COVER.md`](COVER.md). Nodes and perks are in [`CONTROL_POINTS.md`](CONTROL_POINTS.md).

A second kind of map is planned and not built: a data file made in an editor in the game, which a
client can fetch from its server ([`LEVEL_EDITOR.md`](LEVEL_EDITOR.md)). Nothing here changes for it.

---

## 1. Getting into the map select

The host panel (**Host…** in the main menu) lists `MapCatalog.All` in order. A map is one entry there:

```csharp
new("dunes", "Dunes", "res://Scenes/Maps/Dunes.tscn",
	"two nodes in a valley, the barracks on the ridge"),
```

| Field | Rule |
|---|---|
| `Id` | What the server sends to every client (`NetworkManager.ServerMap`), and how the client finds the map in its own catalog. Matched exactly and case-sensitive. **Never change an id once a build that has it is out.** It must be unique (`MapCatalogTests.IdsAreUnique`). |
| `Name` | The picker's text. |
| `ScenePath` | `res://…/*.tscn`. The file must exist (`MapCatalogTests.TheScenesAreInTheCheckout`) and must meet §3 (`MapSceneTests`). |
| `Description` | One line under the picker. |

- **Nothing scans a folder.** A scene in `Scenes/Maps/` with no catalog entry is not a map.
- **The first entry is the default** (`MapCatalog.Default`). `--server`, `--listen` and demo playback
  load it. There is no `--map` flag. Keep the greybox first
  (`MapCatalogTests.TheDefaultIsTheGreybox`) and add new maps after it.
- **The host and every client need the entry.** A client whose build lacks the host's map id refuses
  it and disconnects instead of guessing. Both machines must run the same commit
  ([`LAN.md`](LAN.md) §3.4, §5).
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

---

## 4. Checking a map

**Without Godot.** This reads the scene text and follows inherited and instanced scenes:

```bash
dotnet test Tests/Gdpyr.Tests.csproj --filter "FullyQualifiedName~MapSceneTests|FullyQualifiedName~MapCatalogTests"
```

It runs every §3.1 check on the template and on each catalog map. That covers the root script,
spawns, navigation region and source, barracks count and doors, node count, locker, the cover's node
types and count, and positions inside the field. It does not bake, path or play.

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

The only new log line on any of them was the CSG warning above, from the inherited map's cover boxes.

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
