# gdpyr — Level editor: terrain, prefabs, roads and bridges, and maps that travel

**Status: a plan; none of it is built.** Written against `cd0c558`. The facts in §2 were read in
this repository's source at that commit, and those in §3 in Godot's source at tag `4.6-stable`. The
arithmetic in §10 was run: `python3 tools/level_editor_checks/design_checks.py` prints every number
quoted from it. No Godot and no .NET SDK were available where this was written, so nothing was
compiled and nothing was run in the engine; LE0 (§11) is the phase that measures what could not be.
The decisions (LE-D1–LE-D22, §4) are the defaults the plan is written against. Each is one line to
change; LE-D3, LE-D8, LE-D10 and LE-D18 are the ones to flag before LE1 starts. The phases
(LE0–LE9, §11) say what each builds, what checks it, and what is left for a person.

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
| `HeightMapShape3D`: grid points 1 unit apart, centred on the shape's origin; each square two triangles; a NaN height is a hole; "faster to check collisions against than `ConcavePolygonShape3D`". For another spacing the node is scaled, and "GodotPhysics3D does not support non-uniform scaling" | `doc/classes/HeightMapShape3D.xml` |
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
| LE-D5 | **The collider is the heightfield's triangles as `ConcavePolygonShape3D`s, one for each 16 × 16 cells: 225 of them on one body, on layer 1, in group `navmesh`.** A cell's two triangles are the ones a heightmap would make of it (§3). The mesh drawn uses the same triangles. *Changed by LE0 to the fallback this row was written with.* | Every consumer already asks layer 1, and the bake parses these shapes into the triangles it made of a heightmap, to the same mesh (§10): no consumer learns a new source of truth. One `HeightMapShape3D` was the first choice, and is cheaper to update — 0.2 ms for the whole field, against 0.2–0.3 ms a chunk and 37–48 ms for all 225 — but dearer to tick, by 2–4 ms with a round of bots on it. But LE0 found the defect its fallback was held for, in the shape's ray walk: of 10,000 rays 16 m or longer in plan, each from over the ground to under it, some passed through it and met nothing, on level ground and on hills (§3, §10); none passed through the triangle meshes. Rounds, sight lines and the fog are such rays. Both hand the same face test to the same solver (§3), and LE0 measured a body the same on both (§7.4). |
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
| LE-D16 | **Three places**: `user://maps/` (one's own), `user://maps/cache/<hash>.map` (received, read-only), `res://Maps/` (bundled, in the catalog). A file is listed only if it validates. | "A scene that is merely in the folder is not a map" (`MapCatalog`) holds: what makes a file a map is `MapValidator`, not its place. |
| LE-D17 | **A client without its server's map fetches it from that server**: 1,024 B chunks, reliable, on their own channel, 61 KB/s a peer; verified against the hash, validated, cached. | Asked 4. Under 2 s for a 1 m map's terrain and 2.5 s for the largest file one can be (§10). A channel of its own keeps a fetch from holding up the round's reliable messages (§3). |
| LE-D18 | **A dedicated server takes a map as a file**: `--map`, and `scripts/deploy-map.sh`. Upload over the game's connection is optional, off unless the server was given a token, and proven by HMAC. **Changing a server's map restarts its process.** | Asked 4. The box has a public address (DEPLOYMENT.md). A round cannot be torn down in place (§2); the unit already restarts the process. |
| LE-D19 | **Testing a map from the editor starts a second process** on the saved file. | No way back from a round (§2). It also exercises the real load path on every test. |
| LE-D20 | **Wire and formats: additive.** `ServerMap` gains fields; eight new messages, none in a round's tick; `DemoFormat.Version` 5 → 6; the agent's `welcome` names the map; the observation schema is unchanged. | Every trained checkpoint keeps loading; an old demo is refused, not misread. |
| LE-D21 | **A structure built during a round needs near-level ground**: the ground under its footprint may vary by 0.5 m, else `TooSteep`. It stands on the highest of it, on a plinth down to the lowest. *Confirmed by LE0.* | Today's probe refuses a pillbox on a slope past 4.6–6.5°, by direction, and calls it `Blocked` (§7.5); LE0 measured it at 4.5–6.2°, as `Blocked`, with the downhill edge 0.2 m in the air where it is taken. The rule keeps about that limit, names it, and closes the gap under the downhill edge. |
| LE-D23 | **Walkable is 40°.** The navigation bake's slope goes from 45° to 40° on a data map; the skirt of §6.3 is 30° and not a setting; the slope overlay turns red at 40°. *Added by LE0.* | On triangles a body and a unit walk a 42.1° face and stall on a 43.0° one, both colliders alike; a box they walk to 45° (§7.4). The bake keeps faces to 45°, so it would route units over ground they stall on. 40° is under what they walk with 2° to spare, and over the 39.2° a 30° skirt can leave. |
| LE-D24 | **The 4x4 is a road vehicle on a data map, for a first cut.** It keeps the level hull it has. A road's grade limit stops at 15 % and a bridge's ramp stays 1 in 8. *Added by LE0.* | Its collider does not pitch with its chassis and has 0.25 m under it (§2). From rest at the foot it climbs 9.1° and beaches on 10.0°; at 18 m/s it rides up any face to 70° on its hull, off its wheels, and only 75° stops it (§7.4). 15 % is 8.5° and 1 in 8 is 7.1°. **Flag if a 4x4 that cannot leave the road, and that a cliff does not stop, is not acceptable for a first cut**: the fix is the hull taking the chassis's pitch and roll, which is GROUND_VEHICLES.md's to plan (§14). |
| LE-D25 | **A correction is applied from 2 cm, not 1 cm**: `SimConfig.MispredictionThreshold`. *Added by LE0.* | A position crosses the wire at 1 cm an axis (§2). On level ground two axes of it are rounded and the third is exact; on a slope all three are, which is up to 8.7 mm before anything has gone wrong. Behind an 80 ms link a body crossing a 20° slope was corrected up to 2.5 times a second, by 10–16 mm, in sessions whose clock was being resynchronised, and up to 0.75 times in sessions where it was not; with the threshold at 2 cm, 0.03 at the most in either (§10). **Flag if 1 cm is to be kept**: then that rate is the cost, and it is under GV3's bar of one a second in four sessions of six. |
| LE-D22 | **Placeholders**: the greybox textures by band; boxes for decks, piers and cover. A palette resource is where art goes. | IMPLEMENTATION_PLAN.md §5: greybox. |

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
  MapTransfer.cs       sender and receiver as state machines over chunks; the upload's proof
  EditHistory.cs       undo and redo
Scripts/Level/       the engine half
  LevelRoot.cs         MapHost.tscn's root: builds the world, then opens the session
  TerrainNode.cs       collider, chunk meshes, water, overlays
  LevelBuilder.cs      prefabs, a round's pieces, bridges and piers, the walls
  MapLibrary.cs        user://maps, the cache, the bundled maps
Scripts/Net/MapTransferService.cs    the messages, their channel, their budget
Scripts/MapEditor/   the editor scene's root, its camera, four tools, the panels
Scenes/Maps/MapHost.tscn · Scenes/MapEditor/MapEditor.tscn · Levels/materials/terrain.gdshader
```

**Rejected:** a Godot editor plugin (LE-D2); sending scenes or a PCK (LE-D1); a `HeightMapShape3D`
collider, which LE0 measured and found rays through (LE-D5); a painted splat map (LE-D7); tilting a prefab to its ground
(LE-D9, LE-D10); decals or a ribbon mesh for roads (LE-D12); deriving pads and piers on load
(LE-D14); an engine-free copy of the ground for the simulation to query in place of rays — the
rays work, and two sources of truth would have to be kept equal.

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
  where the fractions sum to at most 1, else the second. `Raycast` walks the cells a ray crosses;
  it is the editor's cursor, which must not depend on when the physics server last saw the ground.
- `EdgeGrade`, `FaceGrade`, and a normal by central differences for the mesh and the bands.
- `MapLimits.MinHeightMeters` −16, `MaxHeightMeters` +64 (LE-D4). Every operation clamps to them.

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

    raise:  apex = min(centre + rate · dt,  min over rim samples q of  h(q) + k · φ(r_q))
            h' = max(h, apex − k · φ(r))    for r ≤ R
    lower:  the mirror image, with max and min exchanged

The rim is the disc's outer 1.5 m. Holding the apex by every rim sample puts the cone at or under
the ground on the whole rim, so only samples inside it move.

*No edge ends steeper than max(k, what it was).* For any two samples a and b,
|max(f, g)(a) − max(f, g)(b)| ≤ max(|f(a) − f(b)|, |g(a) − g(b)|). The cone is no steeper than k
between any two points, because |∇φ| < 1. The rim is thicker than a cell's diagonal, so an edge
either has both ends in the disc — where h′ = max(h, cone) at both, the rim included, since the
cone is not above the ground there — or has no end that moved.

Held still on level ground the cone stops when its foot reaches the rim, k · φ(R − 1.5) high —
7.3 m at 30° and 16 m (§10): a set grade and a radius give a height, and a bigger hill at the same
grade is a bigger brush or a moving one. The rounded tip is what keeps its faces at the setting:
sharp, a 45° cone leaves 54.7° faces at its tip, the √2 (§10).

**Smooth** moves each sample towards the mean of its four neighbours by `w · strength` ≤ 1: a
convex combination, so it never raises the highest or lowers the lowest sample it touches.
**Level** moves each towards the height under the cursor when the stroke began. **Ramp** is two
clicks and a width: the strip between them is anchored to the straight line between their heights
and applied with §6.3, so its sides are skirted.

| Preset | Family | Radius | Core | Rate or grade | Makes |
|---|---|---|---|---|---|
| Plain | sculpt | 32 m | 0 | 1 m/s | 5.4° once 2 m is raised |
| Hill | sculpt | 16 m | 0 | 2 m/s | 10.6° at 2 m |
| Bank | sculpt | 16 m | 0.5 | 2 m/s | 20.4° at 2 m |
| Bluff | sculpt | 16 m | 0.8 | 2 m/s | 42.1° at 2 m |
| Mound 10°, 20°, 30° | grade-locked | 16 m | — | the grade | ground a body walks, on every face |
| Cliff 60° | grade-locked | 8 m | — | the grade | a wall to bodies and to the bake; not to a 4x4 at speed (LE-D24) |
| Smooth, Level, Ramp | | 8 m | 0 | 0.5 | |

Radius runs 2–64 m, rate 0.25–8 m/s, core 0–0.9, grade 5–70°. Up to 30° a grade-locked brush
leaves every face under 40° whatever it is drawn over, by the √2: atan(√2 · tan 30°) = 39.2°. 40° is
what is walkable (LE-D23); at 35° the same bound is 44.7°, which LE0 found a body does not walk.

### 6.3 Levelling ground to a shape — `TerrainConform.cs`

One operator serves a prefab's pad, a road's ribbon, a ramp's strip and a bridgehead. Given
anchors — points q of a shape, each with a target height S(q) — and a skirt grade k:

    L(p) = max over q of  S(q) − k · d(p, q)
    U(p) = min over q of  S(q) + k · d(p, q)
    h'(p) = clamp(h(p), L(p), U(p))

d is flat distance. It applies when L ≤ U at every anchor, which is to say when S is itself no
steeper than k between any two of them; a caller whose targets are steeper is refused. Then:

1. **h′ = S on the anchors.** There L = U = S: no other anchor's cone can pass S(q) when S is no
   steeper than k.
2. **h′ = h wherever L ≤ h ≤ U.** Ground already inside the skirt is not touched.
3. **Between any two samples a and b, |h′(a) − h′(b)| ≤ max(|h(a) − h(b)|, k · d(a, b)).** Each
   S(q) ± k · d(·, q) is no steeper than k (the triangle inequality); max and min keep that
   (§6.2's inequality); and clamp(h, L, U) is max(min(h, U), L).
4. **Applying it twice changes nothing**: h′ already lies between L and U.
5. **It reaches no further than max|h − S| / k from the anchors**: a sample moves only if it is
   further from some S(q) in height than k times its distance from q.

For a pad S is one number and d is the distance to the footprint less the margin, so L and U are
`level ∓ k · max(0, distance − 1.5 m)`. The distance is measured to the footprint's rectangle
itself, not to the nearest levelled sample: measured from samples, every stair of a rotated edge
is a crease, and 58 of 120 pads left a face over the limit, at worst by the whole √2; measured
from the rectangle the worst face seen was 1.15 times the limit, on 0.4 % of the cells changed
(§10).

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
steeper never gives an earlier band**: the order asked for holds everywhere. The six numbers and
the water level are per map. The table of what they give is in §10: sand and mud are low and
gentle, grass is the middle, and stone is anything high or steep — level ground at 22 m, ground
4 m up at 45°.

`Classify` returns the band and, for drawing, a weight for each of two neighbouring bands across a
blend of 1 m of u. The shader computes the same u from the fragment's height and its normal; the
C# function is what the editor's overlay, a map's preview and the tests use. They need not agree
to the bit: nothing but the picture reads the shader.

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

**The checks**, in order; the first that fails is the answer the ghost shows.

| Result | When |
|---|---|
| `LimitReached` | the map already holds as many of the kind as it may |
| `OutOfBounds` | a box leaves ±120 m, or a round's piece has its origin outside ±115 m |
| `InTheWay` | a box overlaps a box of another object or a pier, in plan by more than 0.05 m and in height (LE-D11); or a pad's prefab stands on a road's ribbon |
| `TooSteep` | the average grade under the footprint is over the spec's |
| `InWater` | the pad's level is under the water |
| `DisturbsNeighbour` | levelling would move a sample inside another object's pad |
| `BlocksDoor` | a structure within 10 m of a barracks door (`StructurePlacement.DoorClearanceMeters`) |
| `Ok` | the object and its pad are applied, as one step to undo |

**The average grade and the pad.** Fit `h = a + b·x + c·z` by least squares to the samples under
the footprint. (b, c) is the average grade as a vector; atan√(b² + c²) is what the ghost shows and
what the spec's limit is checked against. A least-squares plane passes through its samples'
centroid, so its height at the footprint's centre is their mean: that is the pad's level, rounded
to a step. Levelling to the mean cuts as much as it fills under the footprint. Then §6.3: every
sample within 1.5 m of the footprint is at the level, exactly, and the skirt runs out at 30°. A
prefab without a pad stands at the ground's height under its origin. A footprint over fewer than
three samples takes the triangle under its centre.

**A pad is kept.** When a stroke or a road ends, every pad its rectangle reaches is levelled
again; by §6.3's fourth property that changes nothing unless the edit had moved it. So in a
document an object always stands on its pad, and the validator's check of that (§6.9) fails only
on a file something else wrote.

Yaw is free or in 15° steps. Moving an object is taking it up and placing it again: the old pad
stays, as any sculpted ground does.

### 6.6 Roads — `RoadPath.cs`

A road is 2–64 control points, a width (4, 6 or 8 m) and a grade limit g (6–15 %, 10 % unless
set: 15 % is 8.5°, under the 9.1° a 4x4 was measured to climb from rest, LE-D24). Its corners are rounded to a radius of at least its width, and the centreline is sampled
every 0.5 m. With f(s) the ground along the centreline:

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

### 7.2 Drawing it

The shader computes §6.4's u from the fragment's world height and normal, blends the two
neighbouring bands, and mixes in the road by the vertex's coverage. Sand, mud and grass are
sampled by world x and z; stone is sampled on three planes, since it is what cliffs wear. Five
`sampler2D`, nothing the Compatibility renderer lacks (§3). A uniform switches the editor's
overlays in the same shader.

Placeholders are `ThirdParty/GreyboxTextures`' grids: yellow sand, orange mud, green grass, grey
stone, and solid blue for the water's quad. `Levels/materials/terrain_palette.tres` names the five
textures and their scales; replacing art is replacing that resource (LE-D22).

### 7.3 Navigation

`BakeNavigation` changes in two things on a data map: its slope limit is 40° (LE-D23), and on a
map with water it sets the bake's box, with its floor 1 m under the water level (LE-D8, §3). The
bake's grid is the 1,200 × 1,200 columns it already is for a 240 m map. What terrain adds is
115,200 source triangles where a slab was 12, and a surface that is not one polygon.

**LE0 measured what that costs** (`docs/baselines/le0-67fec82-checks.txt`): the greybox on its
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
| The 4x4 | its wheel rays follow the ground; its hull, which is level whatever its chassis does, does not (§2). From rest at the foot it climbs 9.1° and beaches on 10.0°, its nose in the slope and its wheels off it. At 18 m/s it rides up a face of anything to 70°, off its wheels for 1.4–2.6 s, and is stopped by 75° (§3). Onto 10° at that speed it is off its wheels for 2.6 s. Parked, it stays put on 5° | LE-D24: roads to 15 %, and a flag |
| The helicopter's height ray and its 25 m ceiling over the ground | follows the ground. Over the hills with the climb held, 19.8–27.7 m over it: 2.7 m past its ceiling where the ground falls away. Flown at them from 5.7 m with nothing held it is carried over, 0.9 m clear at the least, and is neither slowed nor put down | none |
| The dropship: cruise height by ray, landing sites | follows the ground. It will not land on a deck, which has ground under it (`IsLandingSite`) | none |
| The drone's floor (−20 m) and ceiling (120 m); the kill plane (−50 m); the agent's rays from 80 m | all outside LE-D4's limits | none |
| A builder's structure site | a pillbox is refused as `Blocked` past 4.5–6.2°, a sandbag wall across a slope past 5.8°, a sniper tower past 8.4–11.5° (§7.5) | LE-D21, §7.5 |
| The strategist camera | height limits from the anchor's y; a cursor that falls back to the plane y = 0 | held 4 m over the ground under it; the fallback plane goes through the anchor |
| Cover, spawn slots, lockers, kiosk pads, node discs | stand on pads | none |
| A capture pad's 4 m vertical tolerance | holds on a pad | none; the validator warns past 4 m |
| The mortar launcher's level plane | the shell still meets the real ground; the ring drawn is off by the ground's difference in height | none (§14) |
| A barracks' defended ring | a hill between shortens it | none; the validator's warning stays flat distance |
| `FlightTraits`' "ground … under 30 m" | ground to 64 m and cruise to 100 m: 164 m, inside ±327.67 m | the comment |
| HTN planners, bot steering | flat distances and the navigation mesh. LE0 played three rounds of 6 + 1 bots on hills: units trained, nodes taken and structures built as on the slab (§10) | none |
| The server's tick | 3.5 ms on the slab with a round of bots, 5.4–5.9 on level terrain, 7.8–9.1 on hills, where 1 tick in 20 takes over 14–17 ms of its 16.7 and 1 in 100 over 20 (§10) | none decided: a risk (§13), and LE3's to measure on the box |

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

---

## 8. The editor

### 8.1 Entering it

**Map editor** on the main menu, or `--edit [name]`. It lists one's own maps, and anything in the
library to clone. The editor is a scene whose root is not a `WorldRoot`: the session stays in its
menu phase, and the one node that ticks the simulation is gated on that
(`Scripts/Net/PlayerManager.cs:313`). It holds a `MapDocument`, a `TerrainNode` and a
`LevelBuilder` — the ones a round uses — so what is edited is what will be played.

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

### 8.4 Prefabs

A palette by kind. The selected prefab follows the cursor as a ghost, drawn where it would stand
on its pad: green, or red with §6.5's reason beside it and the average grade under it. A click
places it and its pad. A click on a placed object selects it: turn it, take it up, or edit its
parameters. Counts sit beside each palette entry against their limits (`barracks 1/4`).

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

Save writes `name.map.tmp` and renames it over `name.map`, keeping the last as `name.map.bak`, and
bumps the revision. An unsaved map is autosaved to `name.autosave.map` every minute, and offered
back when the editor next opens it.

### 8.7 Validate and test

**Validate** runs §6.9 and lists what it finds; a click on an issue flies to it. **Deep** also
bakes the navigation mesh in the editor and checks reach. Errors do not block saving: an
unfinished map is a file. They block listing, hosting and sending.

**Test** saves and starts a second process (LE-D19): `OS.CreateInstance` with `-- --practice
--map <path>`, and `--path <project>` before that when this is not an export. It is the practice
round the menu offers, on this map, and closing it returns to an editor that never left.

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
  launch error is.
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
  string for a scene path.
- The agent's `welcome` gains `map: { id, name, hash }`. The observation is unchanged: positions
  still scale by 256 m, and the passability plane is still rays.
- A scenario file gains an optional `"map"`, a catalog id; the harness passes it as `--map`.
  Scenarios without one play the greybox, as now.

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
[`baselines/le0-67fec82-checks.txt`](baselines/le0-67fec82-checks.txt), and
`scripts/level-editor-checks.sh` prints it again; §11 says how it was run. Times are one machine's,
four cores of a cloud VM doing nothing else, and are for holding its rows against each other.
"Heightmap" is one `HeightMapShape3D`; "triangle meshes" is LE-D5's collider.

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
jitter and happened on the slab 0, 13 and 32 times in a minute — a body crossing the slope was corrected 0.8–2.5 times a
second; in the four where it was resynchronised four times or not at all, 0.00–0.75. A correction
on the slope is of 10–16 mm, but for the odd one of a tick's travel, which the level has too. At
2 cm there were 0.00–0.03 a second in a session of each kind, and the worst error left standing was
16 mm. The yardstick the repository has for a link like this is GV3's for driving: under one
correction a second and under 5 cm of error (GROUND_VEHICLES.md §12).

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
no editor yet — 14½ days, LE0's 1½ of them spent. Their first maps come from a test's generator.

### LE0 — The baseline and the spike (1½ days)

Nothing is kept from it but numbers, in `docs/baselines/`, and three decisions confirmed or changed.

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

### LE1 — The ground's rules, engine-free (3 days)

`HeightField`, `TerrainBrush`, `TerrainConform`, `TerrainBands`, `MapLimits`, and their tests. The
tests assert §6's properties as properties — over seeded random terrains, not on one example —
and §10's tables as values. **Done when** every claim of §6.1–6.4 is a test that fails when its
rule is broken: the rim rule removed, the skirt measured from samples, a band edge reordered.

### LE2 — The document, the file, the validator (3 days)

`MapDocument`, `MapFile`, `MapValidator`, `PrefabSpecs`, `PrefabPlacement`, `EditHistory`,
`tools/Gdpyr.MapTool` (`info`, `hash`, `validate`, and `generate`, which writes a seeded map:
hills, pads, the pieces a round needs). **Done when** a generated map round-trips to the same
document; the reader refuses every case of §6.8 with its reason and never throws; the validator
refuses a map with each of §6.9's errors and passes the generated one; and placing then undoing
restores every sample.

### LE3 — The world from a file (4 days)

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

### LE4 — The editor: shell and terrain (4 days)

`Scenes/MapEditor`, the camera, the panels, the terrain tool with its presets and overlays, undo,
save and autosave, validate, test (LE-D19), the menu's button, `--edit`. Engine checks drive the
editor headless through its commands: a stroke changes the samples under it and no others; undo
restores them; save then load is the same document; the test process starts on the saved file and
reports `round live`. **Left for a person:** whether sculpting feels right — rates, radii, the
ring, the readout — which no check answers.

### LE5 — Prefabs and a round's pieces (3 days)

The catalog's engine half, the palette, the ghost, selection, parameters, structures at round
start, the deep check. Engine checks: each catalog prefab placed on a 6° slope stands on a level
pad, its colliders inside its boxes; each result of §6.5 is produced by a placement built to
produce it; a map with two barracks, three nodes, cover and a pre-built pillbox plays a bot round
in which the pillbox fires and is destroyed. **Done when** a map made only in the editor passes
the validator and that round.

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

**Total: 28½ days**, LE0's 1½ spent.

---

## 12. Verification at a glance

| Claim | Checked by | Phase |
|---|---|---|
| The collider is where §3 says, and a flat one plays as the slab does | rays against `Sample`; the scenario suite | LE0 |
| A brush's grade is its setting | `TerrainBrushTests`, over random strokes | LE1 |
| A pad is level, minimal and no steeper than its skirt | `TerrainConformTests` | LE1 |
| The bands keep their order | `TerrainBandsTests`, a sweep | LE1 |
| A file reads back as it was written, and a bad one is refused | `MapFileTests` | LE2 |
| A map the validator passes holds what a round needs | `MapValidatorTests`; `BundledMapTests`; a bot round | LE2, LE3 |
| Every peer builds the same ground | no float in the file; exact samples; the hash | LE2, LE7 |
| A server without meshes builds and bakes it | the stripped export's check | LE3 |
| Nothing is in the way of a placed prefab | `PrefabPlacementTests`; LE5's checks | LE2, LE5 |
| A road keeps its grade; a bridge carries units and a 4x4 | `RoadPathTests`; LE6's checks | LE6 |
| A client gets the map it lacks, and only a whole and valid one | `MapTransferTests`; LE7's checks | LE7 |
| A server takes no map from anyone without the token | `MapTransferTests`; LE8's checks | LE8 |

## 13. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| **The navigation bake on terrain is slow**, at the first tick or at every structure | `[rts] navigation re-baked … ms of it on the tick` | Measured by LE0 (§7.3): 2.0–2.3 s at the first tick on hills, 6–11 ms on the tick at a re-bake and 2 s to the new mesh. Not too much to build on; if the 2 s shows, tiles first |
| **Bodies catch on the collider's creases**, or prediction drifts on slopes | mispredictions a second above the flat baseline; visible hitching | Measured by LE0, and decided. They do not catch under 43°, and from there they stall: walkable is 40° (LE-D23). Prediction is corrected up to 2.5 times a second crossing a 20° slope, by 10–16 mm, which is the wire's rounding against a 1 cm threshold and stops at 2 cm (LE-D25). Whether any of it shows is a person's to say, at LE3 |
| **A flat-ground assumption §7.4 missed** | a bot round on hills that does what one on the slab does not | LE0 found one, the 4x4's level hull (LE-D24), and played a bot round on hills (§10). LE3 plays another; the list is where to add it |
| **A hostile map file** from a server | — | A reader that is total, limits before allocation, a known inflated size, no scene and no object decoded, and tests over the hostile cases (§6.8). A client decodes only after the hash matches what it was told |
| **The upload socket is write access to a public box** | — | Off unless a token was given; the deploy unit gives none; HMAC with a nonce; one upload at a time; the cache directory only; validated before written |
| **Two builds disagree about a prefab** | a wall drawn where the server has a box of another size | The catalog is append-only and a test holds it; the file names the count it was made against; a client refuses ids it lacks. Sizes changing under an id is LAN.md §5's problem, unchanged |
| **The editor grows** | LE4–LE6 overrun | Destructive edits, one field size, no painting, no layers (§14). LE0–LE3 with LE7 ships without it |
| **Hand-built UI is slow to make** | LE4's four days gone on panels | The decisions are engine-free and tested; the widgets are the menu's kind, and the console's commands do everything a panel does |
| **Water that only stops units** reads as a bug | a player found under it | LE-D8 is flagged; §14 names the rules that would follow |
| **The √2 on faces** leaves a speck the bake will not walk | a hole in the mesh at a pad's corner or a cone's foot | The skirt is 30°, which the √2 keeps under the 40° that is walkable (LE-D23); the slope overlay draws faces; the deep check catches a cut-off pad |
| **A tick on hills runs out of time**: with a round of bots, 1 in 20 over 14–17 ms and 1 in 100 over 20–23 ms of a 16.7 ms tick, where the slab's worst 1 in 100 is under 10 ms | `level-editor-checks.sh frame`; a server's clients resynchronising | Measured by LE0 on four cores of a cloud VM; the box is DEPLOYMENT.md's and is LE3's to measure. The collider is the cheaper of the two and its chunks cost nothing; what terrain and hills add is not taken apart. LE3 does that before it bundles a map with hills on it, and a first bundled map can be gentler than §10's hills, whose steepest face is 59.6° |
| **A ray through the ground** | a round or a sight line that a hill should have stopped | Found by LE0 in `HeightMapShape3D` and designed out (LE-D5); `level-editor-checks.sh ground` casts 10,000 at the collider, and holds it to none |
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
