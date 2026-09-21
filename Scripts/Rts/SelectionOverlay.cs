using System.Collections.Generic;
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

	private Camera3D _camera;
	private Vector2 _dragFrom;
	private Vector2 _dragTo;
	private bool _dragging;

	private Vector3 _orderPoint;
	private OrderKind _orderKind;
	private float _orderAge = float.MaxValue;

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

	public void FlashOrder(Vector3 point, OrderKind kind)
	{
		_orderPoint = point;
		_orderKind = kind;
		_orderAge = 0f;
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
			if (unit == null || !IsInstanceValid(unit) || !unit.IsAlive)
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

		DrawContacts();

		if (_orderAge < OrderMarkerSeconds && TryScreen(_orderPoint, out Vector2 marker))
		{
			float fade = 1f - (_orderAge / OrderMarkerSeconds);
			Color color = OrderColor(_orderKind);
			color.A = fade;
			DrawLine(marker - new Vector2(8f, 0f), marker + new Vector2(8f, 0f), color, 2f);
			DrawLine(marker - new Vector2(0f, 8f), marker + new Vector2(0f, 8f), color, 2f);
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
		_ => new Color(0.45f, 0.95f, 0.55f),
	};
}
