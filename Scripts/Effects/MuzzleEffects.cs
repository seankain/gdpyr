using System;
using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;
using Godot.Collections;

namespace Gdpyr.Effects;

// TODO(effects, docs/MUZZLE_EFFECTS.md §5): look at on a display, as a client and on a listen
// host: your own rifle, pistol, DMR and launcher from the hip and with the sights up, a
// teammate and a strategist's rifleman firing beside you, a heavy gunner's burst, the
// mounted heavy gun, a tank's cannon and a mortar, and cases on a floor and on a slope.
/// <summary>
/// Every muzzle effect this peer draws (docs/MUZZLE_EFFECTS.md): the flash, the smoke and
/// the spent case of a shot, drawn only for the local player's own shots and for shots
/// fired within a few metres of the camera — each part to its own reach
/// (<see cref="MuzzleRanges"/>) — so a fight across the map costs nothing here.
///
/// Flashes are a pool of <see cref="MuzzleFlash"/> nodes, two or three frames each. Smoke
/// puffs and cases are particles stepped here and drawn as two <see cref="MultiMesh"/>es —
/// one draw call each however many are up — with fixed capacities, past which the oldest
/// is taken for the newest. Playing one allocates nothing once its pool is warm.
///
/// Made only by a process that draws — <c>CombatManager</c> builds it beside the
/// explosions, never on a dedicated server — so <see cref="Instance"/> is null there and
/// every caller's <c>?.</c> is the whole of "a server draws nothing". Purely cosmetic: it
/// reads nothing from the simulation it was not handed, and writes nothing back.
/// </summary>
public partial class MuzzleEffects : Node3D
{
	/// <summary>How many flashes can be up at once. A flash is three frames: this is a dozen guns firing together.</summary>
	public const int FlashCapacity = 12;

	/// <summary>How many puffs of smoke can be up at once.</summary>
	public const int PuffCapacity = 128;

	/// <summary>How many cases can be in the air or on the floor at once: three magazines' worth.</summary>
	public const int CasingCapacity = 96;

	/// <summary>How far below the ejection port the floor is looked for, for a case to land on [m].</summary>
	public const float FloorProbeMeters = 4f;

	/// <summary>How far above the port the probe starts: a port can be below the lip of a wall it is fired over.</summary>
	private const float FloorProbeAboveMeters = 0.1f;

	/// <summary>The side of the textures made for the flame, the star and the smoke [px].</summary>
	private const int TextureSize = 64;

	private struct Puff
	{
		public Vector3 Start;
		public Vector3 Forward;
		public Vector3 Drift;
		public float ForwardSpeed;
		public float RiseSpeed;
		public float Radius;
		public float Seconds;
		public float Alpha;
		public float Age;
		public Color Color;
	}

	private struct Casing
	{
		public CasingBody Body;
		public bool HasFloor;
		public float FloorY;
		public float Length;
		public float Diameter;
		public float Seconds;
		public Color Color;
	}

	public static MuzzleEffects Instance { get; private set; }

	private readonly Puff[] _puffs = new Puff[PuffCapacity];
	private readonly Casing[] _casings = new Casing[CasingCapacity];
	private readonly Random _random = new();

	private EffectPool<MuzzleFlash> _flashes;
	private MultiMeshInstance3D _smoke;
	private MultiMeshInstance3D _brass;
	private Texture2D _flameTexture;
	private Texture2D _starTexture;
	private int _puffCount;
	private int _casingCount;
	private int _puffEvictions;
	private int _casingEvictions;

	/// <summary>How far from the camera each part is drawn for everyone else's shots; the console's <c>muzzle range</c> sets it.</summary>
	public MuzzleRanges Ranges { get; set; } = MuzzleRanges.Default;

	/// <summary>Shots something was drawn for, for the debug HUD's <c>muzzle</c> row.</summary>
	public int Drawn { get; private set; }

	/// <summary>Shots with something to draw that were too far from the camera for any of it: the saving.</summary>
	public int Skipped { get; private set; }

	public int Flashes => _flashes?.ActiveCount ?? 0;

	public int Puffs => _puffCount;

	public int Casings => _casingCount;

	/// <summary>Flashes, puffs and cases cut short for newer ones: anything but 0 means a capacity is small for the fight.</summary>
	public int Evictions => (_flashes?.Evictions ?? 0) + _puffEvictions + _casingEvictions;

	public override void _EnterTree()
	{
		Instance = this;
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	public override void _Ready()
	{
		_flameTexture = Disc(TextureSize, spikes: 0, falloff: 1.3f, hot: true);
		_starTexture = Disc(TextureSize, spikes: 4, falloff: 1.6f, hot: true);
		Texture2D smokeTexture = Disc(TextureSize, spikes: 0, falloff: 1.2f, hot: false);

		_flashes = new EffectPool<MuzzleFlash>(FlashCapacity, NewFlash);

		_smoke = Particles("Smoke", new QuadMesh { Size = Vector2.One }, PuffCapacity, new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
			BillboardKeepScale = true,
			VertexColorUseAsAlbedo = true,
			AlbedoTexture = smokeTexture,
		});

		_brass = Particles("Casings", new CylinderMesh
		{
			TopRadius = 0.5f,
			BottomRadius = 0.5f,
			Height = 1f,
			RadialSegments = 6,
			Rings = 1,
		}, CasingCapacity, new StandardMaterial3D
		{
			VertexColorUseAsAlbedo = true,
			Metallic = 0.75f,
			Roughness = 0.35f,
		});
	}

	/// <summary>
	/// A shot of <paramref name="kind"/> leaving <paramref name="muzzle"/> along
	/// <paramref name="direction"/>. <paramref name="own"/> — the local player's — draws
	/// every part the kind has; anyone else's only the parts whose reach the muzzle is
	/// inside, from the camera. The flash follows <paramref name="anchor"/> when there is
	/// one; a case leaves with <paramref name="shooterVelocity"/> added to its own. Returns
	/// the parts drawn.
	/// </summary>
	public MuzzleParts Emit(ShotKind kind, Vector3 muzzle, Vector3 direction, Node3D anchor, Vector3 shooterVelocity,
		bool own)
	{
		MuzzleProfile profile = Muzzles.Profile(kind);
		if (profile.IsNone || !IsInsideTree() || _flashes == null)
		{
			return MuzzleParts.None;
		}

		MuzzleParts parts = profile.Parts;
		if (!own)
		{
			parts &= GetViewport()?.GetCamera3D() is { } camera
				? Ranges.Within(camera.GlobalPosition.DistanceSquaredTo(muzzle), profile.Reach)
				: MuzzleParts.None;
		}

		if (parts == MuzzleParts.None)
		{
			Skipped++;
			return parts;
		}

		Drawn++;
		Muzzles.Frame(direction, out Vector3 forward, out _, out _);

		if ((parts & MuzzleParts.Flash) != 0)
		{
			float size = Range(0.8f, 1.2f) * (own ? Muzzles.OwnFlashScale : 1f);
			_flashes.Acquire(out _, out _).Play(profile, muzzle, forward, anchor, size);
		}

		if ((parts & MuzzleParts.Smoke) != 0)
		{
			float alpha = profile.SmokeAlpha * (own ? Muzzles.OwnSmokeAlpha : 1f);
			for (int i = 0; i < profile.SmokePuffs; i++)
			{
				AddPuff(profile, muzzle, forward, alpha, i);
			}
		}

		if ((parts & MuzzleParts.Casing) != 0)
		{
			AddCasing(profile, muzzle, forward, shooterVelocity);
		}

		return parts;
	}

	/// <summary>Takes everything off the screen at once, keeping the pools for next time.</summary>
	public void Clear()
	{
		if (_flashes != null)
		{
			for (int i = 0; i < _flashes.Created; i++)
			{
				_flashes.At(i).Stop();
			}

			_flashes.ReleaseAll();
		}

		_puffCount = 0;
		_casingCount = 0;
		Redraw();
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		if (_flashes != null && _flashes.ActiveCount > 0)
		{
			for (int i = 0; i < _flashes.Created; i++)
			{
				if (_flashes.IsActive(i) && !_flashes.At(i).Advance(dt))
				{
					_flashes.Release(i);
				}
			}
		}

		if (_puffCount == 0 && _casingCount == 0)
		{
			return;
		}

		StepPuffs(dt);
		StepCasings(dt);
		Redraw();
	}

	// ---- smoke ------------------------------------------------------------------------

	private void AddPuff(in MuzzleProfile profile, Vector3 muzzle, Vector3 forward, float alpha, int index)
	{
		int slot = _puffCount < PuffCapacity ? _puffCount++ : Oldest(_puffs, ref _puffEvictions);
		Muzzles.Frame(forward, out _, out Vector3 right, out Vector3 up);

		// A second or third puff of the same shot a little further out and a little slower.
		float spread = profile.SmokeRadius * 0.6f;
		_puffs[slot] = new Puff
		{
			Start = muzzle + (forward * (profile.SmokeRadius * (0.5f + index))),
			Forward = forward,
			Drift = (right * Range(-spread, spread)) + (up * Range(-spread, spread)),
			ForwardSpeed = profile.SmokeForwardSpeed * Range(0.7f, 1.1f) / (1f + (0.5f * index)),
			RiseSpeed = profile.SmokeRiseSpeed * Range(0.8f, 1.2f),
			Radius = profile.SmokeRadius * Range(0.85f, 1.15f),
			Seconds = profile.SmokeSeconds * Range(0.85f, 1.15f),
			Alpha = alpha,
			Age = 0f,
			Color = profile.SmokeColor * Range(0.92f, 1.05f),
		};
	}

	private void StepPuffs(float dt)
	{
		for (int i = _puffCount - 1; i >= 0; i--)
		{
			_puffs[i].Age += dt;
			if (_puffs[i].Age >= _puffs[i].Seconds)
			{
				_puffs[i] = _puffs[--_puffCount];
			}
		}
	}

	// ---- cases ------------------------------------------------------------------------

	private void AddCasing(in MuzzleProfile profile, Vector3 muzzle, Vector3 forward, Vector3 shooterVelocity)
	{
		Vector3 port = Muzzles.EjectionPort(muzzle, forward, profile);
		bool hasFloor = TryFindFloor(port, out float floorY);

		int slot = _casingCount < CasingCapacity ? _casingCount++ : Oldest(_casings, ref _casingEvictions);
		_casings[slot] = new Casing
		{
			Body = new CasingBody
			{
				Position = port,
				Velocity = Muzzles.EjectVelocity(forward, profile, shooterVelocity, Next(), Next(), Next()),

				// Thrown out sideways and tumbling end over end.
				Rotation = new Vector3(Mathf.Pi * 0.5f, Range(0f, Mathf.Tau), 0f),
				Spin = new Vector3(Range(-25f, 25f), Range(-8f, 8f), Range(-25f, 25f)),
			},
			HasFloor = hasFloor,
			FloorY = floorY,
			Length = profile.CasingLength,
			Diameter = profile.CasingDiameter,
			Seconds = profile.CasingSeconds * Range(0.9f, 1.1f),
			Color = profile.CasingColor * Range(0.9f, 1.08f),
		};
	}

	private void StepCasings(float dt)
	{
		for (int i = _casingCount - 1; i >= 0; i--)
		{
			ref Casing casing = ref _casings[i];
			Muzzles.StepCasing(ref casing.Body, dt, casing.HasFloor, casing.FloorY, casing.Diameter);
			if (casing.Body.Age >= casing.Seconds)
			{
				_casings[i] = _casings[--_casingCount];
			}
		}
	}

	/// <summary>The floor under <paramref name="port"/>, if the world has one within <see cref="FloorProbeMeters"/>.</summary>
	private bool TryFindFloor(Vector3 port, out float floorY)
	{
		floorY = float.NegativeInfinity;
		PhysicsDirectSpaceState3D space = GetWorld3D()?.DirectSpaceState;
		if (space == null)
		{
			return false;
		}

		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(
			port + (Vector3.Up * FloorProbeAboveMeters), port + (Vector3.Down * FloorProbeMeters), CollisionLayers.World);
		Dictionary hit = space.IntersectRay(query);
		if (hit.Count == 0)
		{
			return false;
		}

		floorY = hit["position"].AsVector3().Y;
		return true;
	}

	// ---- drawing ----------------------------------------------------------------------

	/// <summary>Writes every live puff and case into its multimesh, and draws that many.</summary>
	private void Redraw()
	{
		if (_smoke?.Multimesh is { } smoke)
		{
			var bounds = new Bounds();
			for (int i = 0; i < _puffCount; i++)
			{
				ref Puff puff = ref _puffs[i];
				float diameter = 2f * puff.Radius * Muzzles.SmokeScale(puff.Age, puff.Seconds);
				Vector3 at = puff.Start + (puff.Drift * Muzzles.Progress(puff.Age, puff.Seconds))
					+ Muzzles.SmokeOffset(puff.Forward, puff.ForwardSpeed, puff.RiseSpeed, puff.Age);

				Color color = puff.Color;
				color.A = puff.Alpha * Muzzles.SmokeOpacity(puff.Age, puff.Seconds);
				smoke.SetInstanceTransform(i, new Transform3D(Basis.FromScale(Vector3.One * diameter), at));
				smoke.SetInstanceColor(i, color);
				bounds.Add(at, diameter);
			}

			smoke.VisibleInstanceCount = _puffCount;
			_smoke.CustomAabb = bounds.Box;
		}

		if (_brass?.Multimesh is { } brass)
		{
			var bounds = new Bounds();
			for (int i = 0; i < _casingCount; i++)
			{
				ref Casing casing = ref _casings[i];
				float scale = Muzzles.CasingScale(casing.Body.Age, casing.Seconds);
				var size = new Vector3(casing.Diameter, casing.Length, casing.Diameter) * Mathf.Max(scale, 0.001f);
				Basis basis = Basis.FromEuler(casing.Body.Rotation) * Basis.FromScale(size);
				brass.SetInstanceTransform(i, new Transform3D(basis, casing.Body.Position));
				brass.SetInstanceColor(i, casing.Color);
				bounds.Add(casing.Body.Position, casing.Length);
			}

			brass.VisibleInstanceCount = _casingCount;
			_brass.CustomAabb = bounds.Box;
		}
	}

	/// <summary>The box every live particle fits in, for culling: the multimesh's own would take in the dead ones too.</summary>
	private struct Bounds
	{
		private Vector3 _min;
		private Vector3 _max;
		private bool _any;

		public void Add(Vector3 at, float size)
		{
			Vector3 half = Vector3.One * (size * 0.5f);
			_min = _any ? _min.Min(at - half) : at - half;
			_max = _any ? _max.Max(at + half) : at + half;
			_any = true;
		}

		public readonly Aabb Box => _any ? new Aabb(_min, _max - _min) : new Aabb(Vector3.Zero, Vector3.One * 0.001f);
	}

	// ---- construction -----------------------------------------------------------------

	private MuzzleFlash NewFlash()
	{
		var flash = new MuzzleFlash { Name = $"Flash{_flashes?.Created ?? 0}" };
		AddChild(flash);
		flash.Build(_flameTexture, _starTexture);
		return flash;
	}

	private MultiMeshInstance3D Particles(string name, Mesh mesh, int capacity, Material material)
	{
		var multimesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			UseColors = true,
			Mesh = mesh,
			InstanceCount = capacity,
			VisibleInstanceCount = 0,
		};

		var instance = new MultiMeshInstance3D
		{
			Name = name,
			Multimesh = multimesh,
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};

		AddChild(instance);
		return instance;
	}

	/// <summary>
	/// A disc on transparent, fading out to its edge as (1 − r)^<paramref name="falloff"/>,
	/// with <paramref name="spikes"/> bright rays through it — the star a flash is seen end
	/// on. White for smoke, which is tinted per puff; <paramref name="hot"/> runs it from
	/// white at the middle through yellow to orange at the edge, a flame's colours. Made
	/// once, here, rather than imported: there is no file to lose.
	/// </summary>
	private static ImageTexture Disc(int size, int spikes, float falloff, bool hot)
	{
		var core = new Color(1f, 0.98f, 0.88f);
		var edge = new Color(1f, 0.5f, 0.12f);
		Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		float half = (size - 1) * 0.5f;
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float dx = (x - half) / half;
				float dy = (y - half) / half;
				float r = Mathf.Sqrt((dx * dx) + (dy * dy));
				float a = r >= 1f ? 0f : Mathf.Pow(1f - r, falloff);
				if (spikes > 0 && r < 1f)
				{
					float angle = Mathf.Atan2(dy, dx);
					float ray = Mathf.Pow(Mathf.Abs(Mathf.Cos(angle * spikes * 0.5f)), 12f);
					a = Mathf.Clamp(a + (ray * (1f - r) * 0.8f), 0f, 1f);
				}

				Color color = hot ? core.Lerp(edge, Mathf.Clamp(r * 1.4f, 0f, 1f)) : Colors.White;
				color.A = a;
				image.SetPixel(x, y, color);
			}
		}

		return ImageTexture.CreateFromImage(image);
	}

	/// <summary>The slot of the oldest live particle, to be taken for a new one.</summary>
	private int Oldest(Puff[] puffs, ref int evictions)
	{
		int oldest = 0;
		for (int i = 1; i < _puffCount; i++)
		{
			if (puffs[i].Age > puffs[oldest].Age)
			{
				oldest = i;
			}
		}

		evictions++;
		return oldest;
	}

	private int Oldest(Casing[] casings, ref int evictions)
	{
		int oldest = 0;
		for (int i = 1; i < _casingCount; i++)
		{
			if (casings[i].Body.Age > casings[oldest].Body.Age)
			{
				oldest = i;
			}
		}

		evictions++;
		return oldest;
	}

	private float Next() => _random.NextSingle();

	private float Range(float from, float to) => from + ((to - from) * _random.NextSingle());
}
