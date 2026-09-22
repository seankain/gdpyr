using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// A pillbox, a sandbag wall or a sniper tower a builder has put up, or is putting
/// up (docs/NETCODE.md §10.5).
///
/// Server-authoritative like a unit, but not a unit: it cannot be selected, ordered
/// or moved, and it counts for nothing in the strategist's defeat condition — a
/// pillbox holds no ground and earns nothing, so a strategist with only concrete
/// left has nothing left to play with. What it is, is solid. Once it is finished
/// its boxes are a static collider on the world layer on every peer, so rounds,
/// sight lines, bodies and the navigation bake all stop against it without being
/// taught a new layer; while it is going up it is a squashed, see-through site
/// that stops only the rounds low enough to hit what has been built of it.
///
/// A body and a gun post and nothing else. What it does each tick is
/// <see cref="UnitManager"/>'s, which owns the table it lives in; the rules it goes
/// up and comes down by are <see cref="Construction"/>, and its gun runs through
/// the same <see cref="DefenseBattery.StepGun"/> a barracks' does.
///
/// Built in code rather than from a scene, for the reason a unit is: the boxes come
/// from <see cref="StructureDefinition"/>, and a .tscn would be a second place for
/// the collider to disagree with the hit test.
/// </summary>
public partial class Structure : Node3D, IGunPost
{
	/// <summary>The group the navigation bake reads colliders from (<c>UnitManager.BakeNavigation</c>).</summary>
	public const string NavigationSourceGroup = "navmesh";

	/// <summary>
	/// How far a pillbox's round starts outside its wall, so that it leaves from the
	/// slit rather than from inside the concrete it would otherwise hit.
	/// </summary>
	private const float SlitClearanceMeters = 0.3f;

	/// <summary>Its slot in the structure table: what it is called on the wire.</summary>
	public int Slot { get; set; } = -1;

	/// <summary>Its catalog index, <see cref="StructureKinds"/>.</summary>
	public byte Kind { get; set; }

	public Team Team { get; set; } = Team.Strategist;

	/// <summary>True only on the authority. A client's copy is told its state, never works it out.</summary>
	public bool IsSimulated { get; set; }

	/// <summary>Which way it faces, set before it enters the tree. Its gun's aim is separate.</summary>
	public float Facing { get; set; }

	public StructureDefinition Definition { get; private set; }

	public StructureShape Shape { get; private set; }

	/// <summary>Where it stands, fixed when it was placed.</summary>
	public StructurePose Pose { get; private set; }

	public Footprint Footprint => Pose.FootprintOf(Shape);

	public float Health { get; private set; }

	public float MaxHealth => Definition?.MaxHealth ?? 100f;

	public StructureFlags Flags { get; private set; }

	public bool IsBuilt => (Flags & StructureFlags.Built) != 0;

	public bool IsDestroyed => (Flags & StructureFlags.Destroyed) != 0;

	public bool IsObstructed => (Flags & StructureFlags.Obstructed) != 0;

	public bool IsArmed => Definition?.IsArmed ?? false;

	/// <summary>Server: ticks of work in it. A client has only <see cref="Progress"/>.</summary>
	public int WorkTicks;

	/// <summary>Server: builders in reach of it this tick, counted by the units and spent by the manager.</summary>
	public int Workers;

	/// <summary>Server: the tick its rubble is taken off the field on.</summary>
	public uint DespawnTick { get; set; }

	/// <summary>How far along it is, 0 to 1. The server derives it; a client is told it.</summary>
	public float Progress => IsSimulated
		? Construction.Progress(WorkTicks, Definition?.BuildTicks ?? 1)
		: _replicatedProgress;

	/// <summary>The boxes it presents right now: the whole of it once built, the part built so far until then.</summary>
	public StructureShape HitShape => IsBuilt ? Shape : Shape.Raised(Construction.RaisedFraction(Progress));

	public byte HealthPercent => UnitFlags.PackHealth(Health, MaxHealth);

	public byte ProgressByte => (byte)Mathf.RoundToInt(Mathf.Clamp(Progress, 0f, 1f) * byte.MaxValue);

	/// <summary>Its eye, above its base: over the parapet for a tower, at the slit for a pillbox.</summary>
	public Vector3 EyePosition => Pose.Base + (Vector3.Up * (Definition?.EyeHeightMeters ?? 1f));

	/// <summary>
	/// How many points the side that owns it sees from (docs/NETCODE.md §6.2): one
	/// when its eye is above it, and four — one outside each wall — when its eye is
	/// at a slit. A sight line from the middle of a concrete box would start inside
	/// the concrete; one from the roof would miss everybody standing close to the
	/// walls. Zero for a structure with no eyes at all.
	/// </summary>
	public int SensorCount => (Definition?.SensorRadiusMeters ?? 0f) <= 0f ? 0 : EyeIsAbove ? 1 : 4;

	/// <summary>The <paramref name="index"/>-th of <see cref="SensorCount"/> points it sees from.</summary>
	public Vector3 SensorAt(int index)
	{
		if (EyeIsAbove)
		{
			return EyePosition;
		}

		Footprint footprint = Footprint;
		Vector3 across = footprint.AxisX * (footprint.HalfWidth + SlitClearanceMeters);
		Vector3 along = footprint.AxisZ * (footprint.HalfDepth + SlitClearanceMeters);
		return EyePosition + (index switch
		{
			0 => along,
			1 => -along,
			2 => across,
			_ => -across,
		});
	}

	/// <summary>Where it looks at <paramref name="point"/> from: its eye, out through the wall that faces it.</summary>
	public Vector3 EyeToward(Vector3 point) => OutThroughWall(EyePosition, point);

	/// <summary>Where a round is aimed at it from: the middle of what stands of it.</summary>
	public Vector3 AimPoint => Pose.Base + (Vector3.Up * (HitShape.Height * 0.5f));

	/// <summary>What every round it fires names as its owner.</summary>
	public int ShooterId => OwnerId.ForStructure(Slot);

	// ---- the gun, for the two that have one -----------------------------------

	public DefenseTraits Traits { get; private set; }

	public WeaponState Weapon;

	public int TargetPeerId { get; set; }

	public uint TargetSinceTick { get; set; }

	public uint NextScanTick { get; set; }

	public float AimYaw { get; set; }

	public float AimPitch { get; set; }

	public byte WeaponDefinitionId => Definition?.WeaponDefinitionId ?? WeaponCatalog.Turret;

	public WeaponStats Stats => WeaponCatalog.StatsFor(WeaponDefinitionId);

	public float AccuracyConeRadians => Definition?.AccuracyConeRadians ?? 0f;

	ref WeaponState IGunPost.Belt => ref Weapon;

	int IGunPost.ScanStagger => Slot;

	private float _replicatedProgress;
	private Node3D _model;
	private StaticBody3D _body;
	private Node3D _yawPivot;
	private Node3D _pitchPivot;
	private StandardMaterial3D _solid;
	private StandardMaterial3D _scaffold;
	private StandardMaterial3D _rubble;

	public override void _Ready()
	{
		Definition = StructureCatalog.Definition(Kind);
		Shape = StructureCatalog.ShapeOf(Kind);
		Pose = new StructurePose(GlobalPosition, Facing);
		Rotation = new Vector3(0f, Facing, 0f);

		Health = Construction.InitialHealth(MaxHealth);
		Traits = Definition?.ToGunTraits() ?? default;
		Weapon = WeaponState.Ready(WeaponDefinitionId, Stats);
		AimYaw = Facing + Mathf.Pi;
		AimPitch = 0f;

		BuildBody();
		BuildModel();
		Refresh();
	}

	/// <summary>
	/// Where a round aimed at <paramref name="point"/> leaves from: the muzzle, pushed
	/// out through whichever wall faces the target when the muzzle is inside the
	/// structure — a pillbox's slit — and straight from the muzzle when it is above
	/// it, as a tower's marksman is.
	/// </summary>
	public Vector3 MuzzleToward(Vector3 point) =>
		OutThroughWall(Pose.Base + (Vector3.Up * (Definition?.MuzzleHeightMeters ?? 1f)), point);

	private bool EyeIsAbove => (Definition?.EyeHeightMeters ?? 0f) >= Shape.Height;

	/// <summary>
	/// A point inside the structure, moved out along the flat line to
	/// <paramref name="towards"/> until it is just clear of the wall that line
	/// crosses. A point already above the structure is left where it is.
	/// </summary>
	private Vector3 OutThroughWall(Vector3 inside, Vector3 towards)
	{
		if (inside.Y - Pose.Base.Y >= Shape.Height)
		{
			return inside;
		}

		Vector3 flat = towards - inside;
		flat.Y = 0f;
		if (flat.LengthSquared() < 1e-4f)
		{
			return inside;
		}

		flat = flat.Normalized();
		Vector3 local = flat.Rotated(Vector3.Up, -Facing);
		float toEdge = Mathf.Min(
			Mathf.Abs(local.X) > 1e-4f ? Shape.HalfWidth / Mathf.Abs(local.X) : float.MaxValue,
			Mathf.Abs(local.Z) > 1e-4f ? Shape.HalfDepth / Mathf.Abs(local.Z) : float.MaxValue);

		return inside + (flat * (toEdge + SlitClearanceMeters));
	}

	// ---- server ----------------------------------------------------------------

	/// <summary>Takes damage. Returns true when this was the hit that brought it down.</summary>
	public bool ApplyDamage(float amount)
	{
		if (IsDestroyed || amount <= 0f)
		{
			return false;
		}

		Health = Mathf.Max(0f, Health - amount);
		if (Health > 0f)
		{
			return false;
		}

		SetFlags(StructureFlags.Destroyed);
		return true;
	}

	/// <summary>One tick of work; true on the tick the last of it goes in.</summary>
	public bool Work(int builders)
	{
		float health = Health;
		bool done = Construction.Work(ref WorkTicks, ref health, builders, Definition?.BuildTicks ?? 1, MaxHealth);
		Health = health;
		RefreshModel();
		return done;
	}

	/// <summary>One tick of mending.</summary>
	public void Repair(int builders)
	{
		float health = Health;
		Construction.Repair(ref health, builders, Definition?.BuildTicks ?? 1, MaxHealth);
		Health = health;
	}

	/// <summary>Finished: solid, manned and — for the gun — pointed where it faces.</summary>
	public void Complete()
	{
		WorkTicks = Definition?.BuildTicks ?? WorkTicks;
		SetFlags(StructureFlags.Built);
	}

	public void SetObstructed(bool obstructed) =>
		SetFlags(obstructed ? Flags | StructureFlags.Obstructed : Flags & ~StructureFlags.Obstructed);

	/// <summary>Puts a structure up already finished — a scenario's, or a map's — at full health.</summary>
	public void PlaceFinished()
	{
		Health = MaxHealth;
		Complete();
	}

	/// <summary>
	/// Takes it out of the world before it is freed: off the world layer and out of
	/// the group the navigation bake reads, so nothing that runs before the end of
	/// the frame finds a wall that is gone.
	/// </summary>
	public void Retire()
	{
		if (_body != null)
		{
			_body.CollisionLayer = 0;
			_body.RemoveFromGroup(NavigationSourceGroup);
		}
	}

	// ---- client ----------------------------------------------------------------

	/// <summary>Takes the server's word for how hurt, how far along and how finished it is.</summary>
	public void ApplyRemoteState(byte healthPercent, byte progress, StructureFlags flags)
	{
		Health = healthPercent / (float)byte.MaxValue * MaxHealth;
		_replicatedProgress = progress / (float)byte.MaxValue;
		SetFlags(flags);
		RefreshModel();
	}

	/// <summary>
	/// Points the gun along a round it has just fired. On a client this is the whole
	/// of the gun's replication, as it is for a barracks' (docs/NETCODE.md §10.4).
	/// </summary>
	public void PointAlong(Vector3 direction)
	{
		if (direction == Vector3.Zero)
		{
			return;
		}

		Aim.Angles(direction, out float yaw, out float pitch);
		AimYaw = yaw;
		AimPitch = pitch;
		ApplyPose();
	}

	public void ApplyPose()
	{
		if (_yawPivot == null)
		{
			return;
		}

		_yawPivot.GlobalBasis = Basis.FromEuler(new Vector3(0f, AimYaw, 0f));
		_pitchPivot.Rotation = new Vector3(AimPitch, 0f, 0f);
	}

	// ---- presentation and collision ----------------------------------------------

	private void SetFlags(StructureFlags flags)
	{
		if (flags == Flags)
		{
			return;
		}

		Flags = flags;
		Refresh();
	}

	/// <summary>
	/// Solid only while it is finished and standing: a site is walked through, and
	/// rubble is walked over. On every peer, because a client predicts its own
	/// character into the same walls the server moves it against.
	/// </summary>
	private void Refresh()
	{
		if (_body != null)
		{
			_body.CollisionLayer = IsBuilt && !IsDestroyed ? CollisionLayers.World : 0u;
		}

		RefreshModel();
	}

	private void RefreshModel()
	{
		if (_model == null)
		{
			return;
		}

		if (IsDestroyed)
		{
			_model.Scale = new Vector3(1f, 0.12f, 1f);
			SetMaterial(_model, _rubble);
			if (_yawPivot != null)
			{
				_yawPivot.Visible = false;
			}
			return;
		}

		float raised = IsBuilt ? 1f : Construction.RaisedFraction(Progress);
		_model.Scale = new Vector3(1f, raised, 1f);
		SetMaterial(_model, IsBuilt ? _solid : _scaffold);

		if (_yawPivot != null)
		{
			// Nobody mans a gun in a building that is not finished.
			_yawPivot.Visible = IsBuilt;
		}
	}

	private void BuildBody()
	{
		_body = new StaticBody3D
		{
			Name = "Body",
			CollisionLayer = 0,
			CollisionMask = 0,
		};

		// In the group the navigation bake reads, so a finished structure is carved
		// out of the mesh the next time it is baked; while its layer is 0 the bake's
		// collision mask passes over it.
		_body.AddToGroup(NavigationSourceGroup);
		AddChild(_body);

		AddBox(_body, Shape.Body);
		if (Shape.HasTop)
		{
			AddBox(_body, Shape.Top);
		}
	}

	private static void AddBox(StaticBody3D body, in StructureBox box)
	{
		body.AddChild(new CollisionShape3D
		{
			Position = box.Center,
			Shape = new BoxShape3D { Size = box.HalfExtents * 2f },
		});
	}

	/// <summary>
	/// Greybox, one look per kind: a concrete box with a dark slit round it; a low
	/// run of sandbags in two courses; a timber column with a parapet on top and a
	/// marksman behind it. The model is squashed with the site's progress, which is
	/// the same squash <see cref="HitShape"/> applies to the boxes.
	/// </summary>
	private void BuildModel()
	{
		Color color = Definition?.Color ?? new Color(0.55f, 0.55f, 0.52f);
		_solid = new StandardMaterial3D { AlbedoColor = color };
		_scaffold = new StandardMaterial3D
		{
			AlbedoColor = new Color(color.R, color.G, color.B, 0.45f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		};
		_rubble = new StandardMaterial3D { AlbedoColor = color.Darkened(0.55f) };

		_model = new Node3D { Name = "Model" };
		AddChild(_model);

		switch (Kind)
		{
			case StructureKinds.SandbagWall:
				BuildSandbags();
				break;

			case StructureKinds.SniperTower:
				BuildTower();
				break;

			default:
				BuildPillbox();
				break;
		}
	}

	private void BuildPillbox()
	{
		AddMesh(_model, Shape.Body, null);

		// The slit: a dark band round all four walls at the muzzle's height.
		float muzzle = Definition?.MuzzleHeightMeters ?? 1.5f;
		var slit = new StructureBox(new Vector3(0f, muzzle, 0f),
			new Vector3(Shape.Body.HalfExtents.X + 0.02f, 0.12f, Shape.Body.HalfExtents.Z + 0.02f));
		AddMesh(_model, slit, new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.09f) });

		BuildGun(muzzle, barrelLength: Shape.HalfWidth + 0.5f, marksman: false);
	}

	private void BuildSandbags()
	{
		// Two courses, the top one a little narrower: sandbags, not a kerb.
		StructureBox body = Shape.Body;
		float half = body.HalfExtents.Y;
		AddMesh(_model, new StructureBox(new Vector3(0f, half * 0.5f, 0f),
			new Vector3(body.HalfExtents.X, half * 0.5f, body.HalfExtents.Z)), null);
		AddMesh(_model, new StructureBox(new Vector3(0f, half * 1.5f, 0f),
			new Vector3(body.HalfExtents.X - 0.1f, half * 0.5f, body.HalfExtents.Z - 0.08f)), null);
	}

	private void BuildTower()
	{
		AddMesh(_model, Shape.Body, null);

		if (Shape.HasTop)
		{
			// A floor and four parapet walls: the box the hit test uses, drawn hollow
			// so the marksman stands in it rather than on it.
			StructureBox top = Shape.Top;
			Vector3 h = top.HalfExtents;
			float floor = top.Center.Y - h.Y;
			const float Wall = 0.15f;

			AddMesh(_model, new StructureBox(new Vector3(0f, floor + 0.1f, 0f), new Vector3(h.X, 0.1f, h.Z)), null);
			AddMesh(_model, new StructureBox(new Vector3(0f, top.Center.Y, h.Z - Wall), new Vector3(h.X, h.Y, Wall)), null);
			AddMesh(_model, new StructureBox(new Vector3(0f, top.Center.Y, -h.Z + Wall), new Vector3(h.X, h.Y, Wall)), null);
			AddMesh(_model, new StructureBox(new Vector3(h.X - Wall, top.Center.Y, 0f), new Vector3(Wall, h.Y, h.Z)), null);
			AddMesh(_model, new StructureBox(new Vector3(-h.X + Wall, top.Center.Y, 0f), new Vector3(Wall, h.Y, h.Z)), null);
		}

		BuildGun(Definition?.MuzzleHeightMeters ?? 8.6f, barrelLength: 1.1f, marksman: true);
	}

	/// <summary>A traverse and an elevation pivot with a barrel on it, as a barracks gun has.</summary>
	private void BuildGun(float height, float barrelLength, bool marksman)
	{
		if (!IsArmed)
		{
			return;
		}

		var metal = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.21f, 0.24f) };

		_yawPivot = new Node3D { Name = "Traverse", Position = new Vector3(0f, height, 0f) };
		AddChild(_yawPivot);

		_pitchPivot = new Node3D { Name = "Elevation" };
		_yawPivot.AddChild(_pitchPivot);

		if (marksman)
		{
			// Somebody for the ground force to see up there: a head and shoulders.
			_yawPivot.AddChild(new MeshInstance3D
			{
				Name = "Marksman",
				Position = new Vector3(0f, -0.7f, 0.2f),
				Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.25f, RadialSegments = 8, Rings = 2 },
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.25f, 0.2f) },
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			});
		}

		_pitchPivot.AddChild(new MeshInstance3D
		{
			Name = "Barrel",
			Position = new Vector3(0f, 0f, -barrelLength * 0.5f),
			Rotation = new Vector3(-Mathf.Pi * 0.5f, 0f, 0f),
			Mesh = new CylinderMesh
			{
				TopRadius = 0.05f,
				BottomRadius = 0.05f,
				Height = barrelLength,
				RadialSegments = 8,
			},
			MaterialOverride = metal,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		ApplyPose();
	}

	private static void AddMesh(Node3D parent, in StructureBox box, Material material)
	{
		parent.AddChild(new MeshInstance3D
		{
			Position = box.Center,
			Mesh = new BoxMesh { Size = box.HalfExtents * 2f },
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});
	}

	/// <summary>Paints every box that has no material of its own — the slit keeps its black.</summary>
	private void SetMaterial(Node3D parent, Material material)
	{
		foreach (Node child in parent.GetChildren())
		{
			if (child is MeshInstance3D mesh && (mesh.MaterialOverride == null || IsBodyMaterial(mesh.MaterialOverride)))
			{
				mesh.MaterialOverride = material;
			}
		}
	}

	private bool IsBodyMaterial(Material material) =>
		material == _solid || material == _scaffold || material == _rubble;
}
