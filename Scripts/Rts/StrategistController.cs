using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Ui;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// The strategist's half of the game: a camera over the map, a box select and five
/// orders (docs/IMPLEMENTATION_PLAN.md §M3).
///
/// Entirely client-side and entirely presentation. Nothing here simulates
/// anything — it turns clicks into the two requests the server accepts, an order
/// and a build, and shows the marker immediately because 100–250 ms of command
/// latency is what an RTS feels like and there is nothing to predict
/// (docs/IMPLEMENTATION_PLAN.md §2, docs/NETCODE.md §6.3).
///
/// It lives on the render frame, like the viewmodel and the pause menu, and for
/// the same reason: the simulation may not read <c>Input</c> or <c>_Process</c>
/// (docs/IMPLEMENTATION_PLAN.md §3), and a camera is not the simulation.
/// </summary>
public partial class StrategistController : Node3D
{
	private const float MinDistance = 15f;
	private const float MaxDistance = 140f;
	private const float ZoomStep = 8f;

	/// <summary>Metres per second at the closest zoom. Panning scales with distance, or the far view crawls.</summary>
	private const float PanSpeed = 22f;

	private const float RotateSpeed = 2.2f;

	/// <summary>Pixels from the window edge that start an edge scroll.</summary>
	private const float EdgeScrollMargin = 12f;

	/// <summary>Pixels of mouse travel below which a press-and-release is a click, not a drag.</summary>
	private const float ClickThresholdPixels = 6f;

	/// <summary>Pixels from the cursor a unit or player may be and still count as under it.</summary>
	private const float PickRadiusPixels = 36f;

	/// <summary>How far the ground cursor ray is willing to travel.</summary>
	private const float CursorRayLength = 4000f;

	private readonly List<Unit> _selected = new();

	/// <summary>Rebuilt every simulation tick and handed to the overlay to draw.</summary>
	private readonly List<SelectionOverlay.Contact> _contacts = new();

	private Node3D _pivot;
	private Node3D _arm;
	private Camera3D _camera;
	private StrategistHud _hud;

	private float _yaw;
	private float _pitch = Mathf.DegToRad(55f);
	private float _distance = 55f;

	private Vector2 _dragFrom;
	private bool _dragging;

	/// <summary>What the next right-click means. Reset to Move after each order is given.</summary>
	private OrderKind _pendingOrder = OrderKind.Move;

	/// <summary>No structure armed: the next right-click is an order.</summary>
	private const byte NotPlacing = byte.MaxValue;

	/// <summary>
	/// The structure the next right-click puts up, or <see cref="NotPlacing"/>
	/// (docs/NETCODE.md §10.5). Armed by 5/6/7 the way X/C/B arm an order, and spent
	/// by one placement for the same reason: a key that stayed armed would turn the
	/// next move order into a pillbox.
	/// </summary>
	private byte _placing = NotPlacing;

	/// <summary>Scratch for the client's own guess at whether a site is clear. Rebuilt per frame while placing.</summary>
	private readonly Footprint[] _footprints = new Footprint[SimConfig.MaxStructures];
	private readonly Vector3[] _doors = new Vector3[SimConfig.MaxBarracks];

	public override void _Ready()
	{
		// The camera stops when the pause menu is up, unlike the netcode: this is the
		// player's view, and a view that keeps panning behind a menu is a view that
		// has moved when they come back.
		ProcessMode = ProcessModeEnum.Pausable;

		BuildRig();

		_hud = new StrategistHud { Name = "StrategistHud" };
		AddChild(_hud);
		_hud.Overlay.Attach(_camera);

		// A strategist needs a pointer. The first-person rig takes the mouse back when
		// this node goes away (fps_controller.EnterFirstPerson).
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;

		Pan(dt);
		Rotate(dt);

		_pivot.Rotation = new Vector3(0f, _yaw, 0f);
		_arm.Rotation = new Vector3(-_pitch, 0f, 0f);
		_camera.Position = new Vector3(0f, 0f, _distance);

		if (_dragging)
		{
			_hud.Overlay.SetDrag(true, _dragFrom, GetViewport().GetMousePosition());
		}

		UpdateGhost();
	}

	/// <summary>Called once per simulation tick by the combat manager, which owns this node.</summary>
	public void Refresh(uint tick)
	{
		PruneSelection();

		if (CombatManager.Instance is { } combat)
		{
			RefreshContacts(combat, tick);
			_hud.Refresh(combat.Match, UnitManager.Instance, tick, _selected.Count, _pendingOrder, combat.Economy);
			_hud.RefreshBuilders(UnitManager.Instance, combat.Local?.Team ?? Team.Strategist, _placing,
				CountSelectedBuilders());
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton button)
		{
			HandleMouse(button);
			return;
		}

		if (@event.IsActionPressed("rts_order_attack")) { Arm(OrderKind.Attack); }
		else if (@event.IsActionPressed("rts_order_patrol")) { Arm(OrderKind.Patrol); }
		else if (@event.IsActionPressed("rts_order_defend")) { Arm(OrderKind.Defend); }
		else if (@event.IsActionPressed("rts_order_stop")) { _placing = NotPlacing; IssueOrder(OrderKind.Stop, Vector3.Zero, OwnerId.None); }
		else if (@event.IsActionPressed("rts_select_all")) { SelectAll(); }
		else if (@event.IsActionPressed("rts_build_infantry")) { UnitManager.Instance?.RequestBuild(0, UnitCatalog.Infantry); }
		else if (@event.IsActionPressed("rts_build_technical")) { UnitManager.Instance?.RequestBuild(0, UnitCatalog.Technical); }
		else if (@event.IsActionPressed("rts_build_tank")) { UnitManager.Instance?.RequestBuild(0, UnitCatalog.Tank); }
		else if (@event.IsActionPressed("rts_build_builder")) { UnitManager.Instance?.RequestBuild(0, UnitCatalog.Builder); }
		else if (@event.IsActionPressed("rts_place_pillbox")) { ArmPlacement(StructureKinds.Pillbox); }
		else if (@event.IsActionPressed("rts_place_sandbags")) { ArmPlacement(StructureKinds.SandbagWall); }
		else if (@event.IsActionPressed("rts_place_tower")) { ArmPlacement(StructureKinds.SniperTower); }
		else if (@event.IsActionPressed("rts_cancel_build")) { UnitManager.Instance?.RequestCancelBuild(0); }
		else
		{
			return;
		}

		GetViewport().SetInputAsHandled();
	}

	// ---- contacts (docs/IMPLEMENTATION_PLAN.md §M4) -------------------------

	/// <summary>
	/// What the strategist knows about the enemy (docs/IMPLEMENTATION_PLAN.md §M4).
	///
	/// Two sources for one list, because the fog reaches a client and the authority
	/// by different routes. A client is simply not sent the records, so its evidence
	/// is a record that has stopped arriving and its last known position is where
	/// the body froze. A listen host has the true state in hand and has to be told
	/// what to pretend not to know, so it reads the same
	/// <see cref="VisibilityService"/> the filter is built from
	/// (docs/NETCODE.md §6.2).
	/// </summary>
	private void RefreshContacts(CombatManager combat, uint tick)
	{
		_contacts.Clear();

		Team team = combat.Local?.Team ?? Team.Strategist;

		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);

			// A player the strategist last saw die is not a contact: their client was
			// told the health byte before it lost them, so the corpse is knowledge and
			// not a guess.
			if (player?.Character == null || player.Team == team || !player.IsAlive)
			{
				continue;
			}

			if (!TryContact(combat, player, tick, out Vector3 position, out float age))
			{
				continue;
			}

			float fade = Fog.GhostAlpha(age);
			if (fade <= 0f)
			{
				continue;
			}

			_contacts.Add(new SelectionOverlay.Contact(position, fade, !Fog.IsLost(age)));
		}

		_hud.Overlay.SetContacts(_contacts);
	}

	/// <summary>
	/// Where this player was last seen and how long ago, from whichever of the two
	/// routes this process has. False when it has never been seen at all.
	/// </summary>
	private static bool TryContact(CombatManager combat, PlayerCombat player, uint tick, out Vector3 position,
		out float ageTicks)
	{
		if (NetworkManager.Instance is { IsClient: true })
		{
			// The records stopped: the body is frozen where the last one put it.
			position = player.Character.SimPosition;
			ageTicks = PlayerManager.Instance?.SnapshotAgeTicks(player.PeerId) ?? float.MaxValue;
			return ageTicks < float.MaxValue;
		}

		position = Vector3.Zero;
		ageTicks = float.MaxValue;
		return combat.Visibility != null && combat.Visibility.TryContact(player.PeerId, tick, out position,
			out ageTicks);
	}

	// ---- camera ------------------------------------------------------------

	private void BuildRig()
	{
		_pivot = new Node3D { Name = "Pivot" };
		AddChild(_pivot);

		_arm = new Node3D { Name = "Arm" };
		_pivot.AddChild(_arm);

		_camera = new Camera3D { Name = "StrategistCamera", Far = 2000f };
		_arm.AddChild(_camera);
		_camera.Current = true;

		// Starts over the map's anchor if it has one, and over the origin otherwise.
		foreach (Node node in GetTree().GetNodesInGroup("strategist_anchor"))
		{
			if (node is Node3D anchor)
			{
				_pivot.GlobalPosition = anchor.GlobalPosition;
				break;
			}
		}
	}

	private void Pan(float dt)
	{
		if (Gdpyr.Core.InputFocus.TextEntry)
		{
			// Typing a demo's name into the console must not pan the map
			// (Scripts/Core/InputFocus.cs).
			return;
		}

		Vector2 axes = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
		axes += EdgeScroll();

		if (axes == Vector2.Zero)
		{
			return;
		}

		// Panning is relative to where the camera is pointed, not to the world: a
		// rotated view whose WASD still walks north is unusable.
		float speed = PanSpeed * (_distance / MinDistance) * dt;
		Vector3 forward = new Vector3(-Mathf.Sin(_yaw), 0f, -Mathf.Cos(_yaw));
		Vector3 right = new Vector3(Mathf.Cos(_yaw), 0f, -Mathf.Sin(_yaw));

		_pivot.GlobalPosition += ((right * axes.X) + (forward * -axes.Y)) * speed;
	}

	/// <summary>Screen edges as a second pan axis. Ignored while dragging out a selection box.</summary>
	private Vector2 EdgeScroll()
	{
		if (_dragging)
		{
			return Vector2.Zero;
		}

		Vector2 size = GetViewport().GetVisibleRect().Size;
		Vector2 mouse = GetViewport().GetMousePosition();

		if (mouse.X < 0f || mouse.Y < 0f || mouse.X > size.X || mouse.Y > size.Y)
		{
			return Vector2.Zero;
		}

		var scroll = Vector2.Zero;
		if (mouse.X <= EdgeScrollMargin) { scroll.X -= 1f; }
		if (mouse.X >= size.X - EdgeScrollMargin) { scroll.X += 1f; }

		// Screen Y grows downwards and the pan axis is forward-positive-up, so the
		// top edge is -Y here exactly as `move_forward` is.
		if (mouse.Y <= EdgeScrollMargin) { scroll.Y -= 1f; }
		if (mouse.Y >= size.Y - EdgeScrollMargin) { scroll.Y += 1f; }

		return scroll;
	}

	private void Rotate(float dt)
	{
		if (Gdpyr.Core.InputFocus.TextEntry)
		{
			return;
		}

		if (Input.IsActionPressed("rts_rotate_left")) { _yaw += RotateSpeed * dt; }
		if (Input.IsActionPressed("rts_rotate_right")) { _yaw -= RotateSpeed * dt; }
	}

	// ---- mouse -------------------------------------------------------------

	private void HandleMouse(InputEventMouseButton button)
	{
		if (button.IsAction("rts_zoom_in") && button.Pressed)
		{
			_distance = Mathf.Clamp(_distance - ZoomStep, MinDistance, MaxDistance);
			GetViewport().SetInputAsHandled();
			return;
		}

		if (button.IsAction("rts_zoom_out") && button.Pressed)
		{
			_distance = Mathf.Clamp(_distance + ZoomStep, MinDistance, MaxDistance);
			GetViewport().SetInputAsHandled();
			return;
		}

		if (button.IsAction("rts_select"))
		{
			if (button.Pressed)
			{
				_dragFrom = button.Position;
				_dragging = true;
			}
			else if (_dragging)
			{
				_dragging = false;
				_hud.Overlay.SetDrag(false, Vector2.Zero, Vector2.Zero);
				FinishSelection(_dragFrom, button.Position);
			}

			GetViewport().SetInputAsHandled();
			return;
		}

		if (button.IsAction("rts_command") && button.Pressed)
		{
			CommandAtCursor();
			GetViewport().SetInputAsHandled();
		}
	}

	private void FinishSelection(Vector2 from, Vector2 to)
	{
		_selected.Clear();

		UnitManager units = UnitManager.Instance;
		Team team = CombatManager.Instance?.Local?.Team ?? Team.Strategist;

		if (units != null)
		{
			if (from.DistanceTo(to) < ClickThresholdPixels)
			{
				Unit single = PickUnit(to, team);
				if (single != null)
				{
					_selected.Add(single);
				}
			}
			else
			{
				Rect2 rect = new Rect2(from, to - from).Abs();
				for (int i = 0; i < units.SlotCount; i++)
				{
					Unit unit = units.UnitAt(i);
					if (IsSelectable(unit, team) && TryScreen(unit.GlobalPosition, out Vector2 screen)
						&& rect.HasPoint(screen))
					{
						_selected.Add(unit);
					}
				}
			}
		}

		_hud.Overlay.SetSelection(_selected);
	}

	private void SelectAll()
	{
		_selected.Clear();

		UnitManager units = UnitManager.Instance;
		Team team = CombatManager.Instance?.Local?.Team ?? Team.Strategist;

		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (IsSelectable(unit, team))
			{
				_selected.Add(unit);
			}
		}

		_hud.Overlay.SetSelection(_selected);
	}

	/// <summary>
	/// A right-click. Move by default; an enemy under the cursor turns it into an
	/// attack on that enemy, which is what a right-click means in every RTS anyone
	/// has played.
	/// </summary>
	private void CommandAtCursor()
	{
		if (!TryGroundPoint(out Vector3 point))
		{
			return;
		}

		if (_placing != NotPlacing)
		{
			PlaceAtCursor(point);
			return;
		}

		// Builders right-clicked onto one of their own side's structures go and work
		// on it: finish it if it is a site, mend it if it is hurt.
		if (TryAssistAtCursor())
		{
			return;
		}

		OrderKind kind = _pendingOrder;
		int targetOwnerId = OwnerId.None;

		PlayerCombat enemy = PickEnemyPlayer(GetViewport().GetMousePosition(), out Vector3 lastKnown);
		if (enemy != null)
		{
			kind = OrderKind.Attack;
			targetOwnerId = OwnerId.ForPeer(enemy.PeerId);

			// Where they were last seen, not where they are: the order carries the
			// strategist's knowledge, and the units walk to it and look for whoever it
			// named when they get there (UnitManager.AcquireTarget).
			point = lastKnown;
		}

		IssueOrder(kind, point, targetOwnerId);
	}

	private void IssueOrder(OrderKind kind, Vector3 point, int targetOwnerId)
	{
		UnitManager units = UnitManager.Instance;
		if (units == null)
		{
			return;
		}

		PruneSelection();

		if (_selected.Count == 0)
		{
			// Nothing selected: a right-click on the ground moves the rally point, so
			// that queueing twenty riflemen and pointing them somewhere is two clicks
			// rather than two clicks per rifleman.
			if (kind != OrderKind.Stop)
			{
				units.RequestRally(0, point);
				_hud.Overlay.FlashOrder(point, OrderKind.Move);
			}
			return;
		}

		var ids = new int[_selected.Count];
		for (int i = 0; i < _selected.Count; i++)
		{
			ids[i] = _selected[i].UnitId;
		}

		units.RequestOrder(ids, kind, point, targetOwnerId);
		_hud.Overlay.FlashOrder(point, kind);

		// One order per modifier press: leaving attack-move armed is how a retreat
		// becomes a charge.
		_pendingOrder = OrderKind.Move;
	}

	// ---- builders (docs/NETCODE.md §10.5) -------------------------------------

	/// <summary>Arms an order for the next right-click, which disarms any structure.</summary>
	private void Arm(OrderKind kind)
	{
		_pendingOrder = kind;
		_placing = NotPlacing;
	}

	/// <summary>
	/// Arms a structure for the next right-click. The same key again puts it away,
	/// which is the only way out of placement besides Z and a placement.
	/// </summary>
	private void ArmPlacement(byte kind)
	{
		_placing = _placing == kind ? NotPlacing : kind;
		_pendingOrder = OrderKind.Move;
	}

	/// <summary>
	/// Sends the selected builders to put the armed structure up at
	/// <paramref name="point"/>, facing the way the camera is turned: a wall lies
	/// across the screen, so Q and E are how one is turned. Everything else about
	/// whether it fits is the server's to say.
	/// </summary>
	private void PlaceAtCursor(Vector3 point)
	{
		int[] builders = SelectedBuilderIds();
		if (builders.Length == 0)
		{
			// Nothing to build it with: stay armed, so selecting a builder and
			// clicking again is all it takes.
			return;
		}

		UnitManager.Instance?.RequestConstruct(builders, _placing, point, _yaw);
		_hud.Overlay.FlashOrder(point, OrderKind.Defend);
		_placing = NotPlacing;
	}

	/// <summary>Builders in the selection, onto a site or a hurt structure of their own side under the cursor.</summary>
	private bool TryAssistAtCursor()
	{
		PruneSelection();
		if (CountSelectedBuilders() == 0)
		{
			return false;
		}

		Structure structure = PickStructure(GetViewport().GetMousePosition(),
			CombatManager.Instance?.Local?.Team ?? Team.Strategist);
		if (structure == null || (structure.IsBuilt && structure.Health >= structure.MaxHealth))
		{
			return false;
		}

		UnitManager.Instance?.RequestAssist(SelectedBuilderIds(), structure.Slot);
		_hud.Overlay.FlashOrder(structure.Pose.Base, OrderKind.Defend);
		return true;
	}

	private int CountSelectedBuilders()
	{
		int count = 0;
		for (int i = 0; i < _selected.Count; i++)
		{
			Unit unit = _selected[i];
			if (unit != null && IsInstanceValid(unit) && unit.IsAlive && UnitCatalog.CanConstruct(unit.DefinitionId))
			{
				count++;
			}
		}
		return count;
	}

	private int[] SelectedBuilderIds()
	{
		var ids = new int[CountSelectedBuilders()];
		int at = 0;
		for (int i = 0; i < _selected.Count && at < ids.Length; i++)
		{
			Unit unit = _selected[i];
			if (unit != null && IsInstanceValid(unit) && unit.IsAlive && UnitCatalog.CanConstruct(unit.DefinitionId))
			{
				ids[at++] = unit.UnitId;
			}
		}
		return ids;
	}

	/// <summary>
	/// Draws the armed structure where the cursor is, green where the flat rules
	/// allow it and red where they do not. A guess: the server also asks the world
	/// whether anything is in the way and whether a builder can walk there, which a
	/// client could answer only with a navigation mesh it does not have.
	/// </summary>
	private void UpdateGhost()
	{
		if (_placing == NotPlacing || _dragging || !TryGroundPoint(out Vector3 point))
		{
			_hud.Overlay.SetGhost(false, default, false);
			return;
		}

		StructureShape shape = StructureCatalog.ShapeOf(_placing);
		Footprint ghost = new StructurePose(point, _yaw).FootprintOf(shape);

		UnitManager units = UnitManager.Instance;
		int existing = 0;
		for (int i = 0; units != null && i < SimConfig.MaxStructures; i++)
		{
			if (units.StructureAt(i) is { IsDestroyed: false } structure)
			{
				_footprints[existing++] = structure.Footprint;
			}
		}

		int doors = 0;
		for (int i = 0; units != null && i < units.BarracksCount && doors < _doors.Length; i++)
		{
			_doors[doors++] = units.BarracksAt(i).SpawnPosition;
		}

		bool clear = StructurePlacement.Check(ghost, _footprints.AsSpan(0, existing), _doors.AsSpan(0, doors))
			== PlacementResult.Ok;
		bool affordable = CombatManager.Instance is not { } combat
			|| combat.Match.StrategistPoints >= StructureCatalog.CostOf(_placing);

		_hud.Overlay.SetGhost(true, ghost, clear && affordable && CountSelectedBuilders() > 0);
	}

	/// <summary>The structure of <paramref name="team"/>'s under the cursor, if any.</summary>
	private Structure PickStructure(Vector2 screen, Team team)
	{
		UnitManager units = UnitManager.Instance;
		Structure best = null;
		float bestDistance = PickRadiusPixels * 1.5f;

		for (int i = 0; units != null && i < SimConfig.MaxStructures; i++)
		{
			Structure structure = units.StructureAt(i);
			if (structure == null || !IsInstanceValid(structure) || structure.IsDestroyed || structure.Team != team
				|| !TryScreen(structure.AimPoint, out Vector2 at))
			{
				continue;
			}

			float distance = at.DistanceTo(screen);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = structure;
			}
		}

		return best;
	}

	// ---- picking -----------------------------------------------------------

	private static bool IsSelectable(Unit unit, Team team) =>
		unit != null && IsInstanceValid(unit) && unit.IsAlive && unit.Team == team;

	private Unit PickUnit(Vector2 screen, Team team)
	{
		UnitManager units = UnitManager.Instance;
		Unit best = null;
		float bestDistance = PickRadiusPixels;

		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (!IsSelectable(unit, team) || !TryScreen(unit.GlobalPosition + Vector3.Up, out Vector2 at))
			{
				continue;
			}

			float distance = at.DistanceTo(screen);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = unit;
			}
		}

		return best;
	}

	/// <summary>
	/// The enemy under the cursor, if the strategist still has any idea where one
	/// is. It picks from the same contacts the markers are drawn from rather than
	/// from the roster, so a player whose ghost has faded cannot be right-clicked
	/// where their body happens to be (docs/IMPLEMENTATION_PLAN.md §M4).
	/// </summary>
	private PlayerCombat PickEnemyPlayer(Vector2 screen, out Vector3 lastKnown)
	{
		CombatManager combat = CombatManager.Instance;
		Team team = combat?.Local?.Team ?? Team.Strategist;
		uint tick = NetworkManager.Instance?.Tick ?? 0;
		PlayerCombat best = null;
		float bestDistance = PickRadiusPixels;
		lastKnown = Vector3.Zero;

		for (int i = 0; combat != null && i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player?.Character == null || !player.IsAlive || player.Team == team)
			{
				continue;
			}

			if (!TryContact(combat, player, tick, out Vector3 position, out float age)
				|| Fog.IsForgotten(age))
			{
				continue;
			}

			if (!TryScreen(position + Vector3.Up, out Vector2 at))
			{
				continue;
			}

			float distance = at.DistanceTo(screen);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = player;
				lastKnown = position;
			}
		}

		return best;
	}

	private bool TryScreen(Vector3 world, out Vector2 screen)
	{
		screen = Vector2.Zero;
		if (_camera == null || _camera.IsPositionBehind(world))
		{
			return false;
		}

		screen = _camera.UnprojectPosition(world);
		return true;
	}

	/// <summary>
	/// Where the cursor is on the ground. The world geometry first, so a click on a
	/// roof orders units to the roof rather than to the floor below it; the ground
	/// plane as a fallback, so a click on the sky still resolves to somewhere.
	/// </summary>
	private bool TryGroundPoint(out Vector3 point)
	{
		point = Vector3.Zero;
		if (_camera == null)
		{
			return false;
		}

		Vector2 mouse = GetViewport().GetMousePosition();
		Vector3 from = _camera.ProjectRayOrigin(mouse);
		Vector3 direction = _camera.ProjectRayNormal(mouse);

		PhysicsDirectSpaceState3D space = GetWorld3D()?.DirectSpaceState;
		if (space != null)
		{
			var query = PhysicsRayQueryParameters3D.Create(from, from + (direction * CursorRayLength),
				CollisionLayers.World);
			Godot.Collections.Dictionary hit = space.IntersectRay(query);
			if (hit.Count > 0)
			{
				point = hit["position"].AsVector3();
				return true;
			}
		}

		Vector3? plane = new Plane(Vector3.Up, 0f).IntersectsRay(from, direction);
		if (plane is { } ground)
		{
			point = ground;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Drops units that have died or been freed. Selections are held across ticks
	/// and units are freed from under them, so every consumer has to assume the list
	/// is stale.
	/// </summary>
	private void PruneSelection()
	{
		bool changed = false;
		for (int i = _selected.Count - 1; i >= 0; i--)
		{
			Unit unit = _selected[i];
			if (unit == null || !IsInstanceValid(unit) || !unit.IsAlive)
			{
				_selected.RemoveAt(i);
				changed = true;
			}
		}

		if (changed)
		{
			_hud?.Overlay.SetSelection(_selected);
		}
	}
}
