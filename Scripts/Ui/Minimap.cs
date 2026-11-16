using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The strategist's map, in the bottom-right corner of the screen: the field from straight
/// above, north up, with the side's units, vehicles, barracks and structures on it, and the
/// outline of the ground the camera sees.
///
/// What it shows and how it zooms is <see cref="MinimapView"/>'s. What a click, a drag and the
/// zoom input on it do is <see cref="StrategistController"/>'s: this control hands it every
/// mouse event that lands on the map (<see cref="MapEvent"/>), already in map pixels, and stops
/// them there, so a click on the map is never also a box select or an order on the world
/// behind it, and the wheel over it zooms the map rather than the camera.
///
/// The picture under the markers is the world's own heights, taken once with a ray straight
/// down for each cell against the world layer and shaded as relief
/// (<see cref="MinimapTerrain"/>): no second camera rendering the world, so nothing that moves
/// is in it and it costs nothing after the first second. The rays are spread over frames.
/// Markers are drawn from the units' and structures' positions every frame, as
/// <see cref="SelectionOverlay"/> draws its brackets.
/// </summary>
public partial class Minimap : Control
{
	/// <summary>Pixels across the map, and down it.</summary>
	public const float SizePixels = 208f;

	/// <summary>Cells across the picture of the ground: about 1.5 m each over the field.</summary>
	public const int TerrainCells = 160;

	/// <summary>Rays a frame while the picture is being taken: the whole field in a little over a second.</summary>
	private const int RaysPerFrame = 480;

	/// <summary>Metres above and below the anchor the rays for the picture start and stop.</summary>
	private const float RayAboveMeters = 200f;
	private const float RayBelowMeters = 100f;

	private const float BorderWidth = 2f;

	private static readonly Color BorderColor = new(0.35f, 0.45f, 0.55f, 0.95f);
	private static readonly Color SideColor = new(0.45f, 0.95f, 0.55f);
	private static readonly Color SelectedColor = new(1f, 1f, 1f);
	private static readonly Color SiteColor = new(1f, 0.85f, 0.35f);
	private static readonly Color OutlineColor = new(0f, 0f, 0f, 0.85f);
	private static readonly Color ViewColor = new(1f, 1f, 1f, 0.85f);
	private static readonly Color ZoomTextColor = new(0.85f, 0.88f, 0.9f);

	/// <summary>A mouse event on the map, in map pixels from its top-left corner (<see cref="InputEventMouse.Position"/>).</summary>
	public event Action<InputEvent> MapEvent;

	public MinimapView View { get; } = new();

	/// <summary>The size this map is drawn at: square, whatever the layout says.</summary>
	public Vector2 MapSize => new(SizePixels, SizePixels);

	/// <summary>How many of each kind of marker the last frame drew: for the engine check, and nothing else.</summary>
	public (int Units, int Vehicles, int Barracks, int Structures, int Sites) Drawn { get; private set; }

	/// <summary>Whether the picture of the ground has been taken.</summary>
	public bool HasTerrain => _terrain != null;

	private IReadOnlyList<Unit> _selected = Array.Empty<Unit>();

	private readonly Vector2[] _view = new Vector2[4];
	private readonly Vector2[] _viewMap = new Vector2[5];
	private readonly Vector2[] _footprint = new Vector2[4];
	private readonly Vector2[] _diamond = new Vector2[4];
	private bool _hasView;

	private float[] _heights;
	private int _scanned = -1;
	private float _anchorY;
	private PhysicsRayQueryParameters3D _ray;
	private ImageTexture _terrain;

	public override void _Ready()
	{
		// Stop: a click on the map is the map's, not a box select of the world behind it.
		MouseFilter = MouseFilterEnum.Stop;
		ClipContents = true;
		CustomMinimumSize = MapSize;
	}

	/// <summary>
	/// Starts taking the picture of the ground, with rays from <see cref="RayAboveMeters"/> over
	/// <paramref name="anchorY"/> — the strategist anchor's height, which the camera's own limits
	/// are measured from — to <see cref="RayBelowMeters"/> under it.
	/// </summary>
	public void Survey(float anchorY)
	{
		_anchorY = anchorY;
		_heights = new float[TerrainCells * TerrainCells];
		_scanned = 0;
		_terrain = null;
	}

	/// <summary>The selection, to draw over the rest. Read every frame; it is the controller's own list.</summary>
	public void Track(IReadOnlyList<Unit> selected) => _selected = selected ?? Array.Empty<Unit>();

	/// <summary>
	/// The ground the camera sees, as the four points <paramref name="corners"/> — top-left,
	/// top-right, bottom-right, bottom-left of the screen — each <c>(x, z)</c>; and the point at
	/// its middle, which the map follows when it is zoomed in and the camera has flown off it.
	/// </summary>
	public void SetView(ReadOnlySpan<Vector2> corners, Vector2 focus, bool follow)
	{
		for (int i = 0; i < _view.Length; i++)
		{
			_view[i] = corners[i];
		}

		_hasView = true;
		if (follow)
		{
			View.Follow(View.ClampToField(focus));
		}
	}

	/// <summary>The world point, <c>(x, z)</c>, under <paramref name="map"/> pixels from the top-left corner, held to the field.</summary>
	public Vector2 WorldAt(Vector2 map) => View.ClampToField(View.ToWorld(map, MapSize));

	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouse)
		{
			MapEvent?.Invoke(@event);
			AcceptEvent();
		}
	}

	public override void _Process(double delta)
	{
		ScanSome();
		QueueRedraw();
	}

	public override void _Draw()
	{
		Vector2 size = MapSize;
		var whole = new Rect2(Vector2.Zero, size);

		DrawRect(whole, MinimapTerrain.Empty);
		if (_terrain != null)
		{
			// The part of the picture the shown square covers: the picture spans the field.
			float cell = TerrainCells / (2f * View.FieldHalf);
			Vector2 from = (View.Center - (Vector2.One * View.HalfSpan) + (Vector2.One * View.FieldHalf)) * cell;
			DrawTextureRectRegion(_terrain, whole, new Rect2(from, Vector2.One * (2f * View.HalfSpan * cell)));
		}

		var drawn = (Units: 0, Vehicles: 0, Barracks: 0, Structures: 0, Sites: 0);
		Team side = CombatManager.Instance?.Local?.Team ?? Team.Strategist;
		UnitManager units = UnitManager.Instance;

		DrawStructures(units, side, ref drawn);
		DrawBarracks(units, side, ref drawn);
		DrawUnits(units, side, ref drawn);
		DrawSelection(side);
		DrawView();

		if (View.ZoomIndex > 0)
		{
			DrawString(ThemeDB.FallbackFont, new Vector2(6f, size.Y - 6f), $"x{View.Zoom:0}",
				HorizontalAlignment.Left, -1f, 12, ZoomTextColor);
		}

		DrawRect(whole, BorderColor, filled: false, width: BorderWidth);
		Drawn = drawn;
	}

	// ---- the picture of the ground -------------------------------------------------------

	/// <summary>
	/// Takes the next <see cref="RaysPerFrame"/> heights, straight down at each cell's middle,
	/// against the world layer only: the ground, buildings, walls and finished structures, not
	/// players, units or vehicles. When the last is in, shades them and makes the texture.
	/// </summary>
	private void ScanSome()
	{
		if (_scanned < 0 || _heights == null || _scanned >= _heights.Length)
		{
			return;
		}

		PhysicsDirectSpaceState3D space = GetViewport()?.FindWorld3D()?.DirectSpaceState;
		if (space == null)
		{
			return;
		}

		_ray ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
		float cellMeters = 2f * View.FieldHalf / TerrainCells;
		int end = Math.Min(_heights.Length, _scanned + RaysPerFrame);

		for (; _scanned < end; _scanned++)
		{
			int row = _scanned / TerrainCells;
			int column = _scanned % TerrainCells;
			float x = -View.FieldHalf + ((column + 0.5f) * cellMeters);
			float z = -View.FieldHalf + ((row + 0.5f) * cellMeters);

			_ray.From = new Vector3(x, _anchorY + RayAboveMeters, z);
			_ray.To = new Vector3(x, _anchorY - RayBelowMeters, z);
			Godot.Collections.Dictionary hit = space.IntersectRay(_ray);
			_heights[_scanned] = hit.Count > 0 ? hit["position"].AsVector3().Y : float.NaN;
		}

		if (_scanned < _heights.Length)
		{
			return;
		}

		float water = LevelRoot.Current is { HasWater: true } level ? level.WaterLevelMeters : float.NegativeInfinity;
		byte[] pixels = MinimapTerrain.Shade(_heights, TerrainCells, cellMeters, water);
		Image image = Image.CreateFromData(TerrainCells, TerrainCells, false, Image.Format.Rgba8, pixels);
		_terrain = ImageTexture.CreateFromImage(image);
		_heights = null;
	}

	// ---- markers ---------------------------------------------------------------------------

	private Vector2 Map(Vector3 world) => View.ToMap(new Vector2(world.X, world.Z), MapSize);

	/// <summary>Whether <paramref name="map"/> is on the map, or within <paramref name="slack"/> pixels of it.</summary>
	private static bool OnMap(Vector2 map, float slack) =>
		map.X >= -slack && map.Y >= -slack && map.X <= SizePixels + slack && map.Y <= SizePixels + slack;

	/// <summary>
	/// The side's structures by their footprints, turned as they stand: filled when finished, an
	/// amber outline while a site. Never smaller than a few pixels, so a sandbag wall on the
	/// whole field is still a mark.
	/// </summary>
	private void DrawStructures(UnitManager units, Team side, ref (int Units, int Vehicles, int Barracks, int Structures, int Sites) drawn)
	{
		for (int i = 0; units != null && i < SimConfig.MaxStructures; i++)
		{
			Structure structure = units.StructureAt(i);
			if (structure == null || !IsInstanceValid(structure) || structure.IsDestroyed || structure.Team != side)
			{
				continue;
			}

			Footprint footprint = structure.Footprint;
			Vector2 middle = Map(footprint.Center);
			if (!OnMap(middle, 12f))
			{
				continue;
			}

			for (int c = 0; c < 4; c++)
			{
				_footprint[c] = Map(footprint.Corner(c));
			}

			bool built = structure.IsBuilt;
			const float MinPixels = 4f;
			if (_footprint[0].DistanceTo(_footprint[2]) < MinPixels * 1.414f)
			{
				var dot = new Rect2(middle - (Vector2.One * (MinPixels * 0.5f)), Vector2.One * MinPixels);
				DrawRect(dot, built ? SideColor : SiteColor, filled: built);
			}
			else if (built)
			{
				DrawColoredPolygon(_footprint, SideColor);
				DrawClosed(_footprint, OutlineColor, 1f);
			}
			else
			{
				DrawClosed(_footprint, SiteColor, 1.5f);
			}

			if (built)
			{
				drawn.Structures++;
			}
			else
			{
				drawn.Sites++;
			}
		}
	}

	/// <summary>The side's barracks: a large square, outlined, the biggest mark on the map.</summary>
	private void DrawBarracks(UnitManager units, Team side, ref (int Units, int Vehicles, int Barracks, int Structures, int Sites) drawn)
	{
		for (int i = 0; units != null && i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks == null || !IsInstanceValid(barracks) || barracks.Team != side)
			{
				continue;
			}

			Vector2 at = Map(barracks.GlobalPosition);
			if (!OnMap(at, 8f))
			{
				continue;
			}

			// Its 8 m, or 9 pixels when that is smaller.
			float across = Mathf.Max(9f, 8f * View.PixelsPerMeter(MapSize));
			var square = new Rect2(at - (Vector2.One * (across * 0.5f)), Vector2.One * across);
			DrawRect(square, SideColor);
			DrawRect(square, OutlineColor, filled: false, width: 1.5f);
			drawn.Barracks++;
		}
	}

	/// <summary>
	/// The side's units on the field: a dot for one on foot, a square for a vehicle, a diamond
	/// for an aircraft. A rider is not on the field; its vehicle is the mark.
	/// </summary>
	private void DrawUnits(UnitManager units, Team side, ref (int Units, int Vehicles, int Barracks, int Structures, int Sites) drawn)
	{
		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (!IsOnSide(unit, side))
			{
				continue;
			}

			Vector2 at = Map(unit.GlobalPosition);
			if (!OnMap(at, 4f))
			{
				continue;
			}

			if (DrawUnit(unit, at, SideColor))
			{
				drawn.Vehicles++;
			}
			else
			{
				drawn.Units++;
			}
		}
	}

	/// <summary>The selected units again, white, over the rest.</summary>
	private void DrawSelection(Team side)
	{
		for (int i = 0; i < _selected.Count; i++)
		{
			Unit unit = _selected[i];
			if (!IsOnSide(unit, side))
			{
				continue;
			}

			Vector2 at = Map(unit.GlobalPosition);
			if (OnMap(at, 4f))
			{
				DrawUnit(unit, at, SelectedColor);
			}
		}
	}

	/// <summary>One unit's mark. True when it was drawn as a vehicle.</summary>
	private bool DrawUnit(Unit unit, Vector2 at, Color color)
	{
		UnitDefinition definition = unit.Definition;
		if (unit.IsAircraft)
		{
			const float R = 4.5f;
			_diamond[0] = at + new Vector2(0f, -R);
			_diamond[1] = at + new Vector2(R, 0f);
			_diamond[2] = at + new Vector2(0f, R);
			_diamond[3] = at + new Vector2(-R, 0f);
			DrawColoredPolygon(_diamond, color);
			DrawClosed(_diamond, OutlineColor, 1f);
			return true;
		}

		if (unit.HasSeats || definition is { IsVehicle: true })
		{
			var square = new Rect2(at - new Vector2(3f, 3f), new Vector2(6f, 6f));
			DrawRect(square, color);
			DrawRect(square, OutlineColor, filled: false, width: 1f);
			return true;
		}

		DrawCircle(at, 2.6f, OutlineColor);
		DrawCircle(at, 1.9f, color);
		return false;
	}

	/// <summary>The ground the camera sees: its outline, clipped by the map's edge.</summary>
	private void DrawView()
	{
		if (!_hasView)
		{
			return;
		}

		for (int i = 0; i < _view.Length; i++)
		{
			_viewMap[i] = View.ToMap(_view[i], MapSize);
		}

		_viewMap[4] = _viewMap[0];
		DrawPolyline(_viewMap, ViewColor, 1.5f);
	}

	private void DrawClosed(Vector2[] points, Color color, float width)
	{
		for (int i = 0; i < points.Length; i++)
		{
			DrawLine(points[i], points[(i + 1) % points.Length], color, width);
		}
	}

	private static bool IsOnSide(Unit unit, Team side) =>
		unit != null && IsInstanceValid(unit) && unit.IsOnField && unit.Team == side;
}
