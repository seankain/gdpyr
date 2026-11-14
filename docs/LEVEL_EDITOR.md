# gdpyr — Level editor: terrain, prefabs, roads and bridges, and maps that travel

**Status: a plan, with LE0–LE5 done: its ground is measured; the ground's rules, the map in
memory, its file and its validator are written and tested, engine-free; a tool makes, reads and
checks map files; a map is played from its file — `--map`, one bundled map, a server and its
clients, the stripped export; and the game has an editor — the menu's **Map editor** and `--edit`,
the terrain tool, undo, save and autosave, validate and test, and the prefab tool: a round's pieces,
cover, scenery and structures that stand when a round starts, the buildable overlay and the deep
check. Nothing of roads or bridges in it, or of fetching a map, is built.**
Written against `cd0c558`. The
facts in §2 were read in this repository's source at that commit, and those in §3 in Godot's source
at tag `4.6-stable`. The arithmetic in §10 was run: `python3
tools/level_editor_checks/design_checks.py` prints every number quoted from it. No Godot and no
.NET SDK were available where it was written, so nothing was compiled and nothing was run in the
engine; LE0 (§11) was the phase to measure what could not be, and it has: at `67fec82`, on Godot
4.6 .NET, by `scripts/level-editor-checks.sh`, whose output is in `docs/baselines/` and is set out
in §10.1. LE1 (§11) wrote §6.1–6.4 as `Scripts/Sim/Level/`, at `744b906`: `dotnet test` holds each
claim made there, and `tools/level_editor_checks/le1_mutations.py` breaks each rule in turn to show
that a test fails when it is broken. LE2 (§11) wrote §6.5, §6.8, §6.9 and §8.6's undo the same way,
with `tools/Gdpyr.MapTool`, at `ceaaec9`, and `tools/level_editor_checks/le2_mutations.py` does the
same for its rules. LE3 (§11) built §7, §9.1's reading of a map, `--map` and `--practice` at
`fa37670`, and `scripts/level-editor-checks.sh`'s `map-*` parts play the bundled map through them.
LE4 (§11) built §8's shell and terrain tool at `98c6125`: the script's `editor` part drives the editor
with no window through its own commands, and `tools/level_editor_checks/le4_mutations.py` breaks its
engine-free rules in turn. LE5 (§11) built §8.4's prefab tool, the catalog's engine half, a map's
structures at a round's start, the buildable overlay and the deep check at `e03c74b` and `bd8361e`:
the script's `editor-prefabs` part makes a map in the editor alone and plays a bot round on it, and
`le5_mutations.py` breaks its rules in turn.

**What LE0 changed.** The collider is not a `HeightMapShape3D`: 65 of 20,000 long rays passed
through one, and it costs a tick more than the same triangles as meshes, which let none through
(LE-D5). A body and a unit walk a 42° face on triangles and stall on a 43° one, so walkable is 40°
(LE-D23). The 4x4's hull is level whatever is under it: it beaches on 10° from rest and rides up
70° at speed, and is a road vehicle on these maps until that is mended (LE-D24). A correction is
applied from 2 cm (LE-D25). A structure's refusal on a slope is as §7.5 had it (LE-D21). A re-bake
is not too slow to build on (§7.3). A tick on hills with a round of bots is 2.2–2.6 times the
slab's and over its time for 1 in 100, which is a risk and LE3's to measure where the game is served
(§13). Where a section says "LE0", it is one of these.

**What LE1 changed.** A grade-locked brush rises from the apex the ground about its centre already
holds, not from the height under the cursor, which stood it still between samples (§6.2). Its
promise of a walkable face is for ground that was no steeper than its setting. Smooth keeps to a
sample's neighbours along the field's edge there, so an even slope is not bent at it, and a ramp's
ends are skirted as its sides are. Rounded to steps, levelled again and rounded, ground is what it
was, which is what keeps a pad (§6.3). On rough ground a pad left a face 1.18 times the limit where
§10's script saw 1.15, and on level ground it cannot pass 1.156 (§10). Where a section says "LE1",
it is one of these.

**What LE2 changed.** A new map's water is 5 m under its level ground, which is then grass and not
sand (§6.4). A pad's level is the mean of the ground under it to the centimetre, as the object's
height is stored, and then the nearest step: a function of what the file holds, so a pad can be
levelled again from the file alone (§6.5). A pad is kept by levelling every pad again in the order
they were placed, not only those an edit reaches: one pad levelled again can move ground a later pad
had set, and only that pad levelled again puts it back (§6.5). Whether a padded piece is under the
water is asked of its pad's level, by placement and validator alike (§6.5, §6.9). The heights are
deflated in zlib's format, which is what §10 measured; a file's text is UTF-8 with no control
character but a description's line break; a file has its four chunks (§6.8). A prefab's boxes, its
parameters and their ranges, and a bridge section's frame are set (§6.5). One of §6.9's warnings and
one of §6.5's checks need a road's profile and are LE6's; reach is LE5's (§6.9). Where a section says
"LE2", it is one of these.

**What LE3 changed.** A structure's plinth is 0.5 m deep for every site, not down to the lowest
ground under it: the rule takes no site with ground lower than that, and every peer builds the same
body with nothing new on the wire; a corner's ground is looked for within 0.55 m of the centre's
(§7.5). The kiosk's pads are placed inside its footprint, and a spawn line's markers named so that
two lines sort apart (§7.1). A structure in a file is left for LE5 to put up (§7.1). The terrain
shader's fifth sampler is the road's, and the placeholders are imported with mipmaps (§7.2). The
host's picker and `HostSettings` keep the catalog's ids until a client can fetch a map (§9.1).
Where a section says "LE3", it is one of these.

**What LE4 changed.** A held brush dabs 30 times a second, each dab a thirtieth of a second, and at
most eight in a frame: what §6.2 left to the editor (§8.3). The editor lists every map of one's own
that reads, finished or not, where LE-D16 lists only maps that validate — that rule is for what is
played, and an unfinished map is a file to open (§8.1). **Save as** makes a map that has a file a
fork of it, a new GUID with that file its parent, so no two files share a lineage; **Test** saves
only what is not saved, and from a headless editor starts a headless process (§8.6, §8.7). The
camera looks on the middle button, since the right one lowers the ground, a held Ctrl moves it
nowhere, and `Q` and `E` lower and raise it (§8.2). A band slider is not a step to undo, and an edge
is held between its neighbours (§8.3). Where a section says "LE4", it is one of these.

**What LE5 changed.** An object turned, moved or given a parameter is taken up and placed again where
it is in the file, so the world names it as before, and it is not counted, in the way or a neighbour
to itself (§6.5, §8.4). A ramp's deck and rails are sheared boxes that end square at the section's
ends: tilted boxes overhung them by up to 12 cm, into a joined section's boxes (§6.7). *Buildable* is
where a pillbox is taken square to the field and turned 45° (§7.5). The deep check's walk reaches its
node when the path ends within 3 m of it flat and 2 m in height (§6.9). A practice round's process can
crash as it exits, after a round of bots, at LE4's commit as at LE5's; LE5's check reads a test
process's facts from what it writes, not from its exit code (§13). Where a section says "LE5", it is
one of these.

The decisions (LE-D1–LE-D25, §4) are the defaults the plan is written against. Each is one line to
change; LE-D3, LE-D8, LE-D10 and LE-D18 were the ones to flag before LE1, which was built on them
as they stand — LE-D3's grid and LE-D10's pad are code now, and changing either is changing that
code and its tests — and LE-D24 and LE-D25 were the ones before LE3, which built LE-D25 and keeps
LE-D24 flagged. The phases (LE0–LE9, §11) say what each builds, what checks it, and what is left for
a person.

This is not [`MAPS.md`](MAPS.md)'s way of making a map. That one is the Godot editor, a scene that
inherits `Scenes/Maps/MapBase.tscn` and a catalog entry, and it stays. This plan adds a second kind
of map — a data file made in the game — and the way it reaches a server and its clients.

Companion documents: [`MAPS.md`](MAPS.md) §3 (what a map must hold); [`LAN.md`](LAN.md) §3.4 (the
host's map on the wire), §5 (one commit, every machine); [`NETCODE.md`](NETCODE.md) §1 (authority),
§7 (the message set), §10.5 (structures: footprints, placement, the navigation re-bake);
[`COVER.md`](COVER.md) §2.3 (cover boxes); [`DEMOS.md`](DEMOS.md) §2 (the container this file
format copies); [`DEPLOYMENT.md`](DEPLOYMENT.md) §2–3 (the export, the unit, the box);
[`EFFECTS.md`](EFFECTS.md) §3–4 (a placeholder, and the contract art replaces it under).

---

## 1. What is asked

1. A **terrain editor**: raise and lower the terrain with **different brushes**, which make the
   grade **shallower or steeper** and the edited area **bigger or smaller**. The terrain's
   **texture follows elevation and grade**, in the order water → sand → mud → grass → stone.
2. A **prefab placement UI**: select, rotate and place prefab meshes — pillboxes, structures — on
   the terrain. Placement **checks for existing meshes in the way**. A successful placement **takes
   the average grade of the terrain underneath and makes a flat enough area** for the footprint.
3. **Roads and bridges.** Bridge sections are placed like prefabs but **connect at their edges**,
   and are **free-floating or stand on pillars that reach down to the terrain**.
4. A way to **save, clone and export** maps, and to **share** them with a **dedicated server** and
   with **other clients**.

| Asked | Rules | Editor | Wire and disk |
|---|---|---|---|
| 1 | §6.1–6.4 | §8.3 | — |
| 2 | §6.5 | §8.4 | — |
| 3 | §6.6–6.7 | §8.5 | — |
| 4 | §6.8–6.10 | §8.6 | §9 |

## 2. What the code does today

Each read in the source at `cd0c558`.

| Fact | Where |
|---|---|
| A map is a scene with a catalog entry. `MapCatalog.All` holds one, `greybox` → `Scenes/Test.tscn`. "Nothing scans a folder." New maps inherit `Scenes/Maps/MapBase.tscn` | `Scripts/Core/MapCatalog.cs:52`; MAPS.md §1–2 |
| A client is told its server's map as an id string, first on the connection. It refuses an id its catalog lacks and gives up after 5 s without one. Nothing checks that both ends are the same build | `NetworkManager.ServerMap`, `Scripts/Net/NetworkManager.cs:454`, sent at `:472`; `MainMenu.TakeServerMap`, `Scripts/Ui/MainMenu.cs:249`, `:43`; LAN.md §3.4, §5 |
| A role request that arrives before the map is loaded is kept: `LocalAwaitingRole` is set on the autoload and applied when the local character exists | `Scripts/Match/CombatManager.cs:2122`, `:287` |
| Every path into a round is `ChangeSceneToFile(Session.WorldScenePath)`. The map's root, `WorldRoot`, opens the session from `_EnterTree`; the autoloads find the map's pieces by group or by script on the first tick after that | `MainMenu.cs:459`; `Scripts/Ui/GameConsole.cs:909`; `Scripts/Core/WorldRoot.cs:17`; `UnitManager.EnsureStarted`, `Scripts/Rts/UnitManager.cs:382`; `PlayerManager.CollectSpawnPoints`, `Scripts/Net/PlayerManager.cs:1844` |
| What a map must hold: a `WorldRoot` root; `player_spawn` markers; one `navigation_region` with `navmesh` source geometry; 1–4 barracks, each with a `SpawnPoint`; 1–8 resource nodes; a weapon locker; all inside ±115 m. Optionally an anchor, ≤ 96 cover boxes, ≤ 8 emplacements, ≤ 12 cans, a kiosk. Names sorted are indices on the wire. `MapSceneTests` asserts it from the scene's text | MAPS.md §3; `Tests/MapSceneTests.cs`; `Scripts/Sim/SimConfig.cs:262`, `:331`, `:360`, `:363`; `Scripts/Sim/Cover.cs:226`; `Scripts/Sim/Cargo.cs:132` |
| The world is Godot's physics layer 1. Rounds, sight lines, the fog, wheel springs, aircraft height, landing sites, structure sites, the strategist's cursor and the agent's planes each ask it with a ray or a shape; bodies meet it through `MoveAndSlide`. No engine-free code knows where the ground is | `CombatManager.WorldHitFraction`, `Scripts/Match/CombatManager.cs:3188`; `Scripts/Fps/CrewVehicle.cs:294`, `:303`; `Scripts/Rts/UnitManager.Flight.cs:171`; `Scripts/Rts/UnitManager.Structures.cs:407`; `Scripts/Rts/StrategistController.cs:1484`; `Scripts/Agent/AgentPlaneView.cs:125`; `Scripts/Rts/Unit.cs:443` |
| Navigation is baked at runtime on the server, synchronously, on the first tick: static colliders on layer 1 under nodes in group `navmesh`; cells 0.2 / 0.1 m, radius 0.4 m, climb 0.5 m, slope 45°. It is baked again, on a thread, each time a structure finishes or falls, and logs what that cost | `UnitManager.BakeNavigation`, `UnitManager.cs:2742`; `UnitManager.Structures.cs:1021` |
| The floor is a flat 240 × 240 m slab, top at y = 0, walled at ±120 m. Play is kept inside ±115 m. A position on the wire is three `int16` at 1 cm: ±327.67 m | `Scenes/Maps/MapBase.tscn`; `Cargo.cs:132`; `SimConfig.cs:443`, `:449` |
| The flat floor is assumed in writing in six places: structures and cover are yaw-only ("nothing here is built on a slope"); the strategist camera's limits and pan ("the map is flat") and its cursor's fallback plane at y = 0; the mortar launcher's "level plane through its feet"; an aircraft's range on the wire ("the ground anywhere on the map, which is under 30 m"); a capture pad ("the pad is flat") | `Scripts/Sim/Structures.cs:34`; `Scripts/Rts/UnitManager.Cover.cs:32`; `StrategistController.cs:415`, `:1473`; `Scripts/Sim/MortarLay.cs:16`; `Scripts/Sim/Flight.cs:17`; `Scripts/Sim/Capture.cs:26` |
| Four constants are absolute heights: a projectile below y = −50 m is dropped; a drone below −20 m or above 120 m is lost; the agent's passability rays start at y = 80 m and reach −40 m | `SimConfig.cs:143`; `Scripts/Sim/Drone.cs:75`, `:78`, `:178`; `AgentPlaneView.cs:31`, `:34` |
| A strategist's structure is placed by engine-free footprint rules — bounds, door clearance, rectangles overlapping by separating axes — then a ray to the ground under its centre and a box query. The box is 0.1 m smaller than the body and lifted 0.15 m, so its underside is 0.2 m over the ground under the centre. A structure's pose is a point and a yaw. A scenario can put a finished one down free | `StructurePlacement.Check`, `Structures.cs:496`; `Footprint.Overlaps`, `:289`; `UnitManager.Structures.cs:57`, `:407`, `:465`; `ServerPlaceStructure`, `:260` |
| Bodies and units take the engine's floor defaults; nothing sets `FloorMaxAngle` or `FloorSnapLength` | searched `Scripts/`, `Scenes/` |
| The physics engine is pinned to GodotPhysics3D; the renderer is GL Compatibility | `project.godot:296`, `:300` |
| The transport is ENet behind `TransportFactory`, made with the default channel count. No RPC names a transfer channel. Nothing in `Scripts/` compresses or hashes | `Scripts/Net/TransportFactory.cs:19`; searched |
| What a player's process writes lives under `user://`: demos, through an engine-free name parser, a reader that is total, and `System.IO`. A demo's header names its map by scene path | `Scripts/Net/DemoFiles.cs`; `Scripts/Sim/Demo/DemoFormat.cs:119`, `:153`; `Scripts/Net/DemoRecorder.cs:85` |
| There is no way back from a round to the menu: leaving is quitting the process | LAN.md §4, §9 |
| The dedicated server is one export preset, resources stripped, `include_filter` empty. Its unit runs `--server 7777` with `HOME=/var/lib/gdpyr`, `Restart=always`, and that directory the only writable one. There is no `--map` flag | `export_presets.cfg`; `deploy/gdpyr-server.service`; MAPS.md §1 |
| A stripped server has no mesh data to read, which is why the bake reads colliders | `UnitManager.cs:2790` |
| UI is built in code; the decisions behind it are engine-free and unit-tested where they can be | `MainMenu.Build`, `MainMenu.cs:566`; `Scripts/Ui/StrategistCards.cs`; `Scripts/Core/ConsoleCommands.cs` |
| Art on hand: greybox grid textures in blue, yellow, orange, green and grey. No terrain, road or bridge art | `ThirdParty/GreyboxTextures/` |
| Widths that size a road: the tank is 2.6 m across, the supply truck 2.2 m, the technical 2.0 m; the 4x4's track is 1.7 m and its wheelbase 3.0 m | `Units/*.tres` (`RadiusMeters`); `Vehicles/four_by_four.tres:34`, `:35` |
| No `dotnet` and no Godot were available where this plan was written | — |
| *Read for LE0, at `67fec82`.* A crew vehicle's body is put upright each tick, turned to its heading and nothing else, and moved floating: pitch and roll are numbers in its state that its collider does not take. The 4x4's has 0.25 m under it between its axles | `Scripts/Fps/CrewVehicle.cs:283`, `:209`; GROUND_VEHICLES.md §12's table |
| *Read for LE0.* A player's position and velocity cross the wire at 1 cm an axis, and a client's prediction is corrected when it is more than 1 cm or 0.5 m/s from what came back | `Scripts/Sim/SimConfig.cs:443`, `:97`, `:100`; `Scripts/Sim/Quantize.cs:57`; `Scripts/Sim/PredictionLedger.cs:154` |

## 3. What the engine does

Read in `godotengine/godot` at tag `4.6-stable`, the version `Gdpyr.csproj` names, and in
`godotengine/godot-docs` at branch `4.6`. docs.godotengine.org was not reachable from where this
was written; a class reference quoted here is the XML that site is generated from.

| Fact | Where |
|---|---|
| `HeightMapShape3D`: grid points 1 unit apart, centred on the shape's origin; each square two triangles; a NaN height is a hole; "faster to check collisions against than `ConcavePolygonShape3D`" — which a round of bots, rays and all, did not bear out (LE0, §10.1). For another spacing the node is scaled, and "GodotPhysics3D does not support non-uniform scaling" | `doc/classes/HeightMapShape3D.xml` |
| In GodotPhysics3D sample (x, z) is `heights[z * width + x]`, at (x − (width − 1)/2, z − (depth − 1)/2). A cell's triangles are (x,z) (x+1,z) (x,z+1) and (x+1,z) (x+1,z+1) (x,z+1) | `modules/godot_physics_3d/godot_shape_3d.h:406`; `godot_shape_3d.cpp:2015` |
| Setting its data rebuilds a min/max grid of 16-sample chunks in one pass over every sample. There is no partial update | `godot_shape_3d.cpp:2077`, `:2146` |
| A heightmap and a triangle mesh both give the collision solver one `GodotFaceShape3D` a triangle | `godot_shape_3d.cpp:1416`, `:2015` |
| A ray hits a heightmap from either side by default (`hit_back_faces`) | `doc/classes/PhysicsRayQueryParameters3D.xml` |
| A ray 16 m or longer in plan is not walked over a heightmap's cells but over its 16-cell chunks, and inside each chunk it passes the height of, over that chunk's cells with the piece of the ray inside it. That piece starts on a lane between cells, give or take a float; a start one float short of the lane is floored into the cell before it and then taken for a start exactly on the lane, which puts the walk one cell behind for the rest of the chunk. A face is not tested from behind on either walk. **Found by LE0** (§11): such a ray can pass through the ground | `modules/godot_physics_3d/godot_shape_3d.h:400`; `godot_shape_3d.cpp:1975`, `:1757`, `:1861`, `:1804`; `tools/level_editor_checks/heightmap_ray_walk.py`, a float32 port that lets through the rays the engine did |
| A ray against a `ConcavePolygonShape3D` descends a BVH over its faces and tests each face whose box the whole ray crosses: no walk, and no piece of the ray | `godot_shape_3d.cpp:1371`, `:1333` |
| The bake's static-collider parser reads a `HeightMapShape3D`, into the same two triangles a cell | `scene/3d/physics/static_body_3d.cpp:195` |
| A bake's box (`filter_baking_aabb`) sets Recast's bounds on all three axes, and Recast drops a span wholly below them | `modules/navigation_3d/3d/nav_mesh_generator_3d.cpp:397`; `thirdparty/recastnavigation/Recast/Source/RecastRasterization.cpp:425` |
| Source geometry is always parsed on the main thread. It can be parsed once, kept, and baked from on a thread (`bake_from_source_geometry_data_async`). Tiles are baked with `filter_baking_aabb` and `border_size` | `doc/classes/NavigationServer3D.xml`; `doc/classes/NavigationMesh.xml`; godot-docs `tutorials/navigation/navigation_using_navigationmeshes.rst` |
| `CharacterBody3D`: `floor_max_angle` 45°, `floor_snap_length` 0.1 m, `floor_constant_speed` false — "faster on downward slopes and slower on upward slopes" | `doc/classes/CharacterBody3D.xml` |
| A contact is a floor when its normal is within `floor_max_angle` + 0.01 rad of up: 45.57°. A slide asks for up to six contacts, those met only in recovery among them, and one of them being a wall is enough, with `floor_block_on_wall` (the default), to cancel a grounded body's motion into it. **Found by LE0:** on triangles a capsule within its margin of an edge touches the next triangle on a normal up to 3.5° off the face's, so a face from 43° is a wall to it (§7.4) | `scene/3d/physics/character_body_3d.cpp:38`, `:543`, `:164`, `:216` |
| A floating body stops against a wall whose normal is within `wall_min_slide_angle` (15°) + 0.01 rad of its velocity reversed, and slides along anything else: moving level, along any face under 74.4° | `character_body_3d.cpp:429` |
| ENet: a host made with a channel count of 0 has 255. Transfer channel n > 0 is ENet channel n + 1; the first two are the default's. On a channel of its own, "reliable data will only block delivery of other packets on that channel". Default MTU 1,392 B; a packet may be 32 MiB | `modules/enet/enet_connection.cpp:308`; `modules/enet/enet_multiplayer_peer.cpp:357`; `doc/classes/MultiplayerPeer.xml`; `thirdparty/enet/enet/enet.h:222` |
| `ENetMultiplayerPeer.create_server` hands `create_host_bound` a channel count of 0 whatever it was given, and puts its own `max_channels` argument, when that is above 0, plus 2 in the incoming-bandwidth position | `modules/enet/enet_multiplayer_peer.cpp:65`; `modules/enet/enet_connection.cpp:45` |
| An RPC whose only argument is a byte array is sent as those bytes behind a header of 3–7 B: a command byte, a node id, a method id | `modules/multiplayer/scene_rpc_interface.cpp:357` |
| C#'s `[Rpc]` takes `TransferChannel` | `modules/mono/glue/GodotSharp/GodotSharp/Core/Attributes/RpcAttribute.cs` |
| The Compatibility renderer has no decals. It has `sampler2DArray` | godot-docs `tutorials/rendering/renderers.rst`; `doc/classes/Decal.xml`; godot-docs `tutorials/shaders/shader_reference/shading_language.rst` |
| "Strip Visuals" exports textures and materials as placeholders that keep their size and nothing else | godot-docs `tutorials/export/exporting_for_dedicated_servers.rst` |
| A file that is not a resource is exported only through a preset's include filter, whatever the preset's export mode | `editor/export/editor_export_platform.cpp:1194`; godot-docs `tutorials/export/exporting_projects.rst` |
| `OS.create_instance(arguments)` starts another instance of the running executable | `doc/classes/OS.xml` |
| An object decoded off the wire can run code: "Deserialized objects can contain code which gets executed" | `modules/multiplayer/doc_classes/SceneMultiplayer.xml` |

---

## 4. Decisions

| | Decision | Why |
|---|---|---|
| LE-D1 | **A map made here is a data file, not a scene.** One binary file, `.map`, magic `GDPYRMAP`, that one fixed scene (`Scenes/Maps/MapHost.tscn`) builds the world from. Scene maps and `MapCatalog` stay as they are. | A scene cannot be sent to a peer. It is an object graph that can carry scripts, and the engine's own warning for objects taken off the wire is that they "can contain code which gets executed" (§3). Bytes with no object in them can be hashed, bounded and refused. |
| LE-D2 | **The editor is in the game**: a button on the main menu and `--edit`. The Godot editor stays the way to make a scene map. | Asked 4: a map has to be made, saved and passed on by somebody running an export, and the server has no editor. One load path — file to world — for the editor, a client and a server. |
| LE-D3 | **Terrain is a heightfield of 241 × 241 samples, 1 m apart, over the 240 m field. A sample is an `int16` in 1/256 m.** The file carries its dimensions and spacing; this plan builds only this size. | 240 m and ±120 m are the field the code assumes (§2). 1 m is the one spacing a heightmap takes without scaling (§3), and the spacing LE0 measured the collider, the bake and a tick at (§10). Every `int16`/256 is exact as a `float` (§10), so every peer builds the same collider. 116,162 B raw. |
| LE-D4 | **Heights are held to −16 m … +64 m**, by the brushes and by the validator. | Inside every absolute constant in §2 with no change to any: the kill plane (−50), the drone's floor (−20) and ceiling (120), the agent's rays (80 down to −40). |
| LE-D5 | **The collider is the heightfield's triangles as `ConcavePolygonShape3D`s, one for each 16 × 16 cells: 225 of them on one body, on layer 1, in group `navmesh`.** A cell's two triangles are the ones a heightmap would make of it (§3). The mesh drawn uses the same triangles. *Changed by LE0 to the fallback this row was written with.* | Every consumer already asks layer 1, and the bake parses these shapes into the triangles it made of a heightmap, to the same mesh (§10): no consumer learns a new source of truth. One `HeightMapShape3D` was the first choice. LE0 found the defect its fallback was held for, in the shape's ray walk: of 10,000 rays 16 m or longer in plan, each from over the ground to under it, 36 passed through it over level ground and 29 over hills (§3, §10); none passed through the triangle meshes. Rounds, sight lines and the fog are such rays. The heightmap is cheaper to update — 0.2 ms for the whole field, against 0.2–0.3 ms a chunk and 37–48 ms for all 225 — and dearer to tick, by 2–4 ms with a round of bots on it. Both hand the same face test to the same solver (§3), and LE0 measured a body the same on both (§7.4). |
| LE-D6 | **Two families of brush.** *Sculpt* adds or removes height under a falloff: radius, rate and core. *Grade-locked* raises or lowers a cone of a set grade. With them, smooth, level and ramp. Named presets are starting points. | Asked 1. Sculpt is what sculpting feels like, and its grade follows from its settings (§6.2). Grade-locked is the one that can promise a grade, which is what "walkable" and "a wall" need. |
| LE-D7 | **Texture is computed, never painted**: a band from height above the water and slope, by one formula that only moves forward through water, sand, mud, grass, stone as either rises. | Asked 1. Nothing to store, and nothing to go stale when the ground under it is edited. |
| LE-D8 | **Water is a level, a look, and a floor for the navigation bake**: units do not path where the bed is more than 1 m under the water. Bodies, vehicles and rounds are not affected by it. | A river has to stop something for a bridge to matter, and the bake's box does that in one property (§3). Wading, drowning and fording are rules of their own (§14). **Flag if a player hidden under water is not acceptable for a first cut.** |
| LE-D9 | **A prefab is a catalog entry with an authored footprint, placed upright: a point and a yaw.** Three kinds: scenery, the pieces a round needs (MAPS.md §3), and strategist structures that stand when a round starts. | `StructurePose` is a point and a yaw (§2). A stripped server has no mesh to measure, so a footprint is data. A map without spawns, a locker, a barracks and a node is not a map, so the editor has to place them. "Pillbox" is both a scenery bunker and a live structure in this codebase. |
| LE-D10 | **A placed prefab levels its ground** with one operator (§6.3): the pad is level at the mean height under the footprint — the least-squares plane's height at its centre — out to 1.5 m past it, with a skirt no steeper than 30°. The edit is destructive; undo restores it. | Asked 2. The operator is proven to level exactly, to touch nothing that did not need it, and to steepen nothing past the skirt (§6.3). Upright prefabs need level ground, not merely planar. A stack of live modifiers is §14. |
| LE-D11 | **"In the way" is engine-free**: two objects collide when a box of one overlaps a box of the other in plan (separating axes, `Footprint.Overlaps`) and in height. | Asked 2. One answer for the editor's ghost, the validator and a test; a bridge may pass over a pillbox. The physics world is not asked, so the answer does not depend on a frame. |
| LE-D12 | **A road is a polyline that grades the ground**: its profile is the one closest to the ground at its grade limit (§6.6), levelled across, skirted by §6.3. The terrain's shader draws its surface. | Asked 3. No decals in this renderer (§3). The profile is provably the least cut and fill. |
| LE-D13 | **A bridge is prefab sections with connectors at their ends**: a 12 m span and a 12 m ramp, 6 m wide. A section snaps end to end and to a road's end; its height is free. Piers are a switch per section, and their lengths are written in the file. | Asked 3. 6 m passes a tank (2.6 m) and two 4x4s. |
| LE-D14 | **The file is the bake.** Heights, poses and pier lengths are stored final; a loader runs none of the editor's arithmetic. There is no float in the file. What a road was drawn from travels too, so a received map can be cloned and edited. | Peers that derive nothing cannot derive it differently. |
| LE-D15 | **A map has a GUID for its lineage and the SHA-256 of its file for its bytes.** The wire names a map by hash. A file is never re-encoded on its way: what is hashed is what is kept and sent on. | A compressor may differ between machines; bytes do not. |
| LE-D16 | **Three places**: `user://maps/` (one's own), `user://maps/cache/<hash>.map` (received, read-only), `res://Maps/` (bundled, in the catalog). A file is listed only if it validates. *The editor's own list takes every map of one's own that reads (§8.1, LE4).* | "A scene that is merely in the folder is not a map" (`MapCatalog`) holds: what makes a file a map is `MapValidator`, not its place. |
| LE-D17 | **A client without its server's map fetches it from that server**: 1,024 B chunks, reliable, on their own channel, 61 KB/s a peer; verified against the hash, validated, cached. | Asked 4. Under 2 s for a 1 m map's terrain and 2.5 s for the largest file one can be (§10). A channel of its own keeps a fetch from holding up the round's reliable messages (§3). |
| LE-D18 | **A dedicated server takes a map as a file**: `--map`, and `scripts/deploy-map.sh`. Upload over the game's connection is optional, off unless the server was given a token, and proven by HMAC. **Changing a server's map restarts its process.** | Asked 4. The box has a public address (DEPLOYMENT.md). A round cannot be torn down in place (§2); the unit already restarts the process. |
| LE-D19 | **Testing a map from the editor starts a second process** on the saved file. | No way back from a round (§2). It also exercises the real load path on every test. |
| LE-D20 | **Wire and formats: additive.** `ServerMap` gains fields; eight new messages, none in a round's tick; `DemoFormat.Version` 5 → 6; the agent's `welcome` names the map; the observation schema is unchanged. | Every trained checkpoint keeps loading; an old demo is refused, not misread. |
| LE-D21 | **A structure built during a round needs near-level ground**: the ground under its footprint may vary by 0.5 m, else `TooSteep`. It stands on the highest of it, on a plinth down to the lowest. *Confirmed by LE0; built by LE3, the plinth 0.5 m deep for every site (§7.5).* | Today's probe refuses a pillbox on a slope past 4.6–6.5°, by direction, and calls it `Blocked` (§7.5); LE0 measured it at 4.5–6.2°, as `Blocked`, with the downhill edge 0.2 m in the air where it is taken. The rule keeps about that limit, names it, and closes the gap under the downhill edge. |
| LE-D22 | **Placeholders**: the greybox textures by band; boxes for decks, piers and cover. A palette resource is where art goes. | IMPLEMENTATION_PLAN.md §5: greybox. |
| LE-D23 | **Walkable is 40°.** The navigation bake's slope goes from 45° to 40° on a data map; the skirt of §6.3 is 30° and not a setting; the slope overlay turns red at 40°. *Added by LE0.* | On triangles a body and a unit walk a 42.1° face and stall on a 43.0° one, both colliders alike; a box they walk to 45° (§7.4). The bake keeps faces to 45°, so it would route units over ground they stall on. 40° is under what they walk with 2° to spare, and over the 39.2° a 30° skirt can leave. |
| LE-D24 | **The 4x4 is a road vehicle on a data map, for a first cut.** It keeps the level hull it has. A road's grade limit stops at 15 % and a bridge's ramp stays 1 in 8. *Added by LE0.* | Its collider does not pitch with its chassis and has 0.25 m under it (§2). From rest at the foot it climbs 9.1° and beaches on 10.0°; at 18 m/s it rides up any face to 70° on its hull, off its wheels, and only 75° stops it (§7.4). 15 % is 8.5° and 1 in 8 is 7.1°. **Flag if a 4x4 that cannot leave the road, and that a cliff does not stop, is not acceptable for a first cut**: the fix is the hull taking the chassis's pitch and roll, which is GROUND_VEHICLES.md's to plan (§14). |
| LE-D25 | **A correction is applied from 2 cm, not 1 cm**: `SimConfig.MispredictionThreshold`. *Added by LE0; built by LE3.* | A position crosses the wire at 1 cm an axis (§2). On level ground two axes of it are rounded and the third is exact; on a slope all three are, which is up to 8.7 mm before anything has gone wrong. Behind an 80 ms link a body crossing a 20° slope was corrected up to 2.5 times a second, by 10–16 mm, in sessions whose clock was being resynchronised, and up to 0.75 times in sessions where it was not; with the threshold at 2 cm, 0.03 at the most in either (§10). **Flag if 1 cm is to be kept**: then that rate is the cost, and it is under GV3's bar of one a second in four sessions of six. |

---

## 5. The shape of it

```
 editing   tools ─► commands ─► MapDocument ─► MapFile.Write ─► name.map
             ▲ undo, redo       floats while a            the bake: every value final
             └──────────        stroke is down            SHA-256 of the file: its name on the wire

 loading   name.map ─► MapFile.TryRead ─► MapValidator ─► MapDocument ─► LevelRoot builds
             TerrainNode    225 triangle-mesh shapes on layer 1, in group navmesh; meshes and water on clients
             LevelBuilder   prefabs, cover, bridges, piers, walls; spawns, locker, kiosk, barracks, nodes
           then Session.EnterGame()

 joining   host ── ServerMap(id, hash, length, name, nonce) ──► client
                ◄─ ClientRequestMap(hash) ───────────────────   has the hash: load it
                ── ServerMapChunk × n ───────────────────────►  has not: fetch, hash, read, validate, cache
```

After `Session.EnterGame()` nothing differs from a scene map: every autoload finds what it always
found, by group and by script, on the first tick (MAPS.md §3).

```
Scripts/Sim/Level/   engine-free; compiled into Tests/ by the existing ../Scripts/Sim/** glob
  MapLimits.cs         the bounds: the grid, a step, the heights' limits, what is walkable, a file's
  HeightField.cs       the grid: samples, sampling on the collider's triangles, grades, a ray
  TerrainBrush.cs      sculpt, grade-locked, smooth, level, ramp: a dab is a pure function
  TerrainConform.cs    clamp(h, L, U): pads, road ribbons, bridgeheads
  TerrainBands.cs      water, sand, mud, grass, stone from height and slope
  PrefabSpecs.cs       the catalog's engine-free half: ids, footprints, limits, connectors
  PrefabPlacement.cs   may it go here, and the pad it makes
  RoadPath.cs          centreline, profile, ribbon
  BridgeRules.cs       connectors, snapping, piers
  MapDocument.cs       what a map is, in memory
  MapFile.cs           the container: a writer, and a reader that is total
  MapValidator.cs      what MAPS.md §3 asks, asked of a document
  MapNames.cs          a map's name, and what a --map reference names (LE3)
  MapTransfer.cs       sender and receiver as state machines over chunks; the upload's proof
  EditHistory.cs       undo and redo
  MapEditSession.cs    the editor's commands: a stroke, a ramp, undo, the bands, the text (LE4);
                       a placement, a turn, a parameter, a take-up, a pick (LE5)
  MapStore.cs          one's own maps on disk: save, autosave, the list, a fork (LE4)
  BuildableGround.cs   where LE-D21 takes a structure: the buildable overlay (LE5)
Scripts/Level/       the engine half
  LevelRoot.cs         MapHost.tscn's root: builds the world, then opens the session
  TerrainNode.cs       collider, chunk meshes, water, overlays
  TerrainPalette.cs    the five textures and their scales (LE3)
  LevelBuilder.cs      prefabs, a round's pieces, bridges and piers, the walls
  MapLibrary.cs        user://maps, the cache, the bundled maps
Scripts/Net/MapTransferService.cs    the messages, their channel, their budget
Scripts/MapEditor/   the editor scene's root and its commands (MapEditorRoot), the panels (.Ui), the
                     prefab tool (.Prefabs, LE5) and the deep check (.Deep, LE5); and the keys, a
                     brush's settings, the palette and the status line, engine-free (MapEditorControls)
Scripts/Core/PrefabCatalog.cs        the catalog's engine half: what each id is in the world (LE5)
Scenes/Maps/MapHost.tscn · Scenes/MapEditor/MapEditor.tscn · Levels/materials/terrain.gdshader
```

**Rejected:** a Godot editor plugin (LE-D2); sending scenes or a PCK (LE-D1); a `HeightMapShape3D`
collider, which LE0 measured and found rays through (LE-D5); a painted splat map (LE-D7); tilting a
prefab to its ground (LE-D9, LE-D10); decals or a ribbon mesh for roads (LE-D12); deriving pads and
piers on load (LE-D14); an engine-free copy of the ground for the simulation to query in place of
rays — the rays work, and two sources of truth would have to be kept equal.

---

## 6. The rules, engine-free

Everything here is `Scripts/Sim/Level/*.cs` with a test file of the same name, on `dotnet test`
with no engine. "Grade" is rise over run; a grade of k is atan(k) of slope. Two measures of it are
used, and the difference matters:

- an **edge** grade is between two samples joined by an edge of the collider's triangles: +x, +z,
  or the cell's diagonal from (x+1, z) to (x, z+1);
- a **face** grade is the length of a triangle's gradient. It is what the navigation bake's slope
  limit and a body's floor test read (LE-D23).

A face's gradient is the differences along its two axis edges, so **a face is at most √2 times as
steep as its steepest edge**, and is exactly that where a crease crosses it: heights 0, g, g at
(x,z), (x+1,z), (x,z+1). Every guarantee below is stated for edges and is exact; faces get the √2.

### 6.1 The heightfield — `HeightField.cs`

- 241 × 241 samples; sample (x, z) is index `z * 241 + x`, at (x − 120, z − 120) m. That is a
  heightmap's layout (§3), and the collider's triangles are the ones a heightmap makes of it
  (LE-D5): LE0 cast 1,000 rays down at level ground and at hills, and each met the collider where
  `Sample` says, to 0.02 mm.
- A stored sample is an `int16` q; its height is q / 256 m, a step of 3.90625 mm. While a stroke is
  down the editor works in `float`, so a slow brush accumulates; when the stroke ends the rectangle
  it touched is rounded to steps and that is the document.
- `Sample(x, z)` interpolates on the collider's triangle under the point: the first of §3's pair
  where the fractions sum to at most 1, else the second. A point off the field takes its edge.
- `Raycast` is the editor's cursor, which must not depend on when the physics server last saw the
  ground. Along a ray the ground is a broken line, bent where the ray crosses a lane of samples or
  a cell's diagonal. It walks those crossings in order and asks `Sample`'s arithmetic for the ray's
  height over the ground at each: where that changes sign the ray has met the ground, from either
  side, and between two crossings the ground is one triangle's plane, so the point is exact. No
  cell is picked by flooring a point that lies on a lane, which is how a heightmap's walk lets a
  ray through (§3): a crossing found a float early or late moves nothing. There is no ground past
  the field's edge.
- `EdgeGrade`, `FaceGrade`, and a normal by central differences for the mesh and the bands: half
  the difference of the samples either side, or the one difference there is at the field's edge.
- `MapLimits.MinHeightMeters` −16, `MaxHeightMeters` +64 (LE-D4). Every operation clamps to them.
  `MapLimits` also holds the grid's size, the step, and the 40° that is walkable (LE-D23).
- The class takes a size other than 241 × 241, which is what lets a rule about a grid be tested on
  a smaller one. A file of another size is the reader's to refuse (§6.8).

### 6.2 Brushes — `TerrainBrush.cs`

A dab is a pure function of the heights, a centre, the brush and a time step; it returns the
rectangle it touched. A stroke is dabs at the cursor.

**Sculpt.** `h += ±rate · dt · w(r / R)`, with w = 1 inside `core · R` and a smoothstep down to 0
at R. A smoothstep's steepest slope is 1.5 over its width, so once the centre has risen H the
steepest grade is

    1.5 · H / ((1 − core) · R)

A wider or softer brush is a shallower grade; a narrower or harder one is steeper. Measured on the
grid against the formula in §10.

**Grade-locked.** With k the set grade, r the distance from the dab's centre, and
φ(r) = √(r² + ρ²) − ρ a cone rounded over ρ = 2 m at its tip:

    raise:  apex = min(held + rate · dt,  min over rim samples q of  h(q) + k · φ(r_q))
            h' = max(h, apex − k · φ(r))    inside the rim
    lower:  the mirror image, with max and min exchanged

The rim is the disc's outer 1.5 m. Holding the apex by every rim sample puts the cone at or under
the ground on the whole rim, so only samples inside it move.

*held* is the apex the ground about the centre already holds a cone of this grade up to: the least
of h(q) + k · φ(r_q) over the four corners of the cell the centre is in. Over a sample of level
ground that is the ground's height. **Changed by LE1**, from the height under the centre. A cursor
is seldom over a sample, and between four of them the ground under it is their triangles, which lie
under the cone's tip by up to k · φ(0.71) — 7 cm at 30°. A dab raises less than that at any rate
under 4.2 m/s, so a brush that rose from the height under the cursor stood still there.

*No edge ends steeper than max(k, what it was).* For any two samples a and b,
|max(f, g)(a) − max(f, g)(b)| ≤ max(|f(a) − f(b)|, |g(a) − g(b)|). The cone is no steeper than k
between any two points, because |∇φ| < 1. The rim is thicker than a cell's diagonal, so an edge
either has both ends in the disc — where h′ = max(h, cone) at both, the rim included, since the
cone is not above the ground there — or has no end that moved.

Held still on level ground the cone stops when its foot reaches the rim, k · φ(R − 1.5) high —
7.3 m at 30° and 16 m (§10): a set grade and a radius give a height, and a bigger hill at the same
grade is a bigger brush or a moving one. The rounded tip is what keeps its faces at the setting:
sharp, a 45° cone leaves 54.7° faces at its tip, the √2 (§10). The rim leaves a brush R − 1.5 m to
move: at the smallest radius, 2 m, that holds a sample only where the cursor is within 0.5 m of
one, and from 2.21 m — the rim and half a cell's diagonal — it always does.

**Smooth** moves each sample towards the mean of its four neighbours by `w · strength` ≤ 1: a
convex combination, so it never raises the highest or lowers the lowest sample it touches. Every
sample reads the ground as it was before the dab. At the field's edge a sample has its neighbours
along the edge only, and a corner none, so an even slope is left alone there as it is anywhere.
**Level** moves each towards the height under the cursor when the stroke began, by the same share,
and never past it. Neither reads the time step: a dab is `w · strength` however long it stood for,
and how often a stroke dabs is the editor's to set (LE4): 30 times a second, each dab a thirtieth of
a second (§8.3). **Ramp** is two clicks and a width —
twice the brush's radius, the ring drawn: the strip between them is anchored to the straight line
between the ground's heights at the clicks and applied with §6.3, so its sides and its ends are
skirted. A line steeper than the skirt is refused.

| Preset | Family | Radius | Core | Rate or grade | Makes |
|---|---|---|---|---|---|
| Plain | sculpt | 32 m | 0 | 1 m/s | 5.4° once 2 m is raised |
| Hill | sculpt | 16 m | 0 | 2 m/s | 10.6° at 2 m |
| Bank | sculpt | 16 m | 0.5 | 2 m/s | 20.4° at 2 m |
| Bluff | sculpt | 16 m | 0.8 | 2 m/s | 42.1° at 2 m |
| Mound 10°, 20°, 30° | grade-locked | 16 m | — | the grade | ground a body walks, on every face |
| Cliff 60° | grade-locked | 8 m | — | the grade | a wall to bodies and to the bake; not to a 4x4 at speed (LE-D24) |
| Smooth, Level, Ramp | | 8 m | 0 | 0.5 | |

Radius runs 2–64 m, rate 0.25–8 m/s, core 0–0.9, grade 5–70°; a grade-locked preset rises at
2 m/s. Up to 30° a grade-locked brush leaves every face under 40° on ground that was no steeper
than its setting, by the √2: atan(√2 · tan 30°) = 39.2°. 40° is what is walkable (LE-D23); at 35°
the same bound is 44.7°, which LE0 found a body does not walk. Over ground that was steeper an edge
keeps what it had, as the claim above says, and so may a face.

### 6.3 Levelling ground to a shape — `TerrainConform.cs`

One operator serves a prefab's pad, a road's ribbon, a ramp's strip and a bridgehead. Given
anchors — points q of a shape, each with a target height S(q) — and a skirt grade k:

    L(p) = max over q of  S(q) − k · d(p, q)
    U(p) = min over q of  S(q) + k · d(p, q)
    h'(p) = clamp(h(p), L(p), U(p))

d is flat distance. It applies when L ≤ U at every anchor, which is to say when S is itself no
steeper than k between any two of them; a caller whose targets are steeper is refused, as is one
that gives a sample two targets, and nothing is changed. Then:

1. **h′ = S on the anchors.** There L = U = S: no other anchor's cone can pass S(q) when S is no
   steeper than k.
2. **h′ = h wherever L ≤ h ≤ U.** Ground already inside the skirt is not touched.
3. **Between any two samples a and b, |h′(a) − h′(b)| ≤ max(|h(a) − h(b)|, k · d(a, b)).** Each
   S(q) ± k · d(·, q) is no steeper than k (the triangle inequality); max and min keep that
   (§6.2's inequality); and clamp(h, L, U) is max(min(h, U), L).
4. **Applying it twice changes nothing**: h′ already lies between L and U.
5. **It reaches no further than max|h − S| / k from the anchors**: a sample moves only if it is
   further from some S(q) in height than k times its distance from q.

Heights are `float` through it and are rounded with the edit it is part of (§6.1). Rounded,
levelled again and rounded, the ground is what it was after the first rounding: a height that
rounding carried past L or U is put back on that bound, which rounds to the step it came from. In
a document, then, the fourth property holds to the step, which is what §6.5's kept pad stands on.

For a pad S is one number and d is the distance to the footprint less the margin, so L and U are
`level ∓ k · max(0, distance − 1.5 m)`. The distance is measured to the footprint's rectangle
itself, not to the nearest levelled sample: measured from samples, every stair of a rotated edge
is a crease, and 58 of 120 pads left a face over the limit, at worst by the whole √2; measured
from the rectangle the worst face seen was 1.15 times the limit, on 0.4 % of the cells changed
(§10). On level ground it is 1.156 at the most: the skirt is a plane along a side and a cone round
a corner, and the cone's steepest face is the one with a corner where it starts, 1.5 m out (§10).
A node's pad is the same operator with d the distance to its disc.

**The skirt is 30°, and is not a setting.** By the √2 it can leave no face steeper than 39.2°,
under the 40° the bake keeps and the 42° a body and a unit were measured to walk (LE-D23): a unit
and a body walk onto every pad and every road from any side, with no further check. A 35° skirt
could leave 44.7°, which they do not walk.

### 6.4 Texture bands — `TerrainBands.cs`

With e the height above the map's water level in metres and s the slope in degrees:

    under the water (e < 0):  water
    otherwise  u = e + 0.6 · max(0, s − 15)
               sand below u = 1.5,  mud below 4,  grass below 22,  stone above

u does not fall when e or s rises, and a band is how many edges u has passed, so **higher or
steeper never gives an earlier band**: the order asked for holds everywhere. The six numbers —
0.6, 15, the three edges and the blend's 1 m below — and the water level are per map; they are a
map's when each is from 0 to 65.535, which is what the file holds (§6.8), and the edges are in
their order. The table of what they give is in §10: sand and mud are low and gentle, grass is the
middle, and stone is anything high or steep — level ground at 22 m, ground 4 m up at 45°.

**A new map's water is at −5 m**, 5 m under the level ground a new map is (`MapBands.NewMap`).
*Decided by LE2.* With the water at 0 m, as `BandSettings.Default` has it for §10's table, level
ground at 0 m is sand; 5 m over the water it is grass, clear of the mud's blend, which reaches 4.5 m
of u. Water is where the ground is dug 5 m down, and sand from 3.5 m down. The other six numbers are
§6.4's. A document holds the seven as the file does — the water in steps of 1/256 m, the six in
thousandths — and n / 1000 is the float nearest n thousandths, which is what a literal is, so
`0.6` read back is `0.6f`.

`Classify` returns the band and, for drawing, a weight for each of two neighbouring bands across a
blend of 1 m of u: centred on their edge, so each has half on it, and the band is always the
heavier of the two. The water's edge is not blended; the water's own surface is drawn there. The
shader computes the same u from the fragment's height and its normal; the C# function is what the
editor's overlay, a map's preview and the tests use. They need not agree to the bit: nothing but
the picture reads the shader.

### 6.5 Prefabs — `PrefabSpecs.cs`, `PrefabPlacement.cs`

A spec is engine-free: an id, a kind, one or more boxes (centre, half extents, in the prefab's own
frame, as `StructureBox` is), whether it makes a pad, the steepest average grade it accepts, how
many a map may hold, and connectors for a bridge section. Ids are append-only, as
`StructureCatalog`'s are: an id is two bytes in a file somebody else will open. The engine half,
`Scripts/Core/PrefabCatalog.cs`, maps an id to a scene or a builder.

Starting values; LE5 authors them from the scenes, and a test holds each to its scene's colliders.

| Id | Prefab | Kind | Footprint | Pad | Steepest | A map holds |
|---|---|---|---|---|---|---|
| 0 | `spawn_line`: four `player_spawn` markers 4 m apart, and the slots beside them | round | 20 × 6 m | yes | 10° | 1 or more |
| 1 | `weapon_locker` | round | 3 × 3 m | yes | 10° | 1 or more |
| 2 | `vehicle_kiosk`, with `Pad4x4` and `PadHelicopter` | round | 26 × 10 m | yes | 8° | at most 1 |
| 3 | `barracks` (`Scenes/Environment/Barracks.tscn`): building, guns, door and its apron | round | 13 × 13 m | yes | 12° | 1–4 |
| 4 | `resource_node` | round | a disc of its capture radius, 10 m | yes | 15° | 1–8 |
| 5 | `strategist_anchor` | round | none | no | — | at most 1 |
| 6, 7 | `heavy_gun`, `mortar_launcher` | round | 1.5 × 1.5 m | no | 20° | 8 together |
| 8 | `ammo_can` | round | 0.6 × 0.6 m | no | 20° | at most 12 |
| 9, 10 | `cover_low_wall` 4 × 1.2 × 0.8 m, `cover_block` 3 × 3 × 3 m: `CSGBox3D` in group `cover` | cover | their size | yes | 25° | 96 together |
| 11 | `bunker` (`Scenes/Environment/Pillbox.tscn`) | scenery | 30 × 30 m | yes | 12° | — |
| 12–14 | `structure_pillbox`, `structure_sandbag_wall`, `structure_sniper_tower` | structure | `Structures/*.tres` | yes | 8° | 16 together |
| 15, 16 | `bridge_span`, `bridge_ramp` (§6.7) | bridge | 12 × 6 m | no | — | — |

The resource node levels its whole capture radius because the pad it draws is a flat disc of that
radius (`ResourceNode.cs:169`) and a body counts as on it within 4 m of its height
(`Capture.cs:32`). A structure is put up on the first server tick, and again whenever the field is
cleared for a new round, through `ServerPlaceStructure` — free, finished, the strategist's, and as
destructible as one a builder made. Sixteen leaves builders the other half of
`SimConfig.MaxStructures`.

*Set by LE2*, in `PrefabSpecs`, where the table does not say. A box stands on the object's point,
centred on it, and is as tall as what it keeps clear: a spawn line 2 m, a locker 2.2 m, a kiosk 3 m,
a barracks its 6 m building over all of its 13 × 13 m, a gun 1.6 m and a launcher 1.2 m, a can
0.5 m, the bunker its 7.2 m. A node's box is its pole and ring, 1.2 × 1.2 × 3 m: its disc is ground
to stand on, and cover may go on it. The structures' boxes are `Structures/*.tres`' and the
barracks' door and ring are `Barracks.tscn`'s — its `SpawnPoint`, 6 m along its −Z, and its guns'
7.1 m off its centre and 100 m of reach, 107.1 m — and a test reads each from its file; the other
scenes are LE5's to hold the catalog to. A bridge section's point is the middle of its deck in plan,
at the deck's top at its −Z end; its connectors are there and at +6 m along Z, 1.5 m higher on a
ramp; its boxes are the deck and its two rails, a ramp's in four steps. Parameters are a key and a
value, and a key a prefab has not, or a value outside its range, is the validator's error: a node's
perk (key 0, none or drones) and capture radius (key 1, 5–15 m in cm, 10 m unless set); a section's
supports (key 0, 0 or 1) and its piers at its −Z and +Z ends (keys 1 and 2, 0–81 m in cm). No flag
is defined.

**The checks**, in order; the first that fails is the answer the ghost shows.

| Result | When |
|---|---|
| `LimitReached` | the map already holds as many of the kind as it may |
| `OutOfBounds` | a box leaves ±120 m, or a round's piece has its origin outside ±115 m |
| `InTheWay` | a box overlaps a box of another object or a pier, in plan by more than 0.05 m and in height (LE-D11); or a pad's prefab stands on a road's ribbon (LE6: the ribbon is `RoadPath`'s) |
| `TooSteep` | the average grade under the footprint is over the spec's |
| `InWater` | the pad's level, or a round's piece, is under the water |
| `DisturbsNeighbour` | levelling would move a sample inside another object's pad |
| `BlocksDoor` | a structure within 10 m of a barracks door (`StructurePlacement.DoorClearanceMeters`), or a barracks whose door would be |
| `Ok` | the object and its pad are applied, as one step to undo |

**The average grade and the pad.** Fit `h = a + b·x + c·z` by least squares to the samples under
the footprint. (b, c) is the average grade as a vector; atan√(b² + c²) is what the ghost shows and
what the spec's limit is checked against. A least-squares plane passes through its samples'
centroid, so its height there is their mean: that is the pad's level. Levelling to the mean cuts as
much as it fills under the footprint. Then §6.3: every sample within 1.5 m of the footprint is at
the level, exactly, and the skirt runs out at 30°. A prefab without a pad stands at the ground's
height under its origin. A footprint over fewer than three samples, or over samples in one line,
takes the triangle under its centre.

*Changed by LE2*, from "rounded to a step": the object's height is the mean to the centimetre, as
the file stores it, and the pad's level is the step nearest that, round(y · 256 / 100) in integers,
where there is never a tie. The level is then a function of what the file holds, and a pad can be
levelled again from the file alone and land on the same step; it is within 7 mm of the mean. An
object stood at its own level to the centimetre has that level again. Whether a padded piece is
under the water is asked of its pad's level, by placement and validator alike: its height to the
centimetre can be 2 mm under a water level its pad is on.

**A pad is kept.** When a stroke or a road ends and its rectangle reaches any pad's level ground,
the pads are levelled again and rounded; by §6.3's fourth property that changes nothing unless the
edit had moved them. So in a document an object always stands on its pad, and the validator's check
of that (§6.9) fails only on a file something else wrote. *Changed by LE2*: every pad is levelled
again, in the order the objects were placed, as placing them did — not only the pads the rectangle
reaches. Pads overlap: one levelled again can move ground that a pad placed after it had set, and
only that pad levelled again puts it back; LE2's test found it, as a whole-field keep after a local
one that moved two samples. Sample by sample each pad is a clamp, which rounding keeps one on the
lattice of steps (§6.3), and clamps composed are a clamp or a constant: doing it twice is doing it
once. No pad moves another's level ground — placing one that would is `DisturbsNeighbour` — so
every object still stands on its pad.

Yaw is free or in 15° steps. Moving an object is taking it up and placing it again: the old pad
stays, as any sculpted ground does.

*Built by LE5.* Turned, moved or given a parameter, an object is placed again where it is in the
file — `PrefabPlacement.Check` with the index it replaces — so the world names it as before; it is
not counted against its group's limit, nor the map's 512, and it is not in its own way or a neighbour
to its own old pad. Placed again as it stands, it is where it was on its pad, or refused as it would
be placed afresh, by a neighbour placed after it whose level ground is in its skirt: never by one
placed before. `Scripts/Core/PrefabCatalog.cs` is the catalog's engine half: an id to a scene or a
builder, what `LevelBuilder`'s switch was. The prefabs' boxes are held to their colliders: the
barracks' building and the bunker's faces by `PrefabSpecsTests`, and every prefab built on a 6° slope
by `editor-prefabs` (§11, LE5). A locker's, a kiosk's, a gun's, a can's and a node's nodes have no
collider of their own; their boxes are the room they keep clear. A structure is put up on the round's
first tick, after the bake, and after every clear of the field (`UnitManager.PlaceMapStructures`), at
its pad's level and turned by its yaw, and checked as any site is: one refused is logged and left
out. Its colliders are inside its boxes and, under them, LE-D21's plinth, 0.5 m deep under its level
pad.

### 6.6 Roads — `RoadPath.cs`

A road is 2–64 control points, a width (4, 6 or 8 m) and a grade limit g (6–15 %, 10 % unless set:
15 % is 8.5°, under the 9.1° a 4x4 was measured to climb from rest, LE-D24). Its corners are rounded
to a radius of at least its width, and the centreline is sampled every 0.5 m. With f(s) the ground
along the centreline:

    m(s) = min over t of  f(t) + g · |s − t|        the highest profile of grade g under the ground
    M(s) = max over t of  f(t) − g · |s − t|        the lowest over it
    z(s) = (m(s) + M(s)) / 2

z is no steeper than g, being the mean of two profiles that are not. **No profile of grade g cuts
or fills less at its worst.** Let E be the largest of |f(s) − f(t)| − g · |s − t|. For any profile
φ of grade g and any s, t: f(s) − f(t) − g|s − t| ≤ (f(s) − φ(s)) + (φ(t) − f(t)), so φ is off the
ground by at least E/2 somewhere. And m ≤ f ≤ M with M − m ≤ E, so z is off it by at most E/2.

An end joined to a connector (§6.7) is pinned: z′ = clamp(z, z₀ − g · s, z₀ + g · s) meets the
connector's height exactly and stays within g, and it is the free profile again from the first
point where that lies within g · s of z₀. Two pinned ends further apart in height than g times
the road's length are refused, `TooSteepToJoin`, with the grade it would take.

The ribbon is the samples within half the width of the centreline; each is anchored to the
profile's height at its nearest centreline point, level across. §6.3 then applies, with the skirt
at 30°, and its condition is checked first. On a straight the ribbon's targets are as steep as the
profile, and 15 % is under tan 30° = 57.7 %. Round a bend the inside edge is shorter than the
centreline, so they are steeper there: on the tightest corner a 10 % road may turn 258° before
they pass the skirt and a 15 % one 212° (§10); past that the road is refused,
`BendTooSteep`. So is one that crosses itself at two heights. Each end is a connector: a point, a
height and a heading.

A road's surface is drawn by the terrain's shader from a coverage value per vertex, computed on a
client when it builds the mesh. It is not in the file and nothing reads it but the picture.

### 6.7 Bridges — `BridgeRules.cs`

| Section | Deck | Rise | Connectors |
|---|---|---|---|
| `bridge_span` | 12 × 6 × 0.6 m | 0 | one at each end, at the deck's top |
| `bridge_ramp` | 12 × 6 × 0.6 m | 1.5 m: 1 in 8, 7.1°, which a 4x4 climbs from rest (LE-D24) | one at the low end, one at the high |

Each has a rail 1.0 m high and 0.2 m thick along both sides. Deck, rails and piers are static
boxes on layer 1 under `Geometry`: bodies, wheels, rounds and the navigation bake meet them as
they meet any wall. A ramp's tilt is in its prefab; its pose is still a point and a yaw.

- **Connecting.** A section being placed takes the pose that puts one of its connectors on the
  nearest free connector within 3 m of the cursor — the same point, the opposite heading. `Tab`
  turns it end for end, which for a ramp is up or down. A road's end is a connector like any other.
- **Free-floating.** With no connector near, the section goes where the cursor is, at the height
  of the last section placed; `PgUp` and `PgDn` move it by 0.25 m.
- **Piers.** A section with supports on has a pier under each end that is not joined to a section
  with a pier there already: 1.2 × 1.2 m, from the deck's underside to 0.5 m under the lowest
  ground sample beneath it. Its length is written in the file. A pier that would stand in another
  object's box is `InTheWay`; supports off is the answer.
- **Landing.** An end within 3 m of the ground in height can be landed: a 6 × 4 m pad at the
  deck's top is levelled beyond it by §6.3, so the ground meets the deck flush.
- **An open end** — neither joined nor within 0.5 m of the ground, the bake's climb — is a
  warning, not an error: a broken bridge is a thing a map may want.

*Changed by LE5*: a ramp's deck and rails are built as sheared boxes — their tops rise with the
deck, their sides are vertical, their ends square at the section's — where they were boxes tilted
with the deck, which overhung the section's ends by the thickness times the tilt's sine: 7 cm under
the deck, 12 cm at the rails' tops, into the boxes of a section joined there. A span is boxes still.
The tools for bridges are LE6's.

### 6.8 The document and the file — `MapDocument.cs`, `MapFile.cs`

The container is DEMOS.md §2's, for its reasons: tagged, length-delimited, little-endian, the
dullest format that could work, written and read through a `Stream`.

```
"GDPYRMAP" (8) · format version (1) · chunk count (1); then chunks: tag (4) · length (u32) · payload

META  map guid (16) · revision (u32) · created, modified (i64, unix s)
      parent guid (16) · parent hash (32) · prefab catalog count (u16)
      name ≤ 48 B · author ≤ 32 B · description ≤ 255 B       each a u8 length, then UTF-8
TERR  width, depth (u16) · spacing (u16, mm) · water level (i16, 1/256 m)
      the six band numbers (u16, thousandths)
      heights: each sample less (left + up − upper-left), as int16, deflated
OBJS  count (u16), then for each: prefab id (u16) · x, y, z (i16, cm) · yaw (u16) · flags (u8)
      parameter count (u8, at most 8) · (key u8, value i32) × n
ROAD  count (u8), then for each: style (u8) · width (u8, dm) · grade limit (u8, 0.1 %)
      point count (u8) · (x, z: i16, cm) × n
```

- Positions and yaw are quantized as the wire's are (`Quantize`). There is no float in the file.
- An object's order in `OBJS` is its order in the world: `LevelBuilder` names nodes by it
  (`Barracks1`, `ResourceNode3`, `AmmoCan07`), so the name-sorted indices of MAPS.md §3 are the
  same on every peer.
- Parameters are per kind: a node's perk and capture radius, a section's supports and pier
  lengths.
- The predictor is exact on a plane, so level and evenly sloped ground deflate to almost nothing:
  a new map's height chunk is 135 B (§10).

**The reader is total.** Chunks come in this order, once each; an unknown tag, a version other
than this build's, a length past the end of the file or past its limit, a height outside LE-D4, or
a heights stream that inflates to anything but width × depth × 2 bytes is a refusal with its
reason, and nothing is kept. `MapLimits`: 1 MiB a file; 512 objects of 8 parameters; 32 roads of
64 points; this plan's reader takes 241 × 241 at 1,000 mm and refuses the rest. Inside those a 1 m
map's file is at most 151,643 B (§10). `MapFileTests` holds the truncated, spliced, oversized and
hostile cases, as `DemoCodecTests` does.

A map's hash is the SHA-256 of its file. A file is not re-encoded to be hashed, sent or cached
(LE-D15).

*Set by LE2.* The heights are deflated in zlib's format — `ZLibStream`, whose Adler-32 is a check
on what inflates, and the format §10's sizes were measured in: a new map's heights are 135 B from
C# as from Python. The six band numbers are, in order, the slope's gain, the slope that costs
nothing, the sand's, mud's and grass's tops and the blend. A file has its four chunks, so its chunk
count is 4; nothing may follow the last, and nothing may be left in a chunk once it is read. Each
chunk's limit is §10's largest — META 424 B, TERR 116,230, OBJS 26,626, ROAD 8,321 — and is checked
before anything is allocated. Text is UTF-8 with no control character, but for a line break in a
description; a parameter given twice is refused. What a map *means* — a count, a place, a pad, a
number in its range — is the validator's: the reader takes a prefab id past the catalog, a road 5 m
wide or a set flag, and the validator refuses them. The writer refuses what the reader would: a
string over its limit, more than 512 objects or 32 roads. `MapDocument` holds what the file holds —
positions in centimetres, yaw in 1/65,536 of a turn, the bands as integers — so a document read is
the document written, and written again is the same bytes.

### 6.9 The validator — `MapValidator.cs`

What `MapSceneTests` asks of a scene, asked of a document; one function for the editor's panel, a
file about to be listed, a file just received and a server at boot.

| | Errors: the map is not listed, hosted or loaded |
|---|---|
| counts | no spawn line; no locker; barracks not 1–4; nodes not 1–8; more than 8 guns and launchers, 12 cans, 96 cover boxes, 16 structures, one kiosk or one anchor |
| ids | a prefab id at or past this build's catalog: "needs a newer build" |
| place | an object outside ±120 m, or a round's piece outside ±115 m; two objects in the way of each other |
| ground | a padded object not on its pad: the samples under it and within 1.5 m not level to 2 cm, or its height off theirs; a round's piece under the water |
| limits | a height outside −16…+64 m; a number outside its range |

| | Warnings: shown, and the map still plays |
|---|---|
| layout | a node or the spawn line inside a barracks' defended ring (MAPS.md §2.3) |
| ground | more than 4 m of relief inside a node's capture radius; a road steeper than its limit after later edits; an open bridge end |
| reach | no path from a barracks door or from the spawn line to a node. The editor's *deep* check, which bakes; not run on a file being listed |

*Built by LE2*, every row but two: a road steeper than its limit after later edits needs a road's
profile, and is LE6's with `RoadPath`, as is `InTheWay`'s road ribbon (§6.5); reach bakes, and is
LE5's. "A number outside its range" is the terrain's size, the water outside LE-D4 or the band
edges out of their order, an object's height outside LE-D4, a flag, a parameter a prefab has not
or outside its range, a road's style, width (4, 6 or 8 m), grade limit (6–15 %), fewer than two
points or one off the field, and a text longer than a file holds. A bridge end is joined when
another section's connector is within 0.1 m of it, or a road's end within 0.5 m in plan; it is
landed within 0.5 m of the ground. An issue names the object it is about, and a second one where
there is one — the object in the way, the barracks — and where it is, for the editor to fly to.

*Reach built by LE5* (`MapValidator.Reach`, `MapEditorRoot.Deep`): from every barracks door — 6 m
along its −Z — and every spawn line, at the height each stands at, to every node, a path asked of a
mesh the editor bakes as a round bakes it (`UnitManager.ConfigureBake`), from the ground, the round's
pieces and the structures' boxes. A walk reaches when its path begins within 3 m of its start and ends
within 3 m of its node, flat, and 2 m in height; one that does not is the warning `Unreachable`, naming
both.

### 6.10 Sending a map — `MapTransfer.cs`

Sender and receiver are state machines over byte chunks, with no socket in them, so what a
receiver does with a short, long, repeated, out-of-order or never-finished stream is a test.

- **Chunks** are 1,024 B of the file behind a 4 B index, reliable, in order. One is a message
  whose only argument is that byte array, which Godot sends raw behind at most 7 B (§3): about
  1,035 B, under ENet's 1,392 B MTU, so one datagram and never a fragment.
- **A sender** sends one chunk a tick to a peer, 61,440 B/s, and at most 256 KB/s across peers. It
  sends only the map it is playing, only to a peer that asked, once a connection, and from the
  bytes it loaded: a file saved again under a running server changes nothing.
- **A receiver** knows the length and the hash before the first chunk. It refuses a length over
  `MapLimits`, a chunk past the length or out of turn, and a stream that stalls for 10 s. At the
  last chunk it hashes, reads (§6.8), validates (§6.9), and only then writes
  `cache/<hash>.map`.
- **An upload's proof** is `HMAC-SHA256(token, nonce ‖ hash ‖ op)`. The nonce is 16 random bytes
  the server sends each connection with its map. The token never crosses the wire, and a proof
  does not replay on another connection or for another file.

---

## 7. The world from a file

### 7.1 Nodes

`Scenes/Maps/MapHost.tscn` holds `LevelRoot` (which extends `WorldRoot`), the sun and environment
`MapBase.tscn` has, `Geometry` in group `navmesh`, and a `NavigationRegion3D` in group
`navigation_region` with an empty mesh. `LevelRoot._EnterTree` builds the rest from
`Session.Document`, then calls `Session.EnterGame()` — after, so no tick sees half a world.

| Built | As | Found by the game as |
|---|---|---|
| `Geometry/Terrain` | `StaticBody3D`, layer 1, with 225 `CollisionShape3D`s, each a `ConcavePolygonShape3D` of 16 × 16 cells' triangles, two a cell as §3 has a heightmap split it, from the samples as `float` (LE-D5). The node frees them when it leaves the tree: LE0's stripped server ended in a segmentation fault when it left that to the runtime's finalizer | world geometry; the bake's source |
| `Geometry/Terrain/Chunk_*` | 225 `MeshInstance3D`, 16 × 16 cells each, on the collider's triangles, with the terrain shader. Clients only | — |
| `Water` | one quad at the water level, when any sample is under it. Clients only | — |
| `Geometry/Perimeter` | four walls as `MapBase.tscn` has, each from 1 m under its edge's lowest ground to 4 m over its highest | world geometry |
| `Geometry/Cover/*` | `CSGBox3D`, `use_collision`, group `cover` | `UnitManager.CollectCover`, unchanged |
| `Geometry/Objects/*` | scenery and bridge sections from the catalog; piers as boxes of their stored length | world geometry |
| spawn lines, lockers, kiosk, barracks, nodes, guns, cans, anchor | the node types, scripts and groups of MAPS.md §3, named by their order in the file | every autoload, unchanged |

A server builds no mesh, no water and no material: `Bootstrap.IsDedicatedServer` and headless both
skip them. Every prefab scene carries its own collision shapes, because a stripped server has no
mesh to make one from (§2, §3).

*Built by LE3*, as the table has it, with these settled. An object stands where the file says,
upright, turned by its yaw; a padded one at its pad's level — the step its stored height rounds to
(§6.5), which is the ground under it to the bit — and anything else at its stored height. A node is
named by its order among its kind in `OBJS`, from 1: `SpawnLine1`, whose markers are `Spawn011` to
`Spawn014`, 4 m apart along its X and 1 m over its pad, as `MapBase.tscn` has them; `WeaponLocker1`;
`VehicleKiosk1`, its cabinet at its middle, `Pad4x4` at (−7.5, 0, −1) and `PadHelicopter` at
(7.5, 0, −1.5) in its frame, each turned so a vehicle faces the way the cabinet does; `Barracks1`, an
instance of `Barracks.tscn`; `ResourceNode1`, its capture radius and perk its parameters;
`StrategistAnchor`; `Emplacement1` and `MortarLauncher1`; `AmmoCan01`; `CoverLow01` and
`CoverBlock01`; `Bunker1`, an instance of `Pillbox.tscn`; `Bridge01`, its deck and rails static boxes,
a ramp's tilted, and a box for each pier of its stored length. A structure the file holds is counted
and left: putting it up on the round's first tick is LE5's, with the catalog's engine half, which
is `LevelBuilder`'s switch until then. *Built by LE5*: the round puts each up through
`ServerPlaceStructure` (§6.5), and `PrefabCatalog` is the switch; a ramp's deck is a sheared box (§6.7). A map's water is drawn where any sample is under it, and only
there is the bake given a floor (§7.3).

### 7.2 Drawing it

The shader computes §6.4's u from the fragment's world height and normal, blends the two
neighbouring bands, and mixes in the road by the vertex's coverage. Sand, mud and grass are
sampled by world x and z; stone is sampled on three planes, since it is what cliffs wear. Five
`sampler2D`, nothing the Compatibility renderer lacks (§3). A uniform switches the editor's
overlays in the same shader.

Placeholders are `ThirdParty/GreyboxTextures`' grids: yellow sand, orange mud, green grass, grey
stone, and solid blue for the water's quad. `Levels/materials/terrain_palette.tres` names the five
textures and their scales; replacing art is replacing that resource (LE-D22).

*Built by LE3* (`Levels/materials/terrain.gdshader`, `Scripts/Level/TerrainPalette.cs`). The five
`sampler2D` are sand, mud, grass, stone and the road; the palette's five textures are the four bands'
and the water's, and the road's is LE6's to add with its coverage, which is zero in every vertex's
red until then. The overlay uniform takes none, the bands flat-coloured, the slope of each face —
green under 35°, yellow to 40°, red past it — and contours every 2 m; *buildable* is LE5's. The five
placeholder textures are imported with mipmaps, which they were not: at a distance their 1,024-pixel
grids, 4 m a repeat, drew as white speckle without them. Pictures of the bundled map drawn under a
virtual display are in `docs/baselines/` (§11, LE3); nothing in them is a check of how it looks.

### 7.3 Navigation

`BakeNavigation` changes in two things on a data map: its slope limit is 40° (LE-D23), and on a
map with water it sets the bake's box, with its floor 1 m under the water level (LE-D8, §3). The
bake's grid is the 1,200 × 1,200 columns it already is for a 240 m map. What terrain adds is
115,200 source triangles where a slab was 12, and a surface that is not one polygon.

**LE0 measured what that costs** (`docs/baselines/le0-67fec82-checks-1.txt`): the greybox on its
slab, then on level terrain, then on §10's hills. The two colliders bake alike, to the same mesh;
the ranges are over both.

| | Slab | Level terrain | Hills |
|---|---|---|---|
| Source geometry | 636 triangles | 115,808 | 115,808 |
| The mesh | 565 vertices | 533 | 14,716 |
| The first tick's bake, all of it on the tick | 0.86–1.03 s | 0.48–0.56 s | 2.0–2.3 s |
| of which reading the geometry | 7 ms | 14–21 ms | 14–15 ms |
| A re-bake around a structure, on the tick | 0.2–0.3 ms | 6.4–10.6 ms | 6.4–7.2 ms |
| until the new mesh is in | 0.8–0.9 s | 0.5–0.8 s | 2.0–2.2 s |
| One 48 m tile, from geometry read once and kept | 19 ms | 23–29 ms | 126–129 ms |

The slab's own bake is the slowest of the level two because its floor is 300 m across, not 240 m.
The first tick is before anybody is connected, as it is now. A re-bake puts 6–11 ms on a 16.7 ms
tick, once a structure, and units path on the old mesh for 2 s on hills where it was under 1 s:
they "walk into a new wall and slide along it", the fallback the re-bake was written with
(`UnitManager.Structures.cs:1021`). **Neither is too much to build LE3 on, and nothing here is
built ahead of need.**

*Built by LE3*: `UnitManager.BakeNavigation` asks `LevelRoot.Current`. On a data map the slope is
`MapLimits.WalkableSlopeDegrees`; where any sample is under the water, `filter_baking_aabb` is the
field and 1 m past it, from 1 m under the water to 40 m over LE-D4's highest ground. On a scene map
nothing changes. The bundled map bakes to 2,348 vertices; dug 3 m under its water in a disc of 12 m,
no vertex is under the floor and the mesh's closest point to the bed is on the bank (§11).

If the 2 s shows — units held up at a fresh structure in a playtest — the order is the reverse of
the one this section had before LE0: **tiles first**. A tile is baked from geometry read once and
kept, with `filter_baking_aabb` and `border_size` (§3), and the tile a structure stands in takes
0.13 s on hills, not 2 s. Keeping the geometry without tiles only takes the 6–11 ms off the tick,
and tiles need it kept anyway.

### 7.4 Every consumer

| Consumer | On terrain | Change |
|---|---|---|
| Bodies and units (`MoveAndSlide`) | walk faces to 42° and stall on them from 43°: the engine's 45° less what a capsule's contact with the next triangle adds (§3). A box they walk to 45°. Slower up, as fast down: 5.0 m/s on the level, 4.4 up 20°, 5.0 down it. Behind an 80 ms link a body crossing a 20° slope is corrected up to 2.5 times a second, by 10–16 mm, where on the level it is not (§10) | the bake's slope (LE-D23); the correction's threshold (LE-D25) |
| Rounds, blasts, sight lines, the fog | stop at the ground: a hill hides and shelters. On the collider none of 10,000 rays 16–200 m long passed through it; on a heightmap 36 did over level ground and 29 over hills (LE-D5) | none |
| The navigation bake | reads the collider; leaves out faces over its slope and deep water. At 45° it kept a 44° face no unit climbed | the bake's slope and box (§7.3) |
| The 4x4 | its wheel rays follow the ground; its hull, which is level whatever its chassis does, does not (§2). From rest at the foot it climbs 9.1° and stops on 10.0° with its wheels off the ground, as a level hull with 0.25 m under it would. At 18 m/s it rides up a face of anything to 70°, off its wheels for 1.4–2.6 s, and is stopped by 75° (§3). Onto 10° at that speed it is off its wheels for 2.6 s. Parked, it stays put on 5° | LE-D24: roads to 15 %, and a flag |
| The helicopter's height ray and its 25 m ceiling over the ground | follows the ground. Over the hills with the climb held, 19.8–27.7 m over it: 2.7 m past its ceiling where the ground falls away. Flown at them from 5.7 m with nothing held it is carried over, 0.9 m clear at the least, and is neither slowed nor put down | none |
| The dropship: cruise height by ray, landing sites | follows the ground. It will not land on a deck, which has ground under it (`IsLandingSite`) | none |
| The drone's floor (−20 m) and ceiling (120 m); the kill plane (−50 m); the agent's rays from 80 m | all outside LE-D4's limits | none |
| A builder's structure site | a pillbox is refused as `Blocked` past 4.5–6.2°, a sandbag wall across a slope past 5.8°, a sniper tower past 8.4–11.5° (§7.5) | LE-D21, §7.5 |
| The strategist camera | height limits from the anchor's y; a cursor that falls back to the plane y = 0 | held 4 m over the ground under it, by a ray each frame; the fallback plane goes through the anchor, on every map. *Built by LE3* |
| Cover, spawn slots, lockers, kiosk pads, node discs | stand on pads | none |
| A capture pad's 4 m vertical tolerance | holds on a pad | none; the validator warns past 4 m |
| The mortar launcher's level plane | the shell still meets the real ground; the ring drawn is off by the ground's difference in height | none (§14) |
| A barracks' defended ring | a hill between shortens it | none; the validator's warning stays flat distance |
| `FlightTraits`' "ground … under 30 m" | ground to 64 m and cruise to 100 m: 164 m, inside ±327.67 m | the comment. *Changed by LE3* |
| HTN planners, bot steering | flat distances and the navigation mesh. LE0 played ten rounds of 6 + 1 bots on hills: units trained, nodes taken and structures built as on the slab (§10) | none |
| The server's tick | 3.5 ms on the slab with a round of bots, 5.4–5.9 on level terrain, 7.8–9.1 on hills, where 1 tick in 20 takes over 14–17 ms of its 16.7 and 1 in 100 over 20 (§10) | none decided: a risk (§13). LE3 measured the bundled map against the greybox on the same VM: 4.6–5.5 ms against 2.4–2.9, and under 8.9–11.5 ms for 99 in 100 (§10); the box is a person's |

### 7.5 A structure on a slope

Today the placement probe's underside is 0.2 m over the ground under a site's centre (§2). The
ground enters it where it rises 0.2 m across the probe's half-width: for the pillbox, 1.75 m,
that is atan(0.2 / 1.75) = 6.5° across a side and 4.6° across its diagonal. Past that the answer
is `Blocked`, "something is in the way", and short of it the downhill edge hangs up to 0.2 m over
the ground.

LE0 measured it, grade by grade, through `ServerPlaceStructure`: a pillbox is taken up to 6.2°
across a side and 4.5° across its diagonal and refused from 6.7° and 4.9°, as `Blocked`, and the
downhill edge of the last one taken is 0.20 m in the air. A sandbag wall is refused from 6.2°
across its length and never, to 17°, laid along the slope; a sniper tower from 11.9° across a side
and 8.9° across its diagonal, its edge 0.33 m up. Through `ServerConstruct`, the computer
strategist's way in, with a builder and the mesh baked: on 5° only the pillbox turned 45° is
refused; on 10° the pillbox at any turn, the wall unless it lies along the slope, and the tower
turned 45°. Every refusal is `Blocked`; none is `OffNavigation`.

LE-D21: sample the ground under the footprint's centre and corners. If the highest is more than
0.5 m over the lowest, `PlacementResult.TooSteep` (11, appended), "the ground is too steep". Else
the base is the highest, and the body's box runs down to the lowest. 0.5 m is 5.6–7.9° under a
pillbox, by direction, and 7.1° along a sandbag wall. On level ground nothing changes. The editor's
*buildable* overlay shows where the rule holds.

*Built by LE3* (`StructurePlacement.CheckGround`, `UnitManager.SiteGround`). The centre's ground is
the probe's ray as before; each corner's is a ray down from 0.55 m over it to 0.55 m under it, so a
corner not found is one too far from the centre to be taken, and a ray that starts inside something
standing on a corner — a cover box, a building — does not meet it and finds the ground under it.
The plinth is the body's plan from 0.5 m under the base to the base, for every site: a site the
rule takes has no ground under it lower than that, so the gap is closed whatever the slope, and every
peer builds the same body from the kind, the point and the yaw with nothing new on the wire. On level
ground the plinth is under the ground. It is world geometry and not part of the structure's hit
shape, so a round that meets it stops there as one that meets the ground does, and does the
structure no harm. `StructureGroundTests` holds the rule's limits; LE0's `slopes` part, run again,
holds the engine to them (§11). Through `ServerPlaceStructure`, a pillbox is taken to 7.57° across
a side and 5.36° across its diagonal and refused from 8.00° and 5.80°, as `TooSteep`, where 0.5 m is
7.91° and 5.61° and the steps are 0.45°; a sandbag wall to 7.13° with the slope rising along its
length and to 17.35°, the most tried, with its length along the contour; a sniper tower to 8.88° and
6.24°. Each stands at most 0.50 m over the lowest
ground under it. Through `ServerConstruct` with a builder and the mesh baked, every structure at
every turn is taken on 4.9°, and on 10.0° every one is refused as `TooSteep` but the sandbag wall
laid along the contour.

*Buildable built by LE5* (`BuildableGround`, the terrain shader's overlay 4): a vertex is buildable
where the rule takes a pillbox, the widest structure, centred on it both square to the field and
turned 45°, on the ground the collider has — at most 5.6° of even slope, the 45° turn's limit. The
editor computes it into each vertex's colour while the overlay is up, and draws again three samples
past a change, the rule's reach; a round draws no overlay and computes none.

---

## 8. The editor

### 8.1 Entering it

**Map editor** on the main menu, or `--edit [name]`. It lists one's own maps, and anything in the
library to clone. The editor is a scene whose root is not a `WorldRoot`: the session stays in its
menu phase, and the one node that ticks the simulation is gated on that
(`Scripts/Net/PlayerManager.cs:313`). It holds a `MapDocument`, a `TerrainNode` and a
`LevelBuilder` — the ones a round uses — so what is edited is what will be played.

*Built by LE4* (`Scenes/MapEditor/MapEditor.tscn`, `Scripts/MapEditor/MapEditorRoot.cs`). **Map
editor** is a button on the menu's row; `--edit` alone is the editor's list, and `--edit <name>`
opens one's own map of that name or, when there is none, makes a new one by it (`LaunchOptions`,
which refuses it beside a mode, `--practice`, `--map`, `--playdemo`, `--record` or `--spectate`, and
on a dedicated server). The menu acts on it once; leaving the editor comes back to the menu. The
list holds every map of one's own that reads, with what the validator finds in it and whether an
autosave is beside it — a map with errors in it is listed, to be finished, and one that does not
read is listed with why — then the bundled maps and the received ones, to clone (`MapStore.List`).
A clone is saved at once under its new name, revision 1, with the source's GUID and hash for its
parent and the source's name and author kept until somebody types others. The round's objects are
built under a node that is not processed, as `LevelBuilder` builds them for a round, and built again
when an edit changes them or the ground along the field's edge, which the walls span.

### 8.2 The screen

Built in code, as the menu and the HUDs are. A bar across the top: the map's name and whether it
is saved, Validate, Test, Save, Save as, Export, Exit. A rail of tools down the left; the selected
tool's settings on the right; under the view, the cursor's position, height, slope and band, and
the count of validator issues. The camera is the spectator's fly camera
(`Scripts/Ui/Spectator/SpectatorCamera.cs`), with `T` for straight down.

| Key | |
|---|---|
| `1` `2` `3` `4` | terrain, prefabs, roads, bridges |
| `[` `]` · `-` `=` · `,` `.` | brush radius · rate or strength · core or grade |
| `R`, `Shift+R` · `G` | turn a prefab 15° either way · snap to the metre grid |
| `Tab` · `PgUp` `PgDn` | turn a bridge section end for end · raise or lower it |
| `Del` | take up what is selected |
| `O` | next overlay |
| `Ctrl+Z`, `Ctrl+Y` · `Ctrl+S` · `F5` | undo, redo · save · test |

Which settings a tool shows, which key does what, and what a result reads as are engine-free and
tested, as `StrategistCards` is; the widgets are not.

*Built by LE4* (`MapEditorRoot.Ui.cs`, `MapEditorControls.cs`). The bar has **Maps**, back to the
list, beside the others, and the title opens the map's name, author and description. Prefabs, roads
and bridges are on the rail and off, each saying whose phase it is; their keys say the same. `Esc`
drops a stroke under way or a ramp's first click. The camera is `SpectatorCamera` with two settings
the editor turns on: it looks on the middle button, since the right one lowers the ground, and a held
Ctrl moves it nowhere, so that `Ctrl+Z` is not a step back and down — `Q` lowers it, and `E` or
`Space` raises it. `T` looks straight down from where it is. `MapEditorControls` holds the keys of
the table and what each does, Shift being the camera's and leaving a key without a Shift of its own
as it is; which settings a brush shows — sculpt its radius, core and rate, grade-locked its radius,
grade and rate, smooth and level their radius, core and strength, a ramp its radius as half its
width — and that `[` `]` scale the radius by 1.25, `-` `=` the rate by 1.25 or the strength by 0.05,
and `,` `.` the core by 0.05 or the grade by 5°, each held to its range; and the status line.

### 8.3 Terrain

The presets of §6.2 as buttons, their numbers as sliders, raise on the left button and lower on
the right. A ring on the ground shows the radius; a readout shows the steepest face under it,
coloured as the slope overlay is. The water level and the six band numbers are sliders that
re-draw as they move.

A dab re-meshes the chunks its rectangle touches, and one sample further for their normals: at
most (⌈(2R + 2) / 16⌉ + 1)² of them, 9 at an 8 m radius and 36 at 32 m, of 289 vertices each. The
chunks of the collider it touched are given their triangles when the stroke ends, 0.2–0.3 ms
each (§10): nothing in the editor asks it during one.

Overlays: none; bands, flat-coloured; slope — green under 35°, yellow to 40°, red past it
(LE-D23), on faces; buildable (§7.5); contours every 2 m.

*Built by LE4*, but for *buildable*, LE5's. The cursor is §6.1's ray, not the physics server's,
which a stroke has not told yet. A stroke dabs 30 times a second, each dab a thirtieth of a second,
and at most eight in a frame, so that a held brush makes the same ground at any frame rate and a
stalled frame does not lay its backlog down at one point (`MapEditSession`); a click is one dab. At
its end the rectangle it touched is rounded, every pad kept (§6.5), and what changed is one step to
undo; a stroke that changed no stored sample is not one, and leaves the map saved. A dab draws again
the chunks with a vertex on or next to a sample it moved — next to, for the normals — and the end
gives the collider's chunks the samples that changed. The ring is a line on the ground, coloured as
the readout is; the readout is the steepest face of the cells whose middle is under the brush. The
water's quad comes and goes as ground goes under the water or the slider moves. The sliders are not
steps to undo; sand's top is held under mud's, and mud's between sand's and grass's. On this VM,
headless, with nothing else running, a frame of a stroke took 0.23 ms on average and 1.16 ms at the
most; a stroke's end 12.1 ms at the most, its collider chunks 3.6 ms of it (§11, LE4).

### 8.4 Prefabs

A palette by kind. The selected prefab follows the cursor as a ghost, drawn where it would stand
on its pad: green, or red with §6.5's reason beside it and the average grade under it. A click
places it and its pad. A click on a placed object selects it: turn it, take it up, or edit its
parameters. Counts sit beside each palette entry against their limits (`barracks 1/4`).

*Built by LE5* (`MapEditorRoot.Prefabs.cs`, `MapEditSession`, `MapEditorControls`). The palette is a
round's pieces, cover, structures standing at the start and scenery, one entry a row; bridge sections
are LE6's tool's. A group a map must hold says so while it holds none (`barracks 0/4, needs one`). The
ghost is the prefab's boxes, at its pad's level, and §6.5's answer is asked again only when the
cursor, the yaw, the prefab or the map moves. A click picks the smallest thing under it — a box, else
a footprint, else a marker within 2 m — so cover on a node's disc is picked over the node. `R` and
`Shift+R` turn the selected object, or else the ghost, to the next 15° step; `G` snaps to the metre
grid; `Del` takes the selected object up; `Esc` or the right button drops the selection. A node's
perk and capture radius are sliders. The map's structures are drawn and solid in the editor as their
boxes, which the round puts up itself.

### 8.5 Roads and bridges

A road is clicks: each adds a point, the ribbon and its profile are previewed with the deepest cut
and fill, `Enter` applies it and `Esc` drops it. A road's end near a connector joins it. A placed
road can have its points moved; applying again grades the new line and leaves the old line's
ground as it is.

A bridge is §6.7: pick a section, toggle supports, click. Free connectors are drawn as rings, and
the one a section would take is lit.

### 8.6 Undo, and saving

Every edit is a command with its inverse: a stroke keeps the rectangle it touched, before and
after; an object keeps its record and its pad's rectangle. Two hundred steps; a stroke over a
64 m square is 17 KB.

*Built by LE2*: `MapEdit` and `EditHistory`. An edit is a rectangle of stored samples before and
after, and objects put in, taken out or replaced at their indices; it is applied forwards and
reverted backwards, objects first, so an edit that takes one out and puts another in comes back in
order. A stroke is pushed once it has been made, since it changes the ground as it goes; a placement
is done through the history. A new step drops what could have been redone, and an edit that changes
nothing is not a step.

Save writes `name.map.tmp` and renames it over `name.map`, keeping the last as `name.map.bak`, and
bumps the revision. An unsaved map is autosaved to `name.autosave.map` every minute, and offered
back when the editor next opens it.

*Built by LE4* (`MapStore`). The last `name.map` is copied to `.bak` before the rename, so a whole
map is under the name at every moment; a save that cannot be written leaves the document's revision
and time as they were. Errors in a map do not stop it being saved. An autosave is due a minute after
the first change not yet written, saved or autosaved, and every minute while changes come; it is
written the same way, at the revision it has. Opening a map with an autosave beside it offers it —
taken back, the map is as autosaved and not saved; turned down, it is dropped — and an autosave that
is the map as saved is dropped and not offered. A save drops it, and so does leaving without saving.
Leaving with changes not saved asks first: save, leave, or stay.

### 8.7 Validate and test

**Validate** runs §6.9 and lists what it finds; a click on an issue flies to it. **Deep** also
bakes the navigation mesh in the editor and checks reach. Errors do not block saving: an
unfinished map is a file. They block listing, hosting and sending.

**Test** saves and starts a second process (LE-D19): `OS.CreateInstance` with `-- --practice
--map <path>`, and `--path <project>` before that when this is not an export. It is the practice
round the menu offers, on this map, and closing it returns to an editor that never left.

*Built by LE4*, but for **Deep**, which LE5 built (§6.9): a button on the validator's list, whose
answer the list and the count show while the map is as it was asked of. The validator's count is under the view at all times, asked
again only after a change; **Validate** lists every issue, and a click on one flies the camera to it.
**Test** saves only what is not saved — a save is a new revision, and a map saved and unchanged is
the file already — and a map the validator finds an error in is saved and not played, with the list
shown, since the second process would refuse it. From a headless editor the second process is
started `--headless` as well.

---

## 9. Maps on disk and on the wire

### 9.1 The library — `MapLibrary.cs`

| Place | Holds | Written by |
|---|---|---|
| `user://maps/<name>.map` | one's own maps | the editor; import |
| `user://maps/cache/<hash>.map` | maps received | a fetch; an upload, on a server |
| `res://Maps/<id>.map` | maps bundled with the build, each with a `MapCatalog` entry | the repository |

A name is `[a-z0-9_-]`, 1–48, parsed by one engine-free function as a demo's is
(`DemoFormat.TryParseName`); it is the only place a typed string becomes a path. `user://` is read
with `System.IO`, as `DemoFiles` does. A bundled map is inside the PCK and is read with Godot's
`FileAccess`; the export preset's `include_filter` becomes `*.map`, since a file that is not a
resource is exported only by that filter (§3). LE3's check loads one from the exported server.

`MapInfo` gains a kind (scene or data), a hash and a path. For a data map
`Session.WorldScenePath` is `MapHost.tscn` and `Session.Document` is what `LevelRoot` builds;
`HostSettings` holds a `MapInfo` where it held a catalog id. `MapCatalog` lists scene maps and
bundled data maps, greybox first, as its tests require. `MapSceneTests` runs over the scene maps;
`BundledMapTests` runs `MapValidator` over the bundled ones. The host panel's picker lists the
catalog, then the library.

*Built by LE3*, but for the library in the picker and `HostSettings`' `MapInfo`, which are LE7's:
until a client can fetch a map, a host offers what its clients have, the catalog, and the catalog's
id is still what `HostSettings` holds. `MapLibrary` reads a file — the pack's with `FileAccess`,
anything else with `System.IO`, at most 1 MiB — hashes it, reads it (§6.8) and validates it (§6.9),
and refuses it with every error's reason; a bundled map whose bytes do not hash to its catalog entry
is refused as not the file the build was made with. `Session.TrySelectMap` does that for a data map
when it is selected — from the host panel, as a client told its server's map, or by `--map` — so a
map that does not play is said so before the port is opened or the scene is changed. `MapNames` is
the one parser of a name and of a reference (§9.5). The one bundled map is `hills`,
`res://Maps/hills.map`: `gdpyr-map generate --seed 3 --name Hills`, 27,299 B, SHA-256
`6d0aa3ff…`, which `BundledMapTests` makes again from the generator byte for byte. The preset's
`include_filter` is `*.map`, and a `.map` is binary to git (`.gitattributes`).

### 9.2 Save, clone, export, import

| | |
|---|---|
| Save | §8.6. Same GUID, next revision, a new hash |
| Clone | any map in the library: a copy with a new GUID, the source's GUID and hash as its parent, in `user://maps/` under a new name. It is how a received or bundled map becomes one's own |
| Export | copies the saved file to a path chosen in a file dialog. The file is the whole map. `Show in folder` opens `user://maps/` |
| Import | reads and validates a chosen file, and copies it into `user://maps/`. A GUID already there asks: replace, or import as a clone |

The author is what was typed into the map's settings. It is never taken from the machine's account
name: the file is going to strangers.

Console (`Scripts/Core/ConsoleCommands.cs`): `maps`, `mapinfo <name>`, `clonemap <name> <new>`,
`exportmap <name> <path>`, `importmap <path>`. They are how a headless process does all of this.

*Built by LE4*: save; clone, from the editor's list; **Save as**, which makes a map that has a file a
fork of it — a new GUID, with the GUID and hash of the file it was saved as for its parent, saved as
revision 1 — so that two files never share a lineage, and saves one that has none under the new name; export, which saves
first what is not saved; and **Show folder**. Import and the console's commands are not built: no
phase of §11 names them, and they wait for the library's picker, LE7's.

### 9.3 Hosting and joining

`ServerMap` becomes `ServerMap(id, hash, length, name, nonce)`. The id is the catalog's, and
empty for a map from the library. For a scene map the hash is empty and everything is as LAN.md
§3.4 has it. For a data map the client:

1. has the hash in its library or cache → validates it against its own build, selects it, loads;
2. has not → `ClientRequestMap(hash)`; the menu's status line reads `fetching 'ridge' 47 / 114`;
   the chunks arrive (§6.10); the file is hashed, read, validated, cached; then as 1;
3. is refused by its own validator — a prefab id past its catalog — or by the transfer → the menu
   says why and drops the link, as it does for an unknown scene map. `--client` logs it and exits 1.

While it fetches, a client is connected and held off the field as anybody who has not picked a
side is; its role request is kept (§2). The 5 s wait for `ServerMap` itself is unchanged.

Both ends still want the same commit (LAN.md §5). A map moves between builds whose prefab catalogs
agree up to the ids it uses, and the file says how many it was made against.

### 9.4 Messages

| Message | Direction | Transfer | Rate | ~Size |
|---|---|---|---|---|
| `ServerMap` (changed) | S→C | Reliable | once a connection | ~16 B for a scene map; ~100 B for a data map |
| `ClientRequestMap` | C→S | Reliable, channel 1 | once a connection | ~40 B |
| `ServerMapChunk` | S→C | Reliable, channel 1 | 60 Hz while a peer fetches | ≤ 1,035 B |
| `ClientOfferMap` / `ServerMapOffer` | C→S / S→C | Reliable, channel 1 | per upload | ~120 B / ~4 B |
| `ClientMapChunk` | C→S | Reliable, channel 1 | 60 Hz during an upload | ≤ 1,035 B |
| `ServerMapStored` | S→C | Reliable, channel 1 | per upload | ~40 B |
| `ClientSetNextMap` / `ServerNextMap` | C→S / S→C | Reliable, channel 1 | per request | ~70 B / ~40 B |

Channel 1 is `[Rpc(TransferChannel = 1)]`. `TransportFactory` is not touched: a host made with the
default channel count has 255, and `create_server`'s `max_channels` argument must stay unset, since
at 4.6 it lands in the incoming-bandwidth position (§3). A fetching peer costs 61 KB/s over the
21 KB/s of NETCODE.md §7 for at most 2.5 s.

### 9.5 A dedicated server

- **`--map <ref>`**, for `--server`, `--listen` and `--practice`: a catalog id, a library name, a
  hash's first 8 or more hex digits, or a path ending `.map`. A reference that resolves to nothing,
  or to a file the validator refuses, is a fatal start-up error with its reasons, as every other
  launch error is. *Built by LE3*, with `--practice`, the menu's practice round with no menu: tried
  in that order, a path being a path and nothing else; a hash's start that two maps share names
  neither; with `--client`, or with no mode, `--map` is refused. A map from the library is played with
  an empty catalog id, and a client told an empty id says the host's map is one it cannot fetch yet
  (LE7).
- **`scripts/deploy-map.sh <file> [--activate]`** copies a map to the box's `user://maps` —
  under the unit's `HOME`, by Godot's default
  `/var/lib/gdpyr/.local/share/godot/app_userdata/gdpyr/maps/`, inside the unit's one writable
  directory; LE8's check prints the path the server resolves — and with `--activate` writes its
  hash to `maps/active` and restarts the unit. A server started with no `--map` plays
  `maps/active` if there is one, else the catalog's default. `tools/Gdpyr.MapTool validate <file>`
  runs §6.8 and §6.9 before anything is copied.
- **Upload**, only on a server started with `--map-upload-token <token>`; the deploy unit does not
  pass it. **Publish to server…** in the editor asks for an address and the token, connects, and
  offers the map with its proof (§6.10). The server answers `ok`, `have_it`, `uploads_off`,
  `bad_proof`, `too_large` or `busy`; takes one upload at a time; and hashes, reads and validates
  before it writes to its cache.
- **Changing the map.** `ClientSetNextMap(hash, proof)` writes `maps/active`. When the round in
  progress ends, the process exits 0 and its supervisor starts it on the new map; everybody is
  dropped and joins again. The deploy unit restarts it (`Restart=always`). A server run by hand
  stays down, and says so when it accepts the request.

### 9.6 Demos, the agent API, scenarios

- A demo's header names a data map as `map:<hash>`, and playback loads it from the library or the
  cache or says which hash it lacks. `DemoFormat.Version` 6: a version-5 reader would take the
  string for a scene path. *Until LE7*, a demo recorded on a data map names `MapHost.tscn`, and is
  played back on the catalog's default, as a demo of any map is: playback does not read the name.
- The agent's `welcome` gains `map: { id, name, hash }`. The observation is unchanged: positions
  still scale by 256 m, and the passability plane is still rays.
- A scenario file gains an optional `"map"`, a catalog id; the harness passes it as `--map`.
  Scenarios without one play the greybox, as now. *Built by LE3*, with two:
  `hills_strategist_economy.json` and `hills_spawn_points_shared.json`, the scenarios of those names
  with `"map": "hills"`.

---

## 10. Numbers

`python3 tools/level_editor_checks/design_checks.py` prints every table here; `numpy` only, seeded.

**The grid.** 58,081 samples, 57,600 cells, 115,200 triangles, 225 mesh chunks. One step is
3.90625 mm, and every `int16`/256 is exact as a `float`: |q| ≤ 2¹⁵ needs 16 bits of a 24-bit
significand.

**The height chunk**, on synthetic terrain. Hand-sculpted ground is smoother than noise and should
do better; the bound that matters is the raw size.

| Terrain | Raw | Deflate | Predict, then deflate |
|---|---|---|---|
| flat: a new map | 116,162 B | 135 | 135 |
| rolling hills | 116,162 | 97,160 | 70,626 |
| the same, with six pads and a road | 116,162 | 89,656 | 66,651 |
| rugged | 116,162 | 103,183 | 88,209 |

**Sending one**, at a chunk a tick: the heights stored raw are 114 chunks and 1.89 s; the largest
above, 87 and 1.44 s; a new map, one chunk. The largest file a 1 m map can be, with deflate's
bound on what it cannot shrink and every limit of §6.8 reached: 10 + 32 + 424 (`META`) + 116,230
(`TERR`) + 26,626 (`OBJS`) + 8,321 (`ROAD`) = 151,643 B, 149 chunks, 2.47 s. A file at the 1 MiB
limit is 17.1 s.

**LE2's files**, written by `MapFile` and measured by `gdpyr-map` (`docs/baselines/le2-ceaaec9-maptool.txt`).
`MapFileTests` holds the chunk limits above as `MapFile`'s constants, and a file at every limit at
or under 151,643 B: 151,053 B, its heights noise inside LE-D4, which deflates to 115,620 B, a little
under raw. A flat map's heights deflate to 135 B, §10's number. The generator's maps, seeds 1–8:

| | Heights | File | To send | Ground | Steepest face | Objects | Validator |
|---|---|---|---|---|---|---|---|
| seeds 1–8 | 26,266–26,822 B | 26,867–27,403 B | 27 chunks, 0.44–0.45 s | −3.8 … 10.9 m | 33.2–35.6°, none over 40° | 33 | no error, no warning |
| seed 1, `--hills 0` | 135 B | 736 B | 1 chunk | 0 m | 0° | 33 | no error, no warning |

**LE3's world**, the bundled map (`hills`: −1.53 … 10.57 m, steepest face 33.3°, 33 objects) played
from its file (§11, `docs/baselines/le3-fa37670-*`). It is built in 119–131 ms, 60–77 ms of that
handing the 225 shapes their triangles. The first tick's bake makes 2,348 vertices, and the frame it
is in, with the scene's loading, is 2.1–2.3 s where the greybox's is 0.8–0.9 s; a re-bake around a
structure is 1.8–2.1 s to the new mesh and 4.4–9.5 ms on the tick. A dedicated server with 6 + 1
bots and a client, 300 s of ticks, three runs of each:

| | Mean | 95 in 100 under | 99 in 100 under | Units on the field, on average |
|---|---|---|---|---|
| The greybox | 2.36–2.88 ms | 3.8–5.0 ms | 5.1–6.2 ms | 13.1–20.3 |
| The bundled map | 4.56–5.55 ms | 7.5–9.8 ms | 8.9–11.5 ms | 13.3–17.9 |

**The sculpt brush**: the steepest grade once the centre has risen H.

| Radius | Core | H | On the grid | 1.5·H / ((1 − core)·R) |
|---|---|---|---|---|
| 4 m | 0 | 2 m | 35.60° | 36.87° |
| 8 m | 0 | 2 m | 20.16° | 20.56° |
| 16 m | 0 | 2 m | 10.58° | 10.62° |
| 32 m | 0 | 2 m | 5.35° | 5.36° |
| 16 m | 0.5 | 2 m | 20.36° | 20.56° |
| 16 m | 0.8 | 2 m | 42.09° | 43.15° |
| 16 m | 0.5 | 6 m | 48.07° | 48.37° |

**The grade-locked brush.** Over 200 random strokes of 80 dabs on rough ground, raising and
lowering, 5–60°, radii 4–24 m: the steepest edge after exceeded max(k, the steepest before) by
0.0; the largest change on or outside the rim was 9 × 10⁻¹⁶ m; the steepest face reached 1.357
times the limit, under the √2. Held still on level ground, the steepest face it leaves:

| Setting | Radius | Height reached | Sharp tip | ρ = 1 m | ρ = 2 m | ρ = 3 m |
|---|---|---|---|---|---|---|
| 10° | 8 m | 0.88 m | 14.00° | 10.48° | 10.02° | 9.46° |
| 20° | 8 m | 1.82 m | 27.24° | 20.89° | 20.03° | 18.97° |
| 30° | 16 m | 7.33 m | 39.23° | 31.20° | 30.36° | 30.04° |
| 45° | 16 m | 12.70 m | 54.74° | 46.37° | 45.42° | 45.04° |
| 60° | 8 m | 8.66 m | 67.79° | 61.17° | 60.04° | 58.57° |

**Levelling** (§6.3), over 40 random terrains with pads and roads in turn: off target on an
anchor, 0; change where the ground was already inside the skirt, 0; steepest pair of samples
after, less max(k, before), 6 × 10⁻¹⁶; change on a second application, 0; reach past
max|h − S| / k, none. Over 300 pads on rough ground and 300 on gentle, with the skirt measured
from the footprint: the steepest face was at worst 1.151 and 1.147 times max(k, before), on 0.44 %
and 0.27 % of the cells changed; every pad was level to 0. Measured from the nearest levelled
sample instead, 58 of 120 pads left a face over the limit, at worst 1.414 times it.

**LE1's tests** assert the same over seeded terrains of their own, and where a number is the
terrain's and not the rule's it is another number. 300 pads on rough ground and 300 on gentle,
from the footprint: at worst 1.181 and 1.154 times max(k, before), on 0.48 % and 0.34 % of the
cells changed. From the nearest levelled sample, 48 of 120 pads left a face over 1.16 times it, at
worst 1.414. On level ground, 200 pads under rectangles and discs: at worst 1.150, where the most
there can be is √2 · (√(1.5² + 1.5 · √2 + 1) − 1.5) = 1.156, a face with a corner on the circle a
corner's cone starts at. What is proven of rough ground is the √2. The tables here that no terrain
enters — the sculpt brush, the grade-locked brush held still, the skirt, the bands — came out of
the C# to the decimals printed.

| Skirt | 25° | 30° | 35° | 40° | 45° |
|---|---|---|---|---|---|
| Steepest face it can leave, atan(√2 · tan) | 33.40° | 39.23° | 44.72° | 49.88° | 54.74° |

**A road's profile**, over 200 random centrelines: steeper than g by at most 5 × 10⁻¹⁵; off E/2 by
at most 2 × 10⁻¹⁵ m. For instance 216 m of ground at up to 97.5 % takes an 8 % road with 1.79 m of
cut or fill at its worst. An end pinned 4 m over the ground meets it exactly at 10.0 % and is back
on the free profile in 15 m. Round the tightest corner, where the inside edge climbs twice as
steeply as the centreline, the ribbon's targets pass the 30° skirt at a bend of 258° for a 10 %
road, 212° for 15 % and 164° for 20 %.

**The bands**, with the default numbers. Higher or steeper never gave an earlier band over a
261 × 179 sweep.

| Above the water \ slope | 0° | 10° | 15° | 20° | 25° | 30° | 35° | 40° | 45° | 50° | 60° |
|---|---|---|---|---|---|---|---|---|---|---|---|
| −2 m | water | water | water | water | water | water | water | water | water | water | water |
| 0.5 m | sand | sand | sand | mud | grass | grass | grass | grass | grass | grass | stone |
| 1 m | sand | sand | sand | grass | grass | grass | grass | grass | grass | stone | stone |
| 2 m | mud | mud | mud | grass | grass | grass | grass | grass | grass | stone | stone |
| 5 m | grass | grass | grass | grass | grass | grass | grass | grass | stone | stone | stone |
| 8 m | grass | grass | grass | grass | grass | grass | grass | stone | stone | stone | stone |
| 12 m | grass | grass | grass | grass | grass | grass | stone | stone | stone | stone | stone |
| 16 m | grass | grass | grass | grass | stone | stone | stone | stone | stone | stone | stone |
| 20 m | grass | grass | grass | stone | stone | stone | stone | stone | stone | stone | stone |
| 25 m | stone | stone | stone | stone | stone | stone | stone | stone | stone | stone | stone |

### 10.1 Measured by LE0

At `67fec82`, where this section had a list of what was not known. Every line is in
`docs/baselines/le0-67fec82-*.txt`, as the checks printed it, and
`scripts/level-editor-checks.sh` prints it again; §11 says which file holds what and how it was
run. Times are one machine's, four cores of a cloud VM doing nothing else, and are for holding its
rows against each other. "Heightmap" is one `HeightMapShape3D`; "triangle meshes" is LE-D5's
collider.

**Rays**, against §6.1's `Sample`, to 1 mm. A slanted ray starts over the ground and ends under it.

| | Heightmap, level | Heightmap, hills | Triangle meshes, level | Triangle meshes, hills |
|---|---|---|---|---|
| 1,000 straight down | all met it, to 0.000 mm | all, to 0.016 mm | all, to 0.000 mm | all, to 0.016 mm |
| 1,000 slanted, under 16 m in plan | all met it | all | all | all |
| 10,000 slanted, 16–200 m in plan | **36 met nothing** | **23 met nothing; 6 met ground beyond a hill** | all met it | all met it |
| the ones that failed, cast again in pieces of at most 12 m | all met it | all met it | | |

Split along a cell's other diagonal, 659 of the 1,000 rays down at the hills would have been over
1 mm off: the triangles are the ones §3 names. Of the 65 rays that passed through, none was heading
down both x and z, where a quarter of all the rays were;
`tools/level_editor_checks/heightmap_ray_walk.py` shows why (§3).

**The collider's data.**

| | Heightmap | Triangle meshes |
|---|---|---|
| All 58,081 samples | 0.2 ms | 37–48 ms, for 225 shapes |
| One chunk of 16 × 16 cells | there is no partial update (§3) | 0.2–0.3 ms |
| The file's `int16` bytes to floats, all of them; every height reads back bit for bit | 0.2 ms | 0.2 ms |
| On the stripped server export, from the pack | | 116,162 B read and decoded in 3–4 ms, the shapes set in 40–60 ms; no ray of 12,000 through them; 14,716 vertices baked |

The export took the samples through the preset's include filter, as §9.1 plans, and says "No
loader found for resource" of the file as it stores it.

**The bake**: §7.3's table.

**A tick**, on a dedicated server with its 6 + 1 bots and one client joined, frame to frame over
300 s of ticks at `--fixed-fps 60`. Three runs of each, two of the level heightmap; a tick has
16.7 ms.

| | Mean | 95 in 100 under | 99 in 100 under |
|---|---|---|---|
| Slab | 3.5 ms | 6.2–6.6 ms | 7.8–9.6 ms |
| Level terrain, triangle meshes | 5.4–5.9 | 8.2–8.8 | 10.5–13.9 |
| Level terrain, heightmap | 7.8–9.2 | 12.8–17.5 | 16.7–23.8 |
| Hills, triangle meshes | 7.8–9.1 | 13.8–17.3 | 20.2–23.3 |
| Hills, heightmap | 11.6–12.5 | 19.9–23.4 | 26.1–30.9 |

Every run had 12–22 units on the field on average and 27–48 at the most, and one or a few frames
of 80–390 ms, the slab's among them. On the hills the bots did what they do on the slab: 57–62
units trained in the 300 s against 60–61, nodes taken 5–7 times against 5–7, 1–7 structures built
against 7–9. Their sandbag walls were refused as "something is in the way" 2–10 times a run on the
hills and never on level ground: LE-D21's refusal, met in play.

The heightmap costs a tick 2–4 ms more than the triangle meshes on the same ground. How many shapes
the triangles are cut into costs it nothing: 8.2–8.8 ms on the hills at 1, 25, 225 and 900 of them
(`baselines/le0-67fec82-chunks.txt`). What the hills cost over level terrain, 2.5–3 ms, and level
terrain over the slab, 2 ms, LE0 did not take apart (§13).

**Walking**, a body and a rifleman alike, at a plane 8 m high:

| Face | On triangles, either collider | On a box turned to the same grade |
|---|---|---|
| 20° to 42.1° | up it. Normals met within 3.5° of the face's | up it. Normals met: the face's |
| 43.0°, 44.0°, 45.0° | not up it in 12 s: on the floor and on a wall in the same ticks, normals met to 46.6° | up it |
| 46.0° | not up it | not up it |

The bake at 45° kept the 44.0° face and dropped the 45.0° one; on a box it kept 45.0°. A body makes
5.00 m/s on the level, 4.36 up 20° and 5.01 down it, and is never off the floor going down.

**The 4x4**, on the triangle meshes:

| | |
|---|---|
| From rest, its front axle 1 m short of the foot | over the top of 2.9° to 9.1°, its nose up to 14.6°, off its wheels 4 ticks at the most. On 10.0° and 11.9° it stops on the slope, off its wheels, for the 20 s it was given |
| At 18 m/s | over the top of every face to 70°, off its wheels for 1.4–2.6 s; from 30° its nose stays level, which is its hull and not its wheels taking it up. 75° and 80° stop it at the foot |
| At 18 m/s over a ridge, up and down the same grade | the same: across to 70°, its origin up to 7.8 m over the crest |
| Stopped on a slope with the handbrake | stays put on 4.9°, held or left empty. On 10° and steeper it is not at rest after 3 s of it |

**The helicopter** over the hills, ground from −3.9 to 8.5 m under it: with the climb held,
19.8–27.7 m over the ground against a ceiling of 25; from 5.7 m with nothing held, 0.9–15.3 m over
it, its altitude carried from 6.8 to 15.1 m. Neither pass slowed it under 15.2 m/s or put it down.

**Structure sites**: §7.5.

**Prediction**, a client walking behind the relay — 40 ± 10 ms each way, 80 ms there and back — for
60 s a leg, on a plane of 20° that is 12 m high. Corrections a second and the mean error, over
the seconds that are clear of a clock resynchronisation by 2 s. Six sessions on the slope at the
1 cm threshold, three on each collider, and two with it at 2 cm (LE-D25); the slab's own three
sessions gave 0.00 and 2.6–3.4 mm.

| Leg | Corrections a second, at 1 cm | Mean error | At 2 cm |
|---|---|---|---|
| On the level, beside the slope | 0.00–0.08 | 2.8–4.6 mm | 0.00 |
| Up and down it | 0.00–0.16 | 3.8–4.9 mm | 0.00 |
| Across it | 0.00–1.46 | 4.8–7.1 mm | 0.00 |
| Diagonally on it | 0.00–2.50 | 4.9–6.5 mm | 0.00 |
| Over the crease at its foot | 0.00–0.20 | 3.3–7.6 mm | 0.00–0.03 |
| Over the crease at its top | 0.00–0.52 | 3.8–7.0 mm | 0.00–0.03 |

What sets a session's place in those ranges is its clock, not its collider. In the two sessions
where the clock was resynchronised throughout — 36 and 48 times in six minutes, which is the link's
jitter and happened on the slab 0, 13 and 32 times in a minute — a body crossing the slope was
corrected 0.8–2.5 times a second; in the four where it was resynchronised four times or not at all,
0.00–0.75. A correction on the slope is of 10–16 mm, but for the odd one of a tick's travel, which
the level has too. At 2 cm there were 0.00–0.03 a second in a session of each kind, and the worst
error left standing was 16 mm. The yardstick the repository has for a link like this is GV3's for
driving: under one correction a second and under 5 cm of error (GROUND_VEHICLES.md §12).

---

## 11. Phases

Days are solo engineering days, as elsewhere in `docs/`. Each phase leaves `dotnet test` green and
the scenario suite where it was. Engine checks are `scripts/level-editor-checks.sh` over
`tools/EngineChecks/LevelChecks.cs`: one line a fact, PASS or FAIL, as the other check scripts are.

```
LE0 ─► LE1 ─► LE2 ─► LE3 ─┬─► LE4 ─► LE5 ─► LE6        the editor
                          └─► LE7 ─► LE8               maps that travel
                                               LE9     docs, alongside
```

LE0–LE3 with LE7 is the smallest thing worth having: maps that are data, played and fetched, with
no editor yet — 14½ days, LE0's 1½, LE1's 3, LE2's 3 and LE3's 4 of them spent. Their first maps
come from LE2's generator.

### LE0 — The baseline and the spike (1½ days) — done

What was asked of it:

- `dotnet test` and the scenario suite at the starting commit.
- In a scratch copy of the greybox, the slab replaced by a 241 × 241 `HeightMapShape3D`: flat, then
  the rolling hills of §10.
- A ray down at 1,000 random points agrees with §6.1's `Sample` to 1 mm: the layout and the
  triangles are as §3 reads them.
- The navigation bake: vertices, the first tick's wall time, a re-bake's time and its share on the
  tick. The server's frame time with 6 + 1 bots, on hills against the slab.
- A body and a unit walk 44° and not 46°. Mispredictions a second and mean prediction error, along
  and across a 20° slope and over a crease, on an 80 ms link (`tools/EngineChecks/delay_relay.py`).
- The 4x4: the steepest grade it climbs from rest and at speed, and across a crease. The
  helicopter's height over hills.
- The computer strategist's builders on a 5° and a 10° slope: what is refused, and as what.
- The time to set the collider's data. A stripped server export building the collider from bytes.
- The scenario suite on the flat heightmap against the slab.

**Done when** each is a number, and LE-D5, §7.3's order and LE-D21 are each confirmed or amended
here. **If the flat heightmap does not match the slab within the suite's spread,** stop: the
collider is the plan's foundation.

**Measured at `67fec82`**, whose code is `cd0c558`'s: the commits between them change only `docs/`
and `tools/level_editor_checks/design_checks.py`. .NET SDK 8.0.134 (Ubuntu 24.04's
`dotnet-sdk-8.0`); Godot `4.6.stable.mono.official.89cea1439` and its export templates; the project
imported once with `--headless --import`; four cores of a cloud VM. The suite's harness starts its
server with no frame-rate option, so `--fixed-fps 60` went in through `GODOT`, as GV0's did.

It was to keep nothing but numbers, and it keeps its checks as well:
`scripts/level-editor-checks.sh` over `tools/EngineChecks/LevelChecks.cs` and `LevelTerrain.cs`, on
a copy of the tree whose greybox has its slab replaced by terrain, with three helpers in
`tools/level_editor_checks/`: `le0_terrain.py` (§10's hills as a file's samples; the scene's edit),
`compare_suites.py` and `heightmap_ray_walk.py`. The later phases are checked by the same script,
and two of what it found are the engine's and want casting again when the engine changes (§13).
The game compiles and loads none of it.

| Asked | It came to | Where |
|---|---|---|
| `dotnet test` | 3,247 of 3,247 | |
| The suite at the starting commit | on the slab, twice: 49 and 48 of 57. Eight fail both times: `htn_locker_runner_kit`, `htn_squad_retreats`, `htn_strategist_digs_in`, `locker_launcher_vs_tank`, `locker_resupply`, `locker_resupply_under_fire`, `tactics_move_to_node`, `technical_wreck_ejects`. `tactics_sniper_overwatch` fails once | `suite-slab-1`, `-2` |
| The slab replaced by a heightmap, flat and hills | done, and by LE-D5's triangle meshes beside it. The hills are §10's, read from a file of `int16`, with the greybox's 67 pieces and its walls levelled into them by §6.3 at 30°: −6.7 to 17.1 m, the steepest face 59.6°, 1.6 % of faces over 45° | `checks-1` |
| A ray down at 1,000 points, to 1 mm | to 0.016 mm, on both colliders. **Slanted and 16 m or longer, 65 of 20,000 pass through the heightmap** | §10.1; `checks-1` |
| The bake | 2.0–2.3 s at the first tick on hills, 6–11 ms on the tick at a re-bake | §7.3; `checks-1` |
| The server's frame time with 6 + 1 bots | 3.5 ms a tick on the slab; on hills 7.8–9.1, and over 20 for 1 tick in 100 | §10.1; `checks-1` to `-3`, `chunks` |
| A body and a unit walk 44° and not 46° | **they do not walk 44°**: 42.1°, and not 43.0° | §10.1; `checks-1` |
| Prediction on a 20° slope and over a crease | corrected up to 2.5 times a second crossing it, by 10–16 mm; not at 2 cm | §10.1; `checks-1`, `-4`, `net-by-hand` |
| The 4x4 from rest, at speed, across a crease | 9.1° from rest; **70° at speed, on its hull** | §10.1; `checks-1` |
| The helicopter over hills | 19.8–27.7 m over the ground with the climb held, against a ceiling of 25; carried over hills flown at from 5.7 m | §10.1; `checks-1` |
| Builders on 5° and 10° | `Blocked`: the pillbox turned 45° on 5°; most of what was tried on 10° | §7.5; `checks-1` |
| The collider's data; the stripped export | 0.2 ms a chunk, 37–48 ms for all; nine facts of nine on the export | §10.1; `checks-1` |
| The suite on the flat heightmap | twice on each collider: 49 and 49 of 57 on the triangle meshes, 49 and 50 on the heightmap. The 48 that pass on the slab both times pass all four times; the slab's eight fail, but `htn_squad_retreats` once | `suite-trimesh-1`, `-2`, `suite-heightmap-1`, `-2`, `suites` |

The files are `docs/baselines/le0-67fec82-<name>.txt`. `checks-1` is
`level-editor-checks.sh ground slopes frame net export`; `checks-2` and `-3` its `frame` part
again; `checks-4` `net net-2cm`; `chunks` and `net-by-hand` say in their first lines what was run
by hand; `suites` is `compare_suites.py` over the six runs of the suite.

**The stop did not come.** Level terrain plays as the slab does, on either collider: no scenario the
slab passes fails on it, and the eight the slab fails fail on it, `htn_strategist_digs_in` on the
same one of its two seeds. The suite's own spread on the slab is one scenario,
`tactics_sniper_overwatch`, which passes once and fails once there and passes four times on terrain;
`htn_squad_retreats`, whose claim is one over half of its seeds, passes once in four.

The suite needed one thing the plan did not foresee. The greybox's slab is 300 m across, and
`barracks_mortar` stands its player on the 30 m of it outside the walls, where a 240 m field has no
ground: run without that rim, the suite on terrain failed that scenario four times of four and no
other that the slab passes. The suite part keeps the rim, as boxes; nothing else does, since a data
map ends at its walls (LE-D3). Without the rim `htn_strategist_digs_in` passed four times of four,
which it does not on the slab: whatever fails it there has to do with the ground outside the walls.
That is CONTROL_POINTS.md §5.4's to follow, not this plan's.

**Decided here.**

- **LE-D5 is changed** to the fallback it was written with: the triangle meshes. The heightmap lets
  long rays through, and costs a tick more.
- **§7.3's order is reversed**: tiles first, if anything. Neither is built ahead of need.
- **LE-D21 is confirmed**, with its numbers.
- **LE-D23, LE-D24 and LE-D25 are added**, for three things LE0 was not asked: what is walkable is
  40°, not 45°; the 4x4 is a road vehicle on a data map; a correction is applied from 2 cm.
  LE-D24 and LE-D25 are flagged.
- **A tick on hills is a risk** (§13), not a decision: LE3 measures it on the box.

**Left for a person:** to look at any of it. Every number here is from a headless process: whether
a correction of 12 mm shows, whether a 4x4 going up a cliff reads as a bug or as a stunt, and what a
tick costs on the machine the game is served from.

### LE1 — The ground's rules, engine-free (3 days) — done

What was asked of it: `HeightField`, `TerrainBrush`, `TerrainConform`, `TerrainBands`, `MapLimits`,
and their tests. The tests assert §6's properties as properties — over seeded random terrains, not
on one example — and §10's tables as values. **Done when** every claim of §6.1–6.4 is a test that
fails when its rule is broken: the rim rule removed, the skirt measured from samples, a band edge
reordered.

**Built at `744b906`**, on .NET SDK 8.0.134 (Ubuntu 24.04's `dotnet-sdk-8.0`), four cores of a
cloud VM, with no Godot: `Scripts/Sim/Level/` — `MapLimits.cs`, `HeightField.cs`,
`TerrainBrush.cs`, `TerrainConform.cs`, `TerrainBands.cs` — and in `Tests/` a class for each, with
the seeded terrains and the measures they share in `LevelGround.cs`. Nothing else in the game is
changed, and nothing calls them yet: LE2's document and LE3's `TerrainNode` are their first
callers. The game's own assembly compiles with them, `dotnet build Gdpyr.csproj` with no warning.

| Asked | It came to | Where |
|---|---|---|
| `dotnet test` | 3,369 of 3,369: the 3,247 there were, and 122 in the five classes | `tests` |
| §6's properties, as properties | each over seeded terrains or strokes, against the definition written out a second time in the test: `Sample` against the triangle the point is in; a ray against every triangle in turn, and 20,000 long ones from over the ground to under it, none through; 200 grade-locked strokes of 80 dabs, edge by edge; §6.3's five over 40 terrains, against L and U from every anchor; the bands over §10's sweep and over 40 maps' own numbers | the five classes |
| §10's tables, as values | the sculpt brush's seven rows; the grade-locked brush held still, five rows at four tips; the skirt's five; the bands' ten rows of eleven; the grid's samples, cells, triangles and bytes | the same |
| Every claim fails when its rule is broken | 75 rules broken one at a time in a copy of the tree, each one edit to one file, the five classes run on each: **75 of 75** left a test failing, the three §11 names among them | `mutations` |

The files are `docs/baselines/le1-744b906-<name>.txt`. `mutations` is
`python3 tools/level_editor_checks/le1_mutations.py` as it printed: a line a rule, with the tests
that failed. Its first run, of 66 rules, caught 64. A ramp's strip could run on past its two
clicks, and a blend could be drawn across twice its width, with every test passing; each has its
test now, and the list has those two and nine more.

**Decided here**, each in the section it changes:

- **§6.2, the grade-locked brush rises from `held`**, the apex the ground about its centre already
  holds, not from the height under the cursor. Its 30° claim is for ground no steeper than its
  setting. Under 2.21 m of radius it moves a sample only where the cursor is near one. Its presets
  rise at 2 m/s, which the table did not say.
- **§6.2, smooth and level** do not read the time step; smooth reads the ground as it was before the
  dab and keeps to a sample's neighbours along the field's edge there. **A ramp** is as wide as its
  ring, its ends are skirted, and a line steeper than the skirt is refused.
- **§6.3**: a refusal changes nothing; a sample given two targets is refused; heights stay `float`
  through it, and rounding, levelling and rounding again changes nothing. A pad's worst face on
  level ground is 1.156 times the skirt, and on rough ground 1.18 was seen (§10).
- **§6.4**: which the six numbers are, when they are a map's, and that the water's edge is not
  blended.
- **§6.1**: how the ray walks, and that the class takes a smaller grid for the tests. `MapLimits`
  is a file of its own (§5).

**Left for a person:** nothing here is drawn, so nothing here can be looked at: whether a brush's
rates and radii feel right is LE4's to find out, and what the bands look like is LE3's. One thing
is LE2's to choose and is not chosen here: a new map's water level. `BandSettings.Default` has the
water at 0 m, and level ground at 0 m over water at 0 m is sand. LE2 chose −5 m (§6.4).

### LE2 — The document, the file, the validator (3 days) — done

What was asked of it: `MapDocument`, `MapFile`, `MapValidator`, `PrefabSpecs`, `PrefabPlacement`,
`EditHistory`, `tools/Gdpyr.MapTool` (`info`, `hash`, `validate`, and `generate`, which writes a
seeded map: hills, pads, the pieces a round needs). **Done when** a generated map round-trips to
the same document; the reader refuses every case of §6.8 with its reason and never throws; the
validator refuses a map with each of §6.9's errors and passes the generated one; and placing then
undoing restores every sample.

**Built at `ceaaec9`** — the code at `6f87d96`, and three of its tests made to hold their rules at
`ceaaec9` — on .NET SDK 8.0.134 (Ubuntu 24.04's `dotnet-sdk-8.0`), four cores of a cloud
VM, with no Godot: in `Scripts/Sim/Level/`, `MapDocument.cs`, `MapFile.cs`, `MapValidator.cs`,
`PrefabSpecs.cs`, `PrefabPlacement.cs` and `EditHistory.cs`, and `MapLimits` gains the file's
limits; in `Tests/`, a class for each, with the maps and the file's layout written out a second
time in `LevelMaps.cs`; and `tools/Gdpyr.MapTool`, `gdpyr-map`, whose generator the tests compile
in. Nothing else in the game is changed, and nothing in it calls them yet: LE3's `LevelRoot` and
`MapLibrary` are their first callers. The game's own assembly compiles with them, `dotnet build
Gdpyr.csproj` with no warning.

| Asked | It came to | Where |
|---|---|---|
| `dotnet test` | 3,506 of 3,506: the 3,369 there were, and 137 in the six classes. One that was there, `HtnDebugTests.PlanningWithHistory_AllocatesNothingOnTheTick`, which counts the bytes its thread allocates, failed 2 of 18 whole runs made while the mutation runs held three cores; alone it passed 12 of 12, and at `aba0179` the whole suite passed 10 of 10 under the same load | `tests` |
| A generated map round-trips to the same document | seeds 1–4, field by field and sample by sample, and written again from what was read, the same bytes; and a document with every field set at its extremes, and one at every limit of §6.8 | `MapFileTests` |
| The reader refuses every case of §6.8 with its reason, and never throws | each case, with the words of its reason: not a map; another version or chunk count; a chunk out of order, twice, missing or unknown; a length past the end of the file or past its limit; over 1 MiB; bytes after the last chunk or left in one; another size or spacing; a height outside LE-D4; heights that inflate short, long, or not at all; text too long, not UTF-8, or with a control character; too many objects, parameters, roads or points; a parameter twice. Every truncation of a generated file, 400 splices of two, 3,000 corruptions and 2,000 noises behind a map's header: none throws, and each refusal has a reason | `MapFileTests` |
| The validator refuses each of §6.9's errors, and passes the generated map | seeds 1–8 pass with no error and no warning, and so do their files read back. Each error from the generated map changed to have it — and, where a change can be made that does, nothing else; each warning LE2 checks (§6.9) | `MapValidatorTests` |
| Placing then undoing restores every sample | on six seeded hills, 60 steps each of placements of every prefab, and takings-up, through the history: undone, every sample and every object as they were; redone, as they were after | `PrefabPlacementTests`, `EditHistoryTests` |
| `PrefabSpecs`, `PrefabPlacement` | §6.5's table as values, and the structures, the barracks' door and its ring read from their files; each of §6.5's results from a placement built to produce it, in its order; the grade a least-squares plane's; the pad at the mean, level exactly out to 1.5 m, with no edge past max(30°, before) by more than a step of rounding; of 150 placements tried at random, every one taken stands where the validator finds nothing wrong | `PrefabSpecsTests`, `PrefabPlacementTests` |
| `tools/Gdpyr.MapTool` | `info`, `hash`, `validate`, `generate`. The same seed is the same bytes; its hash is `sha256sum`'s; it exits 0 for a map that plays, 1 for one refused, 2 for a wrong command line or disk | `maptool` |
| Every rule's test fails when it is broken | 99 rules broken one at a time in a copy of the tree, each one edit to one file, the six classes run on each: **99 of 99** | `mutations` |

The files are `docs/baselines/le2-ceaaec9-<name>.txt`. `tests` is the six classes' lines and the
totals; `maptool` is `gdpyr-map` run as its first lines say; `mutations` is
`python3 tools/level_editor_checks/le2_mutations.py` as it printed. Its first run, at `6f87d96`, is
`le2-6f87d96-mutations.txt`, and caught 96 of 99: a triangle taken across the other diagonal, which
the test missed because the sample it raised is in both of its cell's triangles; a pad's skirt left
between steps, which nothing tested by moving a neighbour's sample less than half a step; and a
pad's ground taken as its footprint alone, which the test missed because the sample it lowered was
under another object's footprint too. Each has its test now. A test found one rule wanting:
a whole-field keep after a local one moved two samples, which is why a pad is kept by levelling them
all (§6.5). Reading the code found another: placement asked whether a pad was under the water of the
pad's level and the validator of the object's centimetre, which can be 2 mm apart (§6.5); a test
holds the two to one answer now.

**Decided here**, each in the section it changes:

- **§6.4, a new map's water is at −5 m**: its level ground is grass.
- **§6.5, a pad's level is the mean to the centimetre and then the nearest step**, a function of the
  stored height. **A pad is kept by levelling every pad again**, in the order placed. **Under the
  water** is asked of a pad's level. `BlocksDoor` is either way round. The boxes, the parameters and
  their ranges, and a bridge section's frame are set; no flag is defined.
- **§6.8**: zlib's format; the band numbers' order; four chunks and nothing after or left over; the
  chunks' limits; text that is UTF-8 with no control character; a parameter once; what is the
  reader's to refuse and what the validator's; a document held as the file holds it.
- **§6.9**: what "a number outside its range" is; when a bridge end is joined or landed; an issue
  names its objects and where it is. A road steeper than its limit, and `InTheWay`'s road ribbon,
  are LE6's, with `RoadPath`; reach is LE5's.
- **§8.6**: an edit is applied forwards and reverted backwards, objects first; a stroke is pushed
  once made; an edit that changes nothing is not a step.
- **The generator** puts §2.3's layout of MAPS.md on hills gentler than §10's — steepest face 33–36°,
  none over 40° — and levels a disc under each group of pieces before placing them, so that no seed
  refuses one. It is seeded, its time is 0 unless given, and its maps carry no author unless given.

**Left for a person:** nothing here is drawn or played. What a generated map looks like, and whether
it plays as a map should — its layout is MAPS.md §2.3's, with two nodes the seed puts — is LE3's,
when one is bundled.

### LE3 — The world from a file (4 days) — done

What was asked of it:

`LevelRoot`, `TerrainNode`, `LevelBuilder`, `MapLibrary`, `MapHost.tscn`, the shader and palette,
`--map` and `--practice`, `MapInfo`'s kind, one bundled map from LE2's generator, the export
preset's filter, and §7.4's changes: the camera, the bake's box and its slope (LE-D23), and the
correction's threshold with its tests (LE-D25). LE-D21, which LE0 confirmed. `TerrainNode` frees its
shapes when it leaves the tree (§7.1).

Engine checks: `--practice --map` on the bundled map logs `navigation baked` with vertices and
`round live`; the spawn count, barracks, nodes and locker are the file's; a ray meets the ground
where the file says; a bot round runs ten minutes with units trained, a node taken and nothing in
MAPS.md §4's list of lines that must not appear; the same on a `--server` process with a client;
the same from the stripped export. `level-editor-checks.sh ground slopes` as LE0 left them: no ray
through the collider, and a body and a unit up the faces a skirt can leave. A tick's time with a
round of bots on the bundled map, on the box, against the slab's there (§13). Scenarios: two of the
existing ones re-run on the bundled map by `"map"`.

**Done when** those pass. **Left for a person:** to look at it — the bands, the water, the seams
between chunks — and to walk, drive and fly over it.

**Built at `fa37670`**, on .NET SDK 8.0.134 (Ubuntu 24.04's `dotnet-sdk-8.0`), Godot
`4.6.stable.mono.official.89cea1439` and its Linux export templates, four cores of a cloud VM: in
`Scripts/Level/`, `LevelRoot.cs`, `TerrainNode.cs`, `TerrainPalette.cs`, `LevelBuilder.cs` and
`MapLibrary.cs`; `Scripts/Sim/Level/MapNames.cs`; `Scenes/Maps/MapHost.tscn`;
`Levels/materials/terrain.gdshader` and `terrain_palette.tres`; `Maps/hills.map` and its catalog
entry; and the changes to `Bootstrap`, `LaunchOptions`, `MapCatalog`, `Session`, `MainMenu`,
`UnitManager`, `Structure`, `StrategistController`, `Structures.cs`, `SimConfig`, `Flight.cs`, the
export preset and the playtest harness that §7, §9 and §4 name. In `Tests/`, `BundledMapTests`,
`MapLaunchTests` and `StructureGroundTests`, and LE-D25's and a scenario's map in
`PredictionLedgerTests` and `ScenarioTests`. The checks are `scripts/level-editor-checks.sh`'s
`map-*` parts over `tools/EngineChecks/LevelMapChecks.cs`, and `le3_mutations.py`.

| Asked | It came to | Where |
|---|---|---|
| `--practice --map` on the bundled map logs `navigation baked` with vertices and `round live` | 2,348 vertices, baked at 40°; the round live | `map-practice` |
| The spawn count, barracks, nodes and locker are the file's | they and the kiosk, guns, cans, cover and anchor, counted by group and by the game's managers, are the file's; every padded object stands on its pad to the bit; the document is the file read again | `map-practice`, `map-server`, `map-export` |
| A ray meets the ground where the file says | 1,000 straight down, the worst 0.017 mm from `Sample`; of 2,000 at a slant from over the ground to under it, 16–200 m in plan, none through it and none off it | the same |
| A bot round of ten minutes: units trained, a node taken, none of MAPS.md §4's lines | 600 s of ticks: 110 units trained, nodes taken 12 times, 107 ground deaths, 3 rounds; none of the five lines | `map-practice` |
| The same on a `--server` with a client | 24 facts of 24 on the server — 82 units, nodes taken 16 times, 71 deaths in 600 s of real time — and 20 of 20 on the client, which was told `hills`, built the same world from its own copy and found the same ground; none of the five lines in either log | `map-server` |
| The same from the stripped export | the deploy preset as it is, `include_filter="*.map"`: the map read from the pack, 24 of 24 and 20 of 20, 104 units, nodes taken 15 times; re-bakes 1.8–2.1 s to the new mesh, 4.4–9.5 ms of it on the tick; the process ends with 0 | `map-export` |
| `ground slopes` as LE0 left them | every fact of both: on the triangle meshes no ray of 10,000 a ground through them, and a body and a rifleman up 42.06°; the heightmap's 36 and 23 + 6 rays as LE0 counted them. LE0's structure sites are held to LE-D21 now: 18 facts of 18 (§7.5) | `ground`, `slopes` |
| A tick with a round of bots on the bundled map against the slab's | not on the box, which nothing here reaches: on this VM, three runs each of 300 s of ticks, a dedicated server with 6 + 1 bots and a client, the greybox 2.36–2.88 ms a tick on average, 5.1–6.2 ms for 1 in 100; the bundled map 4.56–5.55 ms and 8.9–11.5 ms | `map-frame-1` to `-3` |
| Two scenarios re-run on the bundled map by `"map"` | `hills_spawn_points_shared` and `hills_strategist_economy` pass, and so do the two they were copied from | `map-suite` |
| `TerrainNode` frees its shapes | the stripped server, whose finalizer LE0 saw fault, ends with 0 | `map-export` |
| The bake's box (LE-D8) | the bundled map has no ground under its water; with a basin dug 3 m under it, the floor is 1 m under the water, no vertex of 2,638 under it, and the closest point of the mesh to the bed is on the bank, 15.6 m off | `map-basin` |
| `dotnet test` | 3,592 of 3,592: the 3,506 there were and 86 | `tests` |
| Every LE3 rule's test fails when it is broken | 31 rules broken one at a time: **31 of 31** | `mutations` |

The files are `docs/baselines/le3-fa37670-<name>.txt`, each saying in its first lines what was run
and what ran beside it, and `le3-fa37670-overview.png`, `-spawn.png` and `-basin.png`, three of
`map-shot`'s pictures. `map-frame-1` was copied from the tree 20 s before `fa37670` was committed,
from the same sources; `map-basin` and `map-shot` are parts the next commit adds, run on `fa37670`'s
game. The first runs of `map-practice` and `map-server`, with the checks a few lines older, passed
the same facts and are not kept. One check was mended before it counted: the basin's closest point
first read the navigation map before it had the new mesh, which answers the origin for anything,
and passed for that reason; it waits for the map's iteration now and holds the point to the bank.

**Decided here**, each in the section it changes:

- **§7.5, LE-D21's plinth is 0.5 m deep for every site**, and a corner's ground is looked for within
  0.55 m of the centre's: a corner not found is too far, and one under a box is the ground under it.
- **§7.1**: what each piece is named and where the kiosk's pads and a spawn line's markers are in
  their prefab's frame; a padded object stands at its pad's level; a structure in a file is LE5's
  to put up; bridge sections are built from their boxes, a ramp's deck tilted.
- **§7.2**: the shader's five samplers are the four bands' and the road's; the palette's five
  textures the four bands' and the water's; the placeholders are imported with mipmaps.
- **§7.3**: the slope and the box on a data map only, and the box only where ground is under the water.
- **§7.4**: the camera's hold over the ground and the cursor's plane through the anchor, on every map.
- **§9.1**: a data map is read and validated when it is selected; a bundled map's hash is its
  catalog entry's; the picker and `HostSettings` keep catalog ids until LE7.
- **§9.5**: `--practice`; `--map`'s order, and what it is refused with.
- **The check script**: `net-2cm` is `net-1cm` now, since 2 cm is the game's; `export`'s filter
  adds the checks' samples to the preset's `*.map`; LE0's `slopes` holds each structure to LE-D21.

**Left for a person:** to look at it, walk, drive and fly over it on a screen — the pictures are
llvmpipe's under a virtual display, and the bundled map is grass from wall to wall, which §6.4's
bands give ground 3.5–15.6 m over its water; to see the strategist camera kept over a hill, which
no check drives; the tick on the box in DEPLOYMENT.md, since every time here is this VM's, where
the bundled map costs 1.7–2.1 times the greybox's tick and its worst 1 in 100 stays under 12 ms of
the 16.7; and LE-D24, whose 4x4 still climbs 70° at speed on its hull.

### LE4 — The editor: shell and terrain (4 days) — done

What was asked of it:

`Scenes/MapEditor`, the camera, the panels, the terrain tool with its presets and overlays, undo,
save and autosave, validate, test (LE-D19), the menu's button, `--edit`. Engine checks drive the
editor headless through its commands: a stroke changes the samples under it and no others; undo
restores them; save then load is the same document; the test process starts on the saved file and
reports `round live`. **Left for a person:** whether sculpting feels right — rates, radii, the
ring, the readout — which no check answers.

**Built at `98c6125`**, on .NET SDK 8.0.134 (Ubuntu 24.04's `dotnet-sdk-8.0`), Godot
`4.6.stable.mono.official.89cea1439`, four cores of a cloud VM: `Scenes/MapEditor/MapEditor.tscn`;
in `Scripts/MapEditor/`, `MapEditorRoot.cs` — the scene's root and every command its panels and keys
give — `MapEditorRoot.Ui.cs`, its widgets, and `MapEditorControls.cs`, the keys, a brush's settings,
the sliders, the readout, the status line, the autosave's clock and the test process's arguments,
engine-free; in `Scripts/Sim/Level/`, `MapEditSession.cs`, the document with its undo, the brush
and the stroke under way, and `MapStore.cs`, one's own maps on disk; and the changes to
`TerrainNode` — a chunk's mesh drawn again, a collider chunk given its triangles again, the water and
the band numbers set, the overlay — `SpectatorCamera`, `MainMenu`, `LaunchOptions` (`--edit`),
`EditHistory` (the steps undo and redo would take) and `MapLibrary`, whose hashes leave an autosave
out. In `Tests/`, `MapEditSessionTests`, `MapStoreTests`, `MapEditorControlsTests` and
`EditLaunchTests`. The checks are `scripts/level-editor-checks.sh`'s `editor` and `editor-shot`
parts over `tools/EngineChecks/LevelEditorChecks.cs`, and `le4_mutations.py`.

| Asked | It came to | Where |
|---|---|---|
| `Scenes/MapEditor`, the camera, the panels, the terrain tool with its presets and overlays, undo, save and autosave, validate, test, the menu's button, `--edit` | built, as §8.1–8.3 and §8.6–8.7 say. `--edit le4-new` opens the editor on a new map, the session in its menu phase; `--edit` alone, the list. Drawn under a virtual display: the list, the editor with the ring under the cursor, and a stroke under the slope overlay with the validator's list | `editor`, `editor-shot` |
| A stroke changes the samples under it and no others | nine strokes of every family of brush — on a new map a sculpt dragged along a line, a 30° mound held still, a 60° cliff lowered, smooth and level over the first, a bluff at the field's edge, one after a save and one to be autosaved; on a clone of the bundled map, one across a node's pad. Every sample that changed, 19 to 3,299 of them, is within the brush's radius of a point it dabbed at, and none beyond; each stroke is one step to undo; no mesh chunk out of its reach was drawn again, of the 2 to 30 that were. While the button was down and after, every vertex drawn and every triangle of the collider is the document's, and rays straight down meet the ground within 0.015 mm of `Sample`. The node's 421 pad samples stay at its level and the map still plays; the stroke at the edge builds the walls again; a ramp up the mound is one step | `editor` |
| Undo restores them | the seven steps undone one at a time, every sample as it was before each and the new map's at the end; redone, as each was after; the drawn ground, the collider and 300 rays the document's at both ends. Through the viewport's input, `Ctrl+Z` and `Ctrl+Y` undo and redo a step, `]` widens the brush by 1.25 and `O` sets the shader's overlay | `editor` |
| Save then load is the same document | saved, the file reads as the document being edited, at revision 1, the editor's hash the file's and no `.tmp` left; saved again after a stroke, the first file is the `.bak`; opened, the same document at revision 2 with nothing to undo, and its world the document's. Autosaved, the autosave is the map as it is and the saved file untouched; opened again, it is offered; taken back, the map is as autosaved and not saved; saved, the autosave goes. Save as is a fork. A clone of the bundled map has the bundled file's hash for its parent's, plays, and the round's builder puts its pieces up in the editor | `editor` |
| The test process starts on the saved file and reports `round live` | a map saved and unchanged is not saved again; the process is started `--path <copy> --headless -- --practice --map <file>`. It plays the file by its path and hash, stands on the edited ground — (−12, −4), raised 0.66 m by the stroke across the pad — and its round is live; it ended with 0 after 3.3 s, the editor holding the same map and simulating nothing throughout. The two processes' log has none of MAPS.md §4's five lines | `editor` |
| `dotnet test` | 3,693 of 3,693: the 3,592 there were and 101 | `tests` |
| Every LE4 rule's test fails when it is broken | 61 rules broken one at a time — a stroke's rate, its cap, its click, its time step, its rounding, its pads, its step and its undo; a ramp's width and rounding; the bands' order and the text's bytes; what a save, an autosave, the list and a fork leave on the disk; the keys, a brush's settings, the key steps, the slope classes, the overlays, the readout's disc and crease; the test process's arguments; the autosave's clock; `--edit` — each one edit to one file: **61 of 61** left a test failing | `mutations` |

The files are `docs/baselines/le4-98c6125-<name>.txt`, each saying in its first lines what was run,
and `le4-98c6125-list.png`, `-editor.png` and `-editor-slope.png`, the `editor-shot` part's pictures.
`editor` and then `editor-shot` ran at `98c6125` with nothing beside them, and the mutations after
them. The mutations' first run, of 62 rules, is `le4-98c6125-mutations-first.txt`: it ran on the tree
`98c6125` was committed from but for the two changes it led to, and caught 60. The water slider's
clamp to LE-D4 broken, every test passed, because the step the water is stored as holds it there
already: the clamp is gone, and its rule with it. The readout reading a cell's first triangle only,
every test passed, because the test's crease was in a neighbouring cell's first triangle too: the test
has a cell now whose crease is in its second alone.

**Decided here**, each in the section it changes:

- **§8.3, a held brush dabs 30 times a second**, each dab a thirtieth of a second and at most eight
  in a frame; a click is one dab. A stroke that changes no stored sample is not a step. A band slider
  is not a step either, and holds an edge between its neighbours.
- **§8.1, the editor lists every map of one's own that reads**, with what the validator finds in it;
  LE-D16's rule is for what is played. `--edit <name>` makes a new map by a name that is not one's
  own. A clone is saved at once and keeps its source's name and author. The round's objects are
  built in the editor under a node that is not processed, and built again when an edit changes the
  ground along the field's edge, which the walls span.
- **§8.2, the camera looks on the middle button**, a held Ctrl moves it nowhere, and `Q` and `E` lower
  and raise it. `Esc` drops a stroke or a ramp's first click.
- **§8.6**: the last file is copied to `.bak` before the rename; an autosave that is the map as saved
  is dropped, not offered; leaving without saving drops the autosave.
- **§8.7**: Test saves only what is not saved, plays no map the validator finds an error in, and is
  headless from a headless editor.
- **§9.2, Save as makes a map that has a file a fork of it.** Import and the console's commands are
  not built.

**Left for a person:** whether sculpting feels right — rates, radii, the ring, the readout — which no
check answers, and the editor on a screen with a mouse: every check here gave its commands, and its
keys through the viewport's input, and the pictures are llvmpipe's under a virtual display; a mouse
over the panels, a drag across the view and a button let go outside the window are not driven by
anything here. Export's and the map settings' dialogs, and the autosave's offer as a dialog, were
not opened by any check.

### LE5 — Prefabs and a round's pieces (3 days) — done

What was asked of it:

The catalog's engine half, the palette, the ghost, selection, parameters, structures at round
start, the deep check. Engine checks: each catalog prefab placed on a 6° slope stands on a level
pad, its colliders inside its boxes; each result of §6.5 is produced by a placement built to
produce it; a map with two barracks, three nodes, cover and a pre-built pillbox plays a bot round
in which the pillbox fires and is destroyed. **Done when** a map made only in the editor passes
the validator and that round.

**Built at `e03c74b`**, with `bd8361e` after it — a palette entry a row, where two to a row clipped
their counts, and the two tests the mutation run found weak — on .NET SDK 8.0.134, Godot
`4.6.stable.mono.official.89cea1439`, four cores of a cloud VM: `Scripts/Core/PrefabCatalog.cs`;
`Scripts/MapEditor/MapEditorRoot.Prefabs.cs` and `MapEditorRoot.Deep.cs`; `Scripts/Sim/Level/BuildableGround.cs`;
and the changes to `PrefabPlacement` (an object replaced in place), `MapEditSession` (placing,
replacing, taking up, picking), `MapValidator` (reach), `PrefabSpecs` (a structure prefab's kind),
`MapEditorControls` (the palette, counts, turns, the grid, the ghost's words, the buildable overlay),
`LevelBuilder`, `TerrainNode` (the buildable channel), the terrain shader, `UnitManager`
(`PlaceMapStructures`, `ConfigureBake`), `CombatManager` and the editor's root and panels. In `Tests/`,
`MapEditPrefabTests`, `MapReachTests` and `BuildableGroundTests`, and LE5's cases in
`MapEditorControlsTests` and `PrefabSpecsTests`. The checks are `scripts/level-editor-checks.sh`'s
`editor-prefabs` part, and two pictures added to `editor-shot`, over `LevelEditorChecks`' `prefabs`
and `round` parts; and `le5_mutations.py`.

| Asked | It came to | Where |
|---|---|---|
| The catalog's engine half, the palette, the ghost, selection, parameters, structures at round start, the deep check | built, as §6.5, §6.9, §7.5 and §8.4 say. Drawn under a virtual display: the prefab tool's ghost over the buildable overlay, and the palette with its counts | `editor-prefabs`, `editor-shot` |
| Each catalog prefab on a 6° slope stands on a level pad, its colliders inside its boxes | all 17, turned 30°, on a map that is a 6° slope, through the editor's commands, the ghost giving each the placement's answer and grade. Every padded one is level to the bit, from 25 samples under a low wall to 1,085 under the bunker, and its node stands at its level. The barracks' building, the cover, the bunker's 252 corners and both bridge sections are inside their boxes to 0.0 cm; the ramp was 12.4 cm out until its deck and rails were sheared boxes (§6.7). The locker, kiosk, guns, can, node and anchor have no collider of their own. The structures are the round's to put up, and the round part holds them | `editor-prefabs` |
| Each result of §6.5 from a placement built to produce it | a second kiosk, `LimitReached`; a locker at the edge, `OutOfBounds`; one on the kiosk, `InTheWay`; one on a 30° mound's side, `TooSteep`; one in the water, `InWater`; a cover block beside the kiosk, `DisturbsNeighbour`; a pillbox before the barracks' door, `BlocksDoor`; prefab 999, `UnknownPrefab` — each the ghost's answer too, with no sample, object or step changed. A click selects the kiosk, `R` turns it 15° where it is in the file onto a level pad, the node's capture radius goes to 12 m on a 577-sample disc, `Del` takes a can up and undo puts it back at its index | `editor-prefabs` |
| The deep check | on the slope, 692 vertices baked in 1.1 s and the 2 walks found; a node on an island raised out of the water, 4 walks and the island's 2 unreached, named, warnings; all undone, every sample the slope's | `editor-prefabs` |
| A map made only in the editor passes the validator, and plays a bot round in which its pillbox fires and is destroyed | a new map, two strokes, and 18 pieces placed with the prefab tool — spawn line, locker, kiosk, 2 barracks, 3 nodes, 6 cover, a pillbox, an anchor, a gun, a can: no error, 2 warnings, and the deep check's 9 walks found. Tested: the round put the pillbox up, finished and the strategist's, where the file put it at its pad's level, its colliders inside its box and the plinth under it; it fired, and a bot's launcher destroyed it 148 s into the round | `editor-prefabs` |
| LE4's checks | `editor`, 132 of 132 and its test process 3 of 3, at `e03c74b` | `editor` |
| `dotnet test` | 3,721 of 3,721 at `bd8361e`: the 3,693 there were and 28 | `tests` |
| Every LE5 rule's test fails when it is broken | 30 rules broken one at a time — an object replaced counted, in its own way, a neighbour to its old pad, or moved to the end of the file; a refused placement applied; a pick that is not the smallest; the walks' starts and heights; the buildable rule's turns and corners; a structure's kind; the palette, counts, turns, grid, ghost and overlays — each one edit to one file: **30 of 30**. The first run, at `e03c74b`, caught 28: an object turned on an even tilt keeps its level, so nothing tested that its own old pad does not refuse it, and the generated map has one spawn line and one locker, so counting the walks' starts could not tell them apart; each has its test at `bd8361e`. LE4's one rule whose piece LE5 moved is brought along, and caught | `mutations` |

The files are `docs/baselines/le5-<commit>-<name>.txt`, each saying in its first lines what was run
and at which commit, and `le5-bd8361e-prefabs.png` and `-prefabs-buildable.png`. `editor-prefabs` and
`editor` ran at `e03c74b`, one after the other with nothing beside them; `editor-shot`, the tests and
the mutations at `bd8361e`, which changed nothing either part reads. The test process of
`editor-prefabs` ended with a crash as it exited, after its seven facts were in and written down
(§13); the editor's part reads them from there.

**Decided here**, each in the section it changes:

- **§6.5, an object turned, moved or given a parameter keeps its place in the file**, and is not
  counted, in the way or a neighbour to itself. The catalog's engine half is `PrefabCatalog`, and a
  map's structures are put up on the round's first tick and after every clear of the field.
- **§6.7, a ramp's deck and rails are sheared boxes**, their ends square at the section's ends.
- **§6.9, a walk reaches** within 3 m of its node flat and 2 m in height, from within 3 m of its start.
- **§7.5, buildable is a pillbox taken square and turned 45°**, computed in the editor only.
- **§8.4**: the palette's order and one entry a row; a click picks the smallest thing under it; the
  map's structures are drawn and solid in the editor as their boxes.

**Left for a person:** placing with a mouse — the ghost under a moving cursor, a click that selects
where one meant to place — which no check moves; whether a map made this way plays well with people
on it, beyond one bot round in which the pillbox fell; and the crash as a practice round's process
exits (§13), which LE5 found and did not take further.

### LE6 — Roads and bridges (4 days)

`RoadPath`, `BridgeRules`, their tools, the road's coverage in the mesh. Engine checks: a road
across a ridge is no steeper than its limit at any centreline sample; a unit ordered across a
three-span bridge over water deeper than 1 m crosses it, and with the bridge taken up does not
arrive; a 4x4 driven by a scenario's script crosses it; a free-floating span stays where it was
put; a pier's top is at its deck and its foot under the ground; a dropship ordered onto the deck
lands beside it. **Left for a person:** driving it.

### LE7 — Fetching a map (3 days)

`MapTransfer`, `MapTransferService`, `ServerMap`'s fields, the menu's states, the host panel's
picker, the cache, demos at version 6, the agent's `welcome`. Engine checks, with a `--server`
process and clients behind the delay relay: a client without the map fetches it, hashes it and is
in the round; a second join reads the cache and sends no request; a client with a wrong file under
the hash's name fetches again; a fetch cut off at half is refused and the link dropped; a
hash-mismatched stream is refused; with the relay dropping datagrams, a chunk that has to be sent
again does not hold back a message on the default channel sent after it; a demo recorded on a data
map plays back. **Left for a person:** two machines, as LAN.md §6.

### LE8 — The dedicated server (2 days)

`scripts/deploy-map.sh`, `maps/active`, `--map-upload-token`, publish, `ClientSetNextMap`, the
restart. Engine checks: each answer of §9.5 from a request built to get it; a wrong token and a
replayed proof are `bad_proof`; an uploaded map is in the cache with its hash; a next-map request
ends the process with 0 after the round and a restart plays the new map. **Left for a person:** the
box in DEPLOYMENT.md, since nothing here has run under its unit.

### LE9 — Docs (alongside; 1 day to close)

MAPS.md gains data maps; LAN.md §3.4 the picker and the fetch; NETCODE.md §7 the messages and a
section of its own; DEMOS.md version 6; AGENT_API.md `welcome` and `"map"`; DEPLOYMENT.md the map
directory, the token and the restart; the README the editor and the flags;
IMPLEMENTATION_PLAN.md §5's "Multiple maps" cut, struck. This file's status becomes what was built
and what each phase ran.

**Total: 28½ days**, LE0's 1½, LE1's 3, LE2's 3, LE3's 4, LE4's 4 and LE5's 3 spent.

---

## 12. Verification at a glance

| Claim | Checked by | Phase |
|---|---|---|
| The collider is where §3 says, lets no ray through, and a flat one plays as the slab does | rays against `Sample`, 12,000 of them a ground; the scenario suite | LE0, done; `ground` again at LE3, done |
| A body and a unit walk what the plan calls walkable | faces from 20° to 46°, on the collider and on a box | LE0, done; `slopes` again at LE3, done |
| The ground is where the collider's triangles are, and stops a ray | `HeightFieldTests`: every triangle in turn; 20,000 long rays | LE1, done |
| A brush's grade is its setting | `TerrainBrushTests`, over random strokes | LE1, done |
| A pad is level, minimal and no steeper than its skirt | `TerrainConformTests` | LE1, done |
| The bands keep their order | `TerrainBandsTests`, a sweep | LE1, done |
| Each of those tests fails when its rule is broken | `le1_mutations.py`, 75 rules | LE1, done |
| A file reads back as it was written, and a bad one is refused | `MapFileTests` | LE2, done |
| A map the validator passes holds what a round needs | `MapValidatorTests`; `BundledMapTests`; a bot round | LE2, done; LE3, done |
| Placing then undoing restores every sample | `PrefabPlacementTests`, `EditHistoryTests` | LE2, done |
| Each of LE2's tests fails when its rule is broken | `le2_mutations.py`, 99 rules | LE2, done |
| Every peer builds the same ground | no float in the file; exact samples; the hash; a client's world held to its file | LE2, done; LE3, done; LE7 |
| A server without meshes builds and bakes it | the stripped export's check | LE3, done |
| A map is played from its file, and the world is the file's | `map-practice`, `map-server`, `map-export`: counts, rays, a bot round | LE3, done |
| A structure is refused as too steep where LE-D21 says, and stands on its plinth | `StructureGroundTests`; `slopes` | LE3, done |
| Each of LE3's tests fails when its rule is broken | `le3_mutations.py` | LE3, done |
| A stroke changes the samples under it and no others, and the drawn ground and the collider follow; undo restores them; save then open is the same map | `MapEditSessionTests`, `MapStoreTests`; `editor` | LE4, done |
| The editor's Test plays the saved file in a second process | `MapEditorControlsTests`; `editor` | LE4, done |
| Each of LE4's tests fails when its rule is broken | `le4_mutations.py` | LE4, done |
| Nothing is in the way of a placed prefab | `PrefabPlacementTests`; LE5's checks | LE2, done; LE5, done |
| Every prefab stands on a level pad with its colliders inside its boxes; each of §6.5's answers; a map made only in the editor plays, and its pillbox fires and falls | `MapEditPrefabTests`, `MapReachTests`, `BuildableGroundTests`; `editor-prefabs` | LE5, done |
| Each of LE5's tests fails when its rule is broken | `le5_mutations.py` | LE5, done |
| A road keeps its grade; a bridge carries units and a 4x4 | `RoadPathTests`; LE6's checks | LE6 |
| A client gets the map it lacks, and only a whole and valid one | `MapTransferTests`; LE7's checks | LE7 |
| A server takes no map from anyone without the token | `MapTransferTests`; LE8's checks | LE8 |

## 13. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| **The navigation bake on terrain is slow**, at the first tick or at every structure | `[rts] navigation re-baked … ms of it on the tick` | Measured by LE0 (§7.3): 2.0–2.3 s at the first tick on hills, 6–11 ms on the tick at a re-bake and 2 s to the new mesh. Not too much to build on; if the 2 s shows, tiles first |
| **Bodies catch on the collider's creases**, or prediction drifts on slopes | mispredictions a second above the flat baseline; visible hitching | Measured by LE0, and decided. They do not catch under 43°, and from there they stall: walkable is 40° (LE-D23). Prediction is corrected up to 2.5 times a second crossing a 20° slope, by 10–16 mm, which is the wire's rounding against a 1 cm threshold and stops at 2 cm (LE-D25). Whether any of it shows is a person's to say. LE3 made 2 cm the threshold, with its tests |
| **A flat-ground assumption §7.4 missed** | a bot round on hills that does what one on the slab does not | LE0 found one, the 4x4's level hull (LE-D24), and played ten bot rounds on hills (§10). LE3 played seven on the bundled map, three of them ten minutes long, and found none: units trained, nodes taken, structures built and lost, and the scenarios that it re-ran pass; the list is where to add one |
| **A hostile map file** from a server | — | A reader that is total, limits before allocation, a known inflated size, no scene and no object decoded, and tests over the hostile cases (§6.8). A client decodes only after the hash matches what it was told |
| **The upload socket is write access to a public box** | — | Off unless a token was given; the deploy unit gives none; HMAC with a nonce; one upload at a time; the cache directory only; validated before written |
| **Two builds disagree about a prefab** | a wall drawn where the server has a box of another size | The catalog is append-only and a test holds it; the file names the count it was made against; a client refuses ids it lacks. Sizes changing under an id is LAN.md §5's problem, unchanged |
| **The editor grows** | LE4–LE6 overrun | Destructive edits, one field size, no painting, no layers (§14). LE0–LE3 with LE7 ships without it |
| **Hand-built UI is slow to make** | LE4's four days gone on panels | The decisions are engine-free and tested; the widgets are the menu's kind, and the console's commands do everything a panel does. LE4 built the shell and the terrain tool's widgets in code over `MapEditorControls`, which `dotnet test` holds, and every command a panel gives is a method an engine check calls; the console's map commands are not built (§9.2) |
| **Water that only stops units** reads as a bug | a player found under it | LE-D8 is flagged; §14 names the rules that would follow |
| **The √2 on faces** leaves a speck the bake will not walk | a hole in the mesh at a pad's corner or a cone's foot | The skirt is 30°, which the √2 keeps under the 40° that is walkable (LE-D23); the slope overlay draws faces; the deep check catches a cut-off pad |
| **A tick on hills runs out of time**: with a round of bots, 1 in 20 over 14–17 ms and 1 in 100 over 20–23 ms of a 16.7 ms tick, where the slab's worst 1 in 100 is under 10 ms | `level-editor-checks.sh frame`; a server that falls behind its ticks | Measured by LE0 on four cores of a cloud VM. The collider is the cheaper of the two and its chunks cost nothing; what terrain and hills add is not taken apart. LE3 bundled a gentler map, steepest face 33.3°, and on the same VM its tick is 4.6–5.5 ms on average and under 8.9–11.5 ms for 99 in 100, 1.7–2.1 times the greybox's (§10). The box is DEPLOYMENT.md's, and nothing here reached it: measuring it there is a person's |
| **A ray through the ground** | a round or a sight line that a hill should have stopped | Found by LE0 in `HeightMapShape3D` and designed out (LE-D5); `level-editor-checks.sh ground` casts 10,000 at the collider, and holds it to none |
| **A practice round's process crashes as it exits**, after a round of bots: `FATAL: Condition "csharp_lang && !csharp_lang->script_bindings.is_empty()"`, then `handle_crash` | an exit code of 134 or 6 after every fact of a check holds | Found by LE5's checks: LE3's `map-practice` for 120 s on the bundled map ended so 2 times of 2 at `c1a9b4b`, LE4's commit, before any of LE5's code, and so did `editor-prefabs`' test process after its round. LE3's 600 s runs ended with 0. LE5 first took it for a re-bake on its worker at the quit; a check that quit with one under way ended with 0 2 times of 2, and one that quit a tick after destroying the map's structures 3 times of 3, and that guess and its code are gone. A C# object is let go of after the runtime has gone, and which is not known. LE5's check reads its test process's facts from what that process writes. A person who quits after a round is where it would be seen |
| **The engine moves under the plan**: a later Godot mends the heightmap's walk, or changes what a capsule's contacts are | `level-editor-checks.sh ground` and `slopes` on the new version | The checks are kept for this: the heightmap's rays are measured beside the collider's every time, and the steepest face walked is a number in the output |

## 14. Not in this plan

- **Other sizes.** A field other than 240 m, or samples closer than 1 m. The file carries both; the
  code that assumes ±120 m (§2) is the work.
- **Painting.** A texture set by hand over the computed band; more than one road surface.
- **Live modifiers.** Pads, roads and ramps that follow their object when it moves. Edits are
  destructive, and undo is the session's.
- **Overhangs, caves, tunnels.** A chunk of the collider can leave triangles out; nothing here
  does.
- **A 4x4 that drives hills**: its collider pitched and rolled with its chassis, and a face it
  could not drive stopping it (LE-D24). GROUND_VEHICLES.md's to plan.
- **Another physics engine.** `project.godot` pins GodotPhysics3D (§2) and LE0 measured nothing
  else; Jolt has a heightfield of its own.
- **Water as a rule**: wading speed, drowning, a vehicle's fording depth, rounds and sight through
  it.
- **Terrain that changes during a round**: craters, digging in.
- **Curved or branching bridge sections; road junctions** beyond ends that share a connector.
- **Props, foliage, lighting and sky per map**; a minimap.
- **Importing a heightmap image.** About half a day on LE4: the document is one array.
- **Changing map without a restart; rotation; votes; a way back to the menu** (LAN.md §9).
- **A map browser, a workshop, HTTP download, deltas between versions, evicting the cache.**
- **Two people editing one map.**
- **The mortar's lay on real ground; bots that use a hill as cover; a navigation mesh per unit
  size** (IMPLEMENTATION_PLAN.md §6).
