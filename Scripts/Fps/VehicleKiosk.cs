using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

// TODO(GV6, docs/GROUND_VEHICLES.md §12): check on a display: the cabinet beside the locker, its
// reach ring, the two pads' markings outside the bunker. Never looked at.
/// <summary>
/// The vehicle kiosk (docs/GROUND_VEHICLES.md §6.6, GV-D13): the locker's twin, beside
/// it in the ground force's bunker. Tap the use key within a metre of its footprint and
/// its panel opens; a vehicle asked for there is put on its kind's pad outside — one
/// <see cref="Marker3D"/> child per kind, <c>Pad4x4</c> and <c>PadHelicopter</c>, each
/// vehicle facing its marker's −Z.
///
/// A marker and nothing else, as <see cref="WeaponLocker"/> is: the timers, the cap,
/// the pads' checks and every request are <see cref="CrewVehicleManager"/>'s, which finds
/// it by its group, measures its reach from its footprint
/// (<see cref="LockerSupply.FootprintDistance"/>, the locker's 1.2 × 0.5 m) and puts what
/// it hands out on the wire in the vehicle message's header (§7.1). No collider, for the
/// locker's reason: furniture to walk up to, not cover.
/// </summary>
public partial class VehicleKiosk : Node3D
{
	/// <summary>Nodes in this group are vehicle kiosks.</summary>
	public const string Group = "vehicle_kiosk";

	private static readonly Vector3 CabinetSize =
		new(LockerSupply.CabinetWidthMeters, 2f, LockerSupply.CabinetDepthMeters);

	/// <summary>Half the floor ring's width [m], the locker's.</summary>
	private const float RingHalfWidth = 0.05f;

	private const int RingCornerSegments = 8;

	/// <summary>How far the painted line round a pad is from the vehicle's collider [m].</summary>
	private const float PadMarginMeters = 0.5f;

	/// <summary>The pad marker for a kind, by <see cref="CrewVehicleKind"/>.</summary>
	public static string PadName(byte kind) =>
		kind == (byte)CrewVehicleKind.Helicopter ? "PadHelicopter" : "Pad4x4";

	/// <summary>Which way the cabinet faces, as a turn about +Y: the locker's convention.</summary>
	public float Yaw
	{
		get
		{
			Vector3 front = GlobalBasis.Z;
			return Mathf.Atan2(front.X, front.Z);
		}
	}

	/// <summary>False for a kiosk a map has scaled or tilted: its footprint would not be the cabinet's.</summary>
	public bool IsUpright
	{
		get
		{
			Basis basis = GlobalBasis;
			return basis.Scale.IsEqualApprox(Vector3.One) && basis.Y.IsEqualApprox(Vector3.Up);
		}
	}

	/// <summary>Flat distance from <paramref name="at"/> to the cabinet's footprint.</summary>
	public float FootprintDistance(Vector3 at) => LockerSupply.FootprintDistance(at, GlobalPosition, Yaw);

	/// <summary>A kind's pad: where its marker is and which way it faces. False when the map has none for it.</summary>
	public bool TryPad(byte kind, out Transform3D pad)
	{
		if (GetNodeOrNull<Node3D>(PadName(kind)) is { } marker)
		{
			pad = marker.GlobalTransform;
			return true;
		}

		pad = Transform3D.Identity;
		return false;
	}

	public override void _Ready()
	{
		AddToGroup(Group);

		var cabinet = new MeshInstance3D
		{
			Name = "Cabinet",
			Position = new Vector3(0f, CabinetSize.Y * 0.5f, 0f),
			Mesh = new BoxMesh { Size = CabinetSize },
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.3f, 0.18f) },
		};
		AddChild(cabinet);

		// A screen on its front, so it reads as something to ask rather than a second locker.
		AddChild(new MeshInstance3D
		{
			Name = "Screen",
			Position = new Vector3(0f, CabinetSize.Y * 0.62f, (CabinetSize.Z * 0.5f) + 0.02f),
			Mesh = new BoxMesh { Size = new Vector3(0.8f, 0.6f, 0.04f) },
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.2f, 0.6f, 0.9f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		AddChild(new Label3D
		{
			Name = "Label",
			Text = "VEHICLE KIOSK\n4x4 · HELICOPTER",
			Position = new Vector3(0f, CabinetSize.Y + 0.35f, 0f),
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			PixelSize = 0.004f,
			OutlineSize = 8,
		});

		if (!Bootstrap.IsDedicatedServer)
		{
			AddChild(Strip(LockerSupply.ReachOutline(SimConfig.LockerReachMeters - RingHalfWidth, RingCornerSegments),
				LockerSupply.ReachOutline(SimConfig.LockerReachMeters + RingHalfWidth, RingCornerSegments),
				new Vector3(0f, 0.03f, 0f), new Color(0.9f, 0.7f, 0.25f), "ReachRing"));
			CallDeferred(MethodName.MarkPads);
		}
	}

	/// <summary>
	/// A painted rectangle round each pad, half a metre outside the vehicle's collider, in
	/// the pad marker's frame: where a person standing in the way blocks it (§6.6).
	/// Deferred, so the pads' own transforms are in.
	/// </summary>
	private void MarkPads()
	{
		for (byte kind = 0; kind < CrewVehicleCatalog.Count; kind++)
		{
			if (GetNodeOrNull<Node3D>(PadName(kind)) is not { } marker || CrewVehicleCatalog.Definition(kind) is not { } definition)
			{
				continue;
			}

			Vector3 half = definition.ColliderHalfExtents;
			Vector3 centre = definition.ColliderCentre;
			float x = half.X + PadMarginMeters;
			float z = half.Z + PadMarginMeters;
			Vector3[] inner = Rectangle(centre.X, centre.Z, x - RingHalfWidth * 2f, z - RingHalfWidth * 2f);
			Vector3[] outer = Rectangle(centre.X, centre.Z, x + RingHalfWidth * 2f, z + RingHalfWidth * 2f);
			marker.AddChild(Strip(inner, outer, new Vector3(0f, 0.03f, 0f), new Color(0.9f, 0.7f, 0.25f), "PadMarking"));
		}
	}

	private static Vector3[] Rectangle(float cx, float cz, float hx, float hz) => new[]
	{
		new Vector3(cx - hx, 0f, cz - hz), new Vector3(cx + hx, 0f, cz - hz),
		new Vector3(cx + hx, 0f, cz + hz), new Vector3(cx - hx, 0f, cz + hz),
	};

	/// <summary>A flat closed strip between two outlines with the same number of points, as the locker draws its ring.</summary>
	private static MeshInstance3D Strip(Vector3[] inner, Vector3[] outer, Vector3 lift, Color colour, string name)
	{
		var vertices = new Vector3[(inner.Length + 1) * 2];
		var normals = new Vector3[vertices.Length];
		for (int i = 0; i <= inner.Length; i++)
		{
			int at = i % inner.Length;
			vertices[2 * i] = outer[at];
			vertices[(2 * i) + 1] = inner[at];
			normals[2 * i] = Vector3.Up;
			normals[(2 * i) + 1] = Vector3.Up;
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.TriangleStrip, arrays);

		return new MeshInstance3D
		{
			Name = name,
			Position = lift,
			Mesh = mesh,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = colour,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			},
		};
	}
}
