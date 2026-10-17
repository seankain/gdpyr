using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// The drag rectangle, the brackets round what is selected and the marker where
/// the last order went.
///
/// This is the pyrrhic <c>SquareDrawer</c>/<c>SelectSquare</c> concept ported
/// rather than the code (docs/IMPLEMENTATION_PLAN.md §1): the rectangle is drawn
/// in screen space and the selection test is <c>UnprojectPosition</c> against it,
/// which is what a frustum select comes to for point-like units and is a great
/// deal less code than building six planes.
///
/// It draws from world positions every frame rather than caching screen ones, so
/// brackets stay on their units while the camera pans under them.
///
/// M4 gave it the other half of the strategist's picture: the contacts its units
/// have, and the ghosts of the ones they have lost
/// (docs/IMPLEMENTATION_PLAN.md §M4).
/// </summary>
public partial class SelectionOverlay : Control
{
	/// <summary>Seconds an order marker stays up.</summary>
	private const float OrderMarkerSeconds = 0.8f;

	/// <summary>Pixels across an enemy contact marker.</summary>
	private const float ContactMarkerPixels = 14f;

	/// <summary>
	/// Metres from its middle to the ring drawn round a selected barracks: clear of
	/// the corners of the greybox's 8 m building.
	/// </summary>
	private const float BarracksRingMeters = 6.5f;

	private const int BarracksRingSegments = 32;

	/// <summary>Metres outside a selected structure's corners its ring is drawn.</summary>
	private const float StructureRingClearanceMeters = 0.8f;

	private static readonly Color SelectionColor = new(0.45f, 0.95f, 0.55f);
	private static readonly Color DragColor = new(0.45f, 0.95f, 0.55f, 0.85f);
	private static readonly Color DragFillColor = new(0.45f, 0.95f, 0.55f, 0.12f);
	private static readonly Color ContactColor = new(1f, 0.35f, 0.3f);

	/// <summary>
	/// One enemy the strategist knows about: either a live contact one of its units
	/// has right now, or the last place a lost one was seen
	/// (docs/NETCODE.md §6.2). Stale information is the point — it decays rather
	/// than disappearing.
	/// </summary>
	public readonly struct Contact
	{
		public readonly Vector3 Position;

		/// <summary>1 while the contact is fresh, falling to 0 as a lost one fades out.</summary>
		public readonly float Fade;

		/// <summary>True while a unit still has eyes on it.</summary>
		public readonly bool Live;

		public Contact(Vector3 position, float fade, bool live)
		{
			Position = position;
			Fade = fade;
			Live = live;
		}
	}

	private readonly List<Unit> _selected = new();
	private readonly List<Contact> _contacts = new();

	private Barracks _barracks;
	private Structure _structure;

	private Camera3D _camera;
	private Vector2 _dragFrom;
	private Vector2 _dragTo;
	private bool _dragging;

	private Vector3 _orderPoint;
	private OrderKind _orderKind;
	private float _orderAge = float.MaxValue;

	/// <summary>The last marker was a refusal — a vehicle with no free seat — drawn as a cross.</summary>
	private bool _orderRefused;

	/// <summary>Pixels across one seat pip over a vehicle, and between two.</summary>
	private const float SeatPipPixels = 6f;
	private const float SeatPipGapPixels = 2f;

	private static readonly Color SeatColor = new(0.4f, 0.9f, 1f);
	private static readonly Color RefusedColor = new(1f, 0.35f, 0.3f);

	private static readonly Color GhostColor = new(0.45f, 0.95f, 0.55f, 0.9f);
	private static readonly Color BlockedGhostColor = new(1f, 0.35f, 0.3f, 0.9f);
	private static readonly Color ProgressColor = new(1f, 0.85f, 0.35f);
	private static readonly Color HealthColor = new(0.45f, 0.95f, 0.55f);
	private static readonly Color BarBackColor = new(0f, 0f, 0f, 0.6f);

	private bool _ghostActive;
	private bool _ghostPlausible;
	private Footprint _ghost;
	private readonly Vector2[] _ghostScreen = new Vector2[5];

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		AnchorRight = 1f;
		AnchorBottom = 1f;
	}

	public void Attach(Camera3D camera) => _camera = camera;

	public void SetDrag(bool active, Vector2 from, Vector2 to)
	{
		_dragging = active;
		_dragFrom = from;
		_dragTo = to;
	}

	/// <summary>
	/// Takes a copy of the selection. A copy, because units are freed from under it
	/// when they die and a list the controller also mutates would be drawn
	/// mid-edit.
	/// </summary>
	public void SetSelection(IReadOnlyList<Unit> units)
	{
		_selected.Clear();
		for (int i = 0; i < units.Count; i++)
		{
			_selected.Add(units[i]);
		}
	}

	/// <summary>The barracks the strategist has clicked on, or null.</summary>
	public void SetSelectedBarracks(Barracks barracks) => _barracks = barracks;

	/// <summary>The sniper tower or pillbox the strategist has clicked on for its training queue, or null (docs/NETCODE.md §10.8).</summary>
	public void SetSelectedStructure(Structure structure) => _structure = structure;

	/// <summary>
	/// Takes a copy of what the strategist can see, rebuilt every simulation tick by
	/// <see cref="StrategistController"/>. A copy for the same reason the selection
	/// is one: the list it comes from is rebuilt while this one is being drawn.
	/// </summary>
	public void SetContacts(IReadOnlyList<Contact> contacts)
	{
		_contacts.Clear();
		for (int i = 0; i < contacts.Count; i++)
		{
			_contacts.Add(contacts[i]);
		}
	}

	/// <summary>
	/// The structure the next right-click will place, drawn where it would stand
	/// (docs/NETCODE.md §10.5). <paramref name="plausible"/> is the client's guess;
	/// the server has the last word.
	/// </summary>
	public void SetGhost(bool active, in Footprint footprint, bool plausible)
	{
		_ghostActive = active;
		_ghost = footprint;
		_ghostPlausible = plausible;
	}

	public void FlashOrder(Vector3 point, OrderKind kind)
	{
		_orderPoint = point;
		_orderKind = kind;
		_orderAge = 0f;
		_orderRefused = false;
	}

	/// <summary>A cross where an order was not sent, because the client could see it would change nothing: a vehicle that is full (docs/VEHICLES.md V-D4).</summary>
	public void FlashRefused(Vector3 point)
	{
		FlashOrder(point, OrderKind.None);
		_orderRefused = true;
	}

	public override void _Process(double delta)
	{
		if (_orderAge < OrderMarkerSeconds)
		{
			_orderAge += (float)delta;
		}

		// Everything here is derived from a camera that moves every frame, so there
		// is nothing to gain from redrawing only on change.
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (_camera == null)
		{
			return;
		}

		for (int i = 0; i < _selected.Count; i++)
		{
			Unit unit = _selected[i];
			if (unit == null || !IsInstanceValid(unit) || !unit.IsOnField)
			{
				continue;
			}

			if (!TryScreen(unit.GlobalPosition + Vector3.Up, out Vector2 screen))
			{
				continue;
			}

			// Brackets scaled by distance, so a unit across the map is a small mark
			// and one under the cursor is a big one.
			float size = Mathf.Clamp(600f / Mathf.Max(1f, _camera.GlobalPosition.DistanceTo(unit.GlobalPosition)),
				6f, 40f);
			DrawRect(new Rect2(screen - (Vector2.One * size * 0.5f), Vector2.One * size), SelectionColor,
				filled: false, width: 1.5f);
		}

		DrawSelectedBarracks();
		DrawContacts();
		DrawStructures();
		DrawSeats();
		DrawAircraftShadows();
		DrawGhost();

		if (_orderAge < OrderMarkerSeconds && TryScreen(_orderPoint, out Vector2 marker))
		{
			float fade = 1f - (_orderAge / OrderMarkerSeconds);
			Color color = _orderRefused ? RefusedColor : OrderColor(_orderKind);
			color.A = fade;
			if (_orderRefused)
			{
				DrawLine(marker - new Vector2(7f, 7f), marker + new Vector2(7f, 7f), color, 2f);
				DrawLine(marker - new Vector2(7f, -7f), marker + new Vector2(7f, -7f), color, 2f);
			}
			else
			{
				DrawLine(marker - new Vector2(8f, 0f), marker + new Vector2(8f, 0f), color, 2f);
				DrawLine(marker - new Vector2(0f, 8f), marker + new Vector2(0f, 8f), color, 2f);
			}
		}

		if (!_dragging)
		{
			return;
		}

		Rect2 rect = new Rect2(_dragFrom, _dragTo - _dragFrom).Abs();
		DrawRect(rect, DragFillColor);
		DrawRect(rect, DragColor, filled: false, width: 1.5f);
	}

	/// <summary>
	/// A ring on the ground round the selected barracks or structure, in the selection colour.
	/// On the ground rather than a bracket on the screen, because a building is
	/// big enough for the camera to be close to one side of it; drawn a segment at a
	/// time so the part behind a low camera drops out on its own.
	/// </summary>
	private void DrawSelectedBarracks()
	{
		if (_barracks != null && IsInstanceValid(_barracks))
		{
			DrawGroundRing(_barracks.GlobalPosition, BarracksRingMeters);
		}

		// A selected structure gets the same ring, just clear of its corners.
		if (_structure != null && IsInstanceValid(_structure) && !_structure.IsDestroyed)
		{
			Footprint footprint = _structure.Footprint;
			float radius = new Vector2(footprint.HalfWidth, footprint.HalfDepth).Length() + StructureRingClearanceMeters;
			DrawGroundRing(_structure.Pose.Base, radius);
		}
	}

	/// <summary>How far down from an aircraft the ground under it is looked for, for its ring.</summary>
	private const float AircraftGroundProbeMeters = 130f;

	private PhysicsRayQueryParameters3D _groundRay;

	/// <summary>
	/// A ring on the ground under each of the strategist's aircraft that is off it
	/// (docs/VEHICLES.md §7), the hull's width: fourteen metres up, a dropship is hard to
	/// place against the map without one, and the ring is where it will come down. One
	/// world-layer ray each, on this peer, a frame.
	/// </summary>
	private void DrawAircraftShadows()
	{
		UnitManager units = UnitManager.Instance;
		PhysicsDirectSpaceState3D space = _camera?.GetWorld3D()?.DirectSpaceState;
		Team side = CombatManager.Instance?.Local?.Team ?? Team.Strategist;
		for (int i = 0; units != null && space != null && i < units.SlotCount; i++)
		{
			Unit aircraft = units.UnitAt(i);
			if (aircraft == null || !IsInstanceValid(aircraft) || !aircraft.IsAircraft || !aircraft.IsOnField
				|| aircraft.Phase == FlightPhase.Grounded || aircraft.Team != side)
			{
				continue;
			}

			Vector3 at = aircraft.GlobalPosition;
			_groundRay ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
			_groundRay.From = at;
			_groundRay.To = at - (Vector3.Up * AircraftGroundProbeMeters);
			Godot.Collections.Dictionary hit = space.IntersectRay(_groundRay);
			if (hit.Count > 0)
			{
				DrawGroundRing(hit["position"].AsVector3(), aircraft.Definition?.RadiusMeters ?? 2f,
					aircraft.CannotLand ? RefusedColor : SeatColor);
			}
		}
	}

	private void DrawGroundRing(Vector3 middle, float radius) => DrawGroundRing(middle, radius, SelectionColor);

	private void DrawGroundRing(Vector3 middle, float radius, Color color)
	{
		Vector3 centre = middle + (Vector3.Up * 0.1f);
		bool previousOnScreen = TryScreen(centre + (Vector3.Right * radius), out Vector2 previous);

		for (int i = 1; i <= BarracksRingSegments; i++)
		{
			float angle = Mathf.Tau * i / BarracksRingSegments;
			Vector3 point = centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
			bool onScreen = TryScreen(point, out Vector2 screen);

			if (onScreen && previousOnScreen)
			{
				DrawLine(previous, screen, color, 2f);
			}

			previous = screen;
			previousOnScreen = onScreen;
		}
	}

	/// <summary>
	/// The enemy, as the strategist is allowed to know it: a filled box where a unit
	/// has eyes on somebody, a fading hollow one with a line through it where one
	/// was last seen. Drawn at chest height, because the marker has to sit on the
	/// player and not at their feet when the camera is low.
	/// </summary>
	private void DrawContacts()
	{
		for (int i = 0; i < _contacts.Count; i++)
		{
			Contact contact = _contacts[i];
			if (contact.Fade <= 0f || !TryScreen(contact.Position + Vector3.Up, out Vector2 screen))
			{
				continue;
			}

			Color color = ContactColor;
			color.A = contact.Live ? 1f : contact.Fade;

			var rect = new Rect2(screen - (Vector2.One * ContactMarkerPixels * 0.5f),
				Vector2.One * ContactMarkerPixels);

			if (contact.Live)
			{
				DrawRect(rect, color);
				continue;
			}

			DrawRect(rect, color, filled: false, width: 1.5f);
			DrawLine(rect.Position, rect.End, color, 1.5f);
		}
	}

	/// <summary>
	/// A bar over each of the strategist's structures that is going up or is hurt:
	/// progress for a site, health for a finished one. A whole, finished structure
	/// gets nothing, so the bars on screen are the ones that need a builder.
	/// </summary>
	private void DrawStructures()
	{
		UnitManager units = UnitManager.Instance;
		for (int i = 0; units != null && i < SimConfig.MaxStructures; i++)
		{
			Structure structure = units.StructureAt(i);
			if (structure == null || !IsInstanceValid(structure) || structure.IsDestroyed)
			{
				continue;
			}

			bool site = !structure.IsBuilt;
			float fraction = site ? structure.Progress : structure.Health / Mathf.Max(structure.MaxHealth, 1f);
			if (!site && fraction >= 0.999f)
			{
				continue;
			}

			Vector3 over = structure.Pose.Base + (Vector3.Up * (structure.Shape.Height + 1f));
			if (!TryScreen(over, out Vector2 screen))
			{
				continue;
			}

			const float Width = 40f;
			const float Height = 5f;
			var back = new Rect2(screen - new Vector2(Width * 0.5f, Height * 0.5f), new Vector2(Width, Height));
			DrawRect(back, BarBackColor);
			DrawRect(new Rect2(back.Position, new Vector2(Width * Mathf.Clamp(fraction, 0f, 1f), Height)),
				site ? ProgressColor : HealthColor);

			if (structure.IsObstructed)
			{
				DrawRect(back.Grow(2f), BlockedGhostColor, filled: false, width: 1.5f);
			}
		}
	}

	/// <summary>
	/// A pip per seat over each of the strategist's vehicles that carry units, filled
	/// for each one aboard (docs/VEHICLES.md §7) — drawn over every one of them, not only
	/// the selected, as the structures' bars are, because a truck's load is what makes
	/// it worth clicking.
	/// </summary>
	private void DrawSeats()
	{
		UnitManager units = UnitManager.Instance;
		Team side = CombatManager.Instance?.Local?.Team ?? Team.Strategist;
		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit vehicle = units.UnitAt(i);
			if (vehicle == null || !IsInstanceValid(vehicle) || !vehicle.HasSeats || !vehicle.IsOnField
				|| vehicle.Team != side)
			{
				continue;
			}

			float height = vehicle.Definition?.HeightMeters ?? 2f;
			if (!TryScreen(vehicle.GlobalPosition + (Vector3.Up * (height + 0.8f)), out Vector2 screen))
			{
				continue;
			}

			int seats = vehicle.Cargo.Seats;
			int aboard = Mathf.Min(vehicle.AboardCount, seats);
			float width = (seats * SeatPipPixels) + ((seats - 1) * SeatPipGapPixels);
			Vector2 at = screen - new Vector2(width * 0.5f, SeatPipPixels * 0.5f);
			for (int s = 0; s < seats; s++)
			{
				var pip = new Rect2(at + new Vector2(s * (SeatPipPixels + SeatPipGapPixels), 0f),
					Vector2.One * SeatPipPixels);
				DrawRect(pip, s < aboard ? SeatColor : BarBackColor);
				DrawRect(pip, SeatColor, filled: false, width: 1f);
			}
		}
	}

	private void DrawGhost()
	{
		if (!_ghostActive)
		{
			return;
		}

		for (int i = 0; i < 4; i++)
		{
			if (!TryScreen(_ghost.Corner(i), out _ghostScreen[i]))
			{
				return;
			}
		}

		_ghostScreen[4] = _ghostScreen[0];
		DrawPolyline(_ghostScreen, _ghostPlausible ? GhostColor : BlockedGhostColor, 2f);
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

	private static Color OrderColor(OrderKind kind) => kind switch
	{
		OrderKind.Attack => new Color(1f, 0.4f, 0.3f),
		OrderKind.Patrol => new Color(0.5f, 0.7f, 1f),
		OrderKind.Defend => new Color(1f, 0.85f, 0.4f),
		OrderKind.Embark => SeatColor,
		OrderKind.Unload => new Color(0.85f, 0.6f, 1f),
		_ => new Color(0.45f, 0.95f, 0.55f),
	};
}
