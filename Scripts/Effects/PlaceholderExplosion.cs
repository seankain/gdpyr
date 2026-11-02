using System;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Effects;

// TODO(effects, docs/EFFECTS.md §5): check on a display, as a client and on a listen host: every
// kind with `explode`, rounds on walls and floors, wrecks on the ground and in the air, and the
// strategist's camera. Built headless and compiled, never looked at.
/// <summary>
/// Every <see cref="ExplosionKind"/>, drawn from primitive meshes until there is art
/// (docs/EFFECTS.md §3): a fireball that grows and cools from white to red, a flash of
/// light, smoke puffs that climb and thin out, debris thrown out and falling, a ring
/// that sweeps out to how far the blast reached, a scorch on the floor, and — for a
/// wreck — a fire that burns on under a column of black smoke.
///
/// How big, how long and what colour each part is comes from the kind's
/// <see cref="ExplosionProfile"/>, and how each part changes with age from the curves
/// in <see cref="Explosions"/>; this class only places meshes. Everything is made once,
/// when the pool first needs it, and reused: playing one allocates nothing.
/// </summary>
public partial class PlaceholderExplosion : Node3D, IExplosionEffect
{
	private const int MaxPuffs = Explosions.MaxSmokePuffs;

	private const int MaxDebris = Explosions.MaxDebrisPieces;

	private const int MaxFlames = 3;

	private const float Gravity = 9.8f;

	/// <summary>How far above the floor the scorch and the ring are drawn, so they are on it rather than in it.</summary>
	private const float FloorLiftMeters = 0.04f;

	private struct Puff
	{
		public MeshInstance3D Mesh;
		public StandardMaterial3D Material;
		public Vector3 Start;
		public Vector3 Drift;
		public float Delay;
		public float Size;
		public Color Color;
	}

	private struct Piece
	{
		public MeshInstance3D Mesh;
		public StandardMaterial3D Material;
		public Vector3 Position;
		public Vector3 Velocity;
		public Vector3 Spin;
		public bool Resting;
	}

	private struct Flame
	{
		public MeshInstance3D Mesh;
		public StandardMaterial3D Material;
		public Vector3 Position;
		public float Size;
		public float Phase;
	}

	private readonly Puff[] _puffs = new Puff[MaxPuffs];
	private readonly Piece[] _debris = new Piece[MaxDebris];
	private readonly Flame[] _flames = new Flame[MaxFlames];

	private MeshInstance3D _fireball;
	private StandardMaterial3D _fireballMaterial;
	private OmniLight3D _light;
	private MeshInstance3D _ring;
	private StandardMaterial3D _ringMaterial;
	private MeshInstance3D _scorch;
	private StandardMaterial3D _scorchMaterial;

	private ExplosionRequest _request;
	private ExplosionProfile _profile;
	private float _age;
	private float _scale;
	private uint _random;
	private bool _built;

	public override void _Ready()
	{
		Build();
		Visible = false;
	}

	public void Play(in ExplosionRequest request)
	{
		Build();

		_request = request;
		_profile = Explosions.Profile(request.Kind);
		_scale = request.Scale > 0f ? request.Scale : 1f;
		_age = 0f;
		_random = request.Seed != 0 ? request.Seed : 0x9E3779B9u;

		GlobalPosition = request.Point;
		Vector3 floor = request.HasFloor ? request.Floor - request.Point : Vector3.Zero;

		_ring.Position = floor + (Vector3.Up * FloorLiftMeters);
		_scorch.Position = floor + (Vector3.Up * FloorLiftMeters);

		SeedPuffs();
		SeedDebris();
		SeedFlames(floor);

		Visible = true;
		Advance(0f);
	}

	public bool Advance(float delta)
	{
		_age += delta;

		DrawFireball();
		DrawLight();
		DrawSmoke();
		DrawDebris(delta);
		DrawRing();
		DrawScorch();
		DrawFlames();

		if (_age >= _profile.Lifetime)
		{
			Stop();
			return false;
		}

		return true;
	}

	public void Stop()
	{
		Visible = false;
	}

	// ---- parts --------------------------------------------------------------------------

	private void DrawFireball()
	{
		float seconds = _profile.FireballSeconds;
		float alpha = Explosions.Fade(_age, seconds, 0.35f);
		_fireball.Visible = alpha > 0f && _profile.FireballRadius > 0f;
		if (!_fireball.Visible)
		{
			return;
		}

		float radius = _profile.FireballRadius * _scale * Explosions.FireballGrowth(_age, seconds);
		_fireball.Scale = Vector3.One * radius;

		// A fireball rises a little off what it burst on as it cools.
		_fireball.Position = Vector3.Up * (radius * 0.3f * Explosions.Progress(_age, seconds));
		Color color = Explosions.FireballColor(Explosions.Progress(_age, seconds));
		color.A = alpha;
		_fireballMaterial.AlbedoColor = color;
	}

	private void DrawLight()
	{
		float energy = _profile.FlashEnergy * Explosions.Flash(_age, _profile.FlashSeconds);

		// A wreck's fire keeps a dim, flickering light on what is round it.
		if (_profile.BurnSeconds > 0f && _request.HasFloor)
		{
			float burn = Explosions.Fade(_age, _profile.BurnSeconds, 0.6f);
			energy = MathF.Max(energy, burn * (1.2f + (0.4f * MathF.Sin(_age * 23f))));
		}

		_light.Visible = energy > 0.01f;
		_light.LightEnergy = energy;
		_light.OmniRange = MathF.Max(_profile.FlashRange * _scale, 0.1f);
	}

	private void DrawSmoke()
	{
		int count = Math.Min(_profile.SmokePuffs, MaxPuffs);
		for (int i = 0; i < MaxPuffs; i++)
		{
			ref Puff puff = ref _puffs[i];
			float age = _age - puff.Delay;
			float seconds = _profile.SmokeSeconds;
			if (i >= count || age < 0f || age >= seconds)
			{
				puff.Mesh.Visible = false;
				continue;
			}

			float growth = 0.45f + (0.75f * Explosions.Reach(age, seconds));
			puff.Mesh.Scale = Vector3.One * (_profile.SmokeRadius * _scale * puff.Size * growth);
			puff.Mesh.Position = puff.Start + (puff.Drift * age) + (Vector3.Up * (_profile.SmokeRiseSpeed * _scale * age));

			// In over a tenth of a second rather than at once, out over the rest of its life.
			Color color = puff.Color;
			color.A = 0.7f * MathF.Min(1f, age / 0.1f) * Explosions.Fade(age, seconds, 0.25f);
			puff.Material.AlbedoColor = color;
			puff.Mesh.Visible = true;
		}
	}

	private void DrawDebris(float delta)
	{
		int count = Math.Min(_profile.DebrisPieces, MaxDebris);
		float seconds = _profile.DebrisSeconds;
		float size = _profile.DebrisSize * _scale;
		float floorY = _request.HasFloor ? _request.Floor.Y - _request.Point.Y + (size * 0.5f) : float.NegativeInfinity;

		for (int i = 0; i < MaxDebris; i++)
		{
			ref Piece piece = ref _debris[i];
			if (i >= count || _age >= seconds)
			{
				piece.Mesh.Visible = false;
				continue;
			}

			if (!piece.Resting)
			{
				piece.Velocity += Vector3.Down * (Gravity * delta);
				piece.Position += piece.Velocity * delta;
				if (piece.Position.Y <= floorY)
				{
					piece.Position.Y = floorY;
					piece.Velocity = Vector3.Zero;
					piece.Resting = true;
				}

				piece.Mesh.Rotation += piece.Spin * delta;
			}

			// Shrinks away over the last quarter of its time rather than vanishing.
			float shrink = Explosions.Fade(_age, seconds, 0.75f);
			piece.Mesh.Scale = Vector3.One * (size * shrink);
			piece.Mesh.Position = piece.Position;
			piece.Mesh.Visible = shrink > 0f;
		}
	}

	private void DrawRing()
	{
		float seconds = _profile.ReachSeconds;
		float reach = _request.ReachMeters;
		float alpha = Explosions.Fade(_age, seconds, 0.5f);
		_ring.Visible = reach > 0f && alpha > 0f;
		if (!_ring.Visible)
		{
			return;
		}

		float radius = MathF.Max(reach * Explosions.Reach(_age, seconds), 0.05f);
		_ring.Scale = new Vector3(radius, radius * 0.5f, radius);
		_ringMaterial.AlbedoColor = new Color(1f, 0.82f, 0.55f, 0.55f * alpha);
	}

	private void DrawScorch()
	{
		float alpha = Explosions.Fade(_age, _profile.ScorchSeconds, 0.7f);
		_scorch.Visible = _request.HasFloor && _profile.ScorchRadius > 0f && alpha > 0f;
		if (!_scorch.Visible)
		{
			return;
		}

		float radius = _profile.ScorchRadius * _scale;
		_scorch.Scale = new Vector3(radius, 1f, radius);
		_scorchMaterial.AlbedoColor = new Color(0.05f, 0.04f, 0.035f, 0.6f * alpha);
	}

	private void DrawFlames()
	{
		// After the fireball has done its work, and only on a floor: a wreck in the air has nothing to burn on.
		float age = _age - (_profile.FireballSeconds * 0.5f);
		float alpha = _request.HasFloor ? Explosions.Fade(age, _profile.BurnSeconds, 0.6f) : 0f;
		for (int i = 0; i < MaxFlames; i++)
		{
			ref Flame flame = ref _flames[i];
			flame.Mesh.Visible = alpha > 0f;
			if (!flame.Mesh.Visible)
			{
				continue;
			}

			float flicker = 0.8f + (0.25f * MathF.Sin((age * 17f) + flame.Phase)) + (0.1f * MathF.Sin((age * 31f) + flame.Phase));
			flame.Mesh.Scale = new Vector3(1f, 1.4f, 1f) * (flame.Size * _scale * flicker);
			flame.Mesh.Position = flame.Position;
			flame.Material.AlbedoColor = new Color(1f, 0.45f + (0.15f * flicker), 0.1f, 0.85f * alpha);
		}
	}

	// ---- seeding a play -----------------------------------------------------------------

	private void SeedPuffs()
	{
		int count = Math.Min(_profile.SmokePuffs, MaxPuffs);
		float spread = _profile.SmokeRadius * _scale * 0.5f;
		for (int i = 0; i < MaxPuffs; i++)
		{
			ref Puff puff = ref _puffs[i];
			Vector3 flat = FlatDirection();
			puff.Start = (flat * (spread * Next())) + (Vector3.Up * (spread * 0.8f * Next()));
			puff.Drift = FlatDirection() * (_scale * Range(0.3f, 0.8f));
			puff.Delay = Explosions.PuffDelay(i, count, _profile.SmokeEmitSeconds);
			puff.Size = Range(0.75f, 1.15f);

			float shade = Range(0.85f, 1.1f);
			Color smoke = _profile.SmokeColor;
			puff.Color = new Color(smoke.R * shade, smoke.G * shade, smoke.B * shade);
			puff.Mesh.Visible = false;
		}
	}

	private void SeedDebris()
	{
		Color color = _request.HasTint ? _request.Tint.Darkened(0.45f) : _profile.DebrisColor;
		for (int i = 0; i < MaxDebris; i++)
		{
			ref Piece piece = ref _debris[i];

			// Out and up from where it went off: nothing is thrown into the floor.
			Vector3 direction = (FlatDirection() + (Vector3.Up * Range(0.35f, 1.2f))).Normalized();
			piece.Velocity = direction * (_profile.DebrisSpeed * _scale * Range(0.45f, 1f));
			piece.Position = Vector3.Zero;
			piece.Spin = new Vector3(Range(-12f, 12f), Range(-12f, 12f), Range(-12f, 12f));
			piece.Resting = false;
			piece.Mesh.Rotation = Vector3.Zero;
			piece.Material.AlbedoColor = color.Lightened(Range(0f, 0.15f));
			piece.Mesh.Visible = false;
		}
	}

	private void SeedFlames(Vector3 floor)
	{
		for (int i = 0; i < MaxFlames; i++)
		{
			ref Flame flame = ref _flames[i];
			flame.Position = floor + (FlatDirection() * (_scale * Range(0f, 0.7f))) + (Vector3.Up * (_scale * Range(0.3f, 0.7f)));
			flame.Size = Range(0.45f, 0.75f);
			flame.Phase = Range(0f, MathF.Tau);
			flame.Mesh.Visible = false;
		}
	}

	/// <summary>A random direction across the ground, of length one.</summary>
	private Vector3 FlatDirection()
	{
		float angle = Next() * MathF.Tau;
		return new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
	}

	private float Range(float from, float to) => from + ((to - from) * Next());

	/// <summary>Xorshift: the next of the play's own sequence, 0 to 1. Cosmetic, and allocation-free.</summary>
	private float Next()
	{
		_random ^= _random << 13;
		_random ^= _random >> 17;
		_random ^= _random << 5;
		return (_random & 0xFFFFFF) / (float)0x1000000;
	}

	// ---- construction -------------------------------------------------------------------

	private void Build()
	{
		if (_built)
		{
			return;
		}

		_built = true;

		var sphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 };
		var lowSphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 8, Rings = 4 };

		(_fireball, _fireballMaterial) = Part("Fireball", sphere, unshaded: true);

		_light = new OmniLight3D
		{
			Name = "Flash",
			Position = Vector3.Up * 0.5f,
			LightColor = new Color(1f, 0.62f, 0.3f),
			ShadowEnabled = false,
			Visible = false,
		};
		AddChild(_light);

		for (int i = 0; i < MaxPuffs; i++)
		{
			(_puffs[i].Mesh, _puffs[i].Material) = Part($"Smoke{i}", lowSphere, unshaded: false);
		}

		var chunk = new BoxMesh { Size = Vector3.One };
		for (int i = 0; i < MaxDebris; i++)
		{
			(_debris[i].Mesh, _debris[i].Material) = Part($"Debris{i}", chunk, unshaded: false, transparent: false);
		}

		for (int i = 0; i < MaxFlames; i++)
		{
			(_flames[i].Mesh, _flames[i].Material) = Part($"Flame{i}", lowSphere, unshaded: true);
		}

		(_ring, _ringMaterial) = Part("Reach", new TorusMesh
		{
			InnerRadius = 0.94f,
			OuterRadius = 1f,
			Rings = 48,
			RingSegments = 4,
		}, unshaded: true);

		(_scorch, _scorchMaterial) = Part("Scorch", new CylinderMesh
		{
			TopRadius = 1f,
			BottomRadius = 1f,
			Height = 0.02f,
			RadialSegments = 24,
			Rings = 1,
		}, unshaded: true);
	}

	private (MeshInstance3D, StandardMaterial3D) Part(string name, Mesh mesh, bool unshaded, bool transparent = true)
	{
		var material = new StandardMaterial3D
		{
			ShadingMode = unshaded ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
			Transparency = transparent ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
			Roughness = 1f,
			AlbedoColor = Colors.White,
		};

		var instance = new MeshInstance3D
		{
			Name = name,
			Mesh = mesh,
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = false,
		};

		AddChild(instance);
		return (instance, material);
	}
}
