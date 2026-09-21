using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// Draws the projectiles the simulation is flying: a pooled mesh per round in the
/// air, plus a short-lived marker where one stopped.
///
/// Purely cosmetic, and deliberately dumb — it reads <see cref="ProjectileSim"/>
/// and never writes to it. Nothing here may feed the simulation
/// (docs/NETCODE.md §3.1), and a client that renders no tracer at all would still
/// agree with the server about every hit.
/// </summary>
public partial class ProjectileView : Node3D
{
	/// <summary>Meshes kept for tracers. Matches the pool the simulation flies.</summary>
	private const int TracerPool = SimConfig.MaxLiveProjectiles;

	private const int ImpactPool = 24;

	/// <summary>Seconds an impact marker stays up.</summary>
	private const float ImpactSeconds = 0.35f;

	private readonly MeshInstance3D[] _tracers = new MeshInstance3D[TracerPool];
	private readonly MeshInstance3D[] _impacts = new MeshInstance3D[ImpactPool];
	private readonly float[] _impactAge = new float[ImpactPool];

	private int _nextImpact;

	public override void _Ready()
	{
		for (int i = 0; i < TracerPool; i++)
		{
			_tracers[i] = NewMesh(new BoxMesh { Size = Vector3.One });
		}

		for (int i = 0; i < ImpactPool; i++)
		{
			_impacts[i] = NewMesh(new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 6, Rings = 3 });
			_impactAge[i] = float.MaxValue;
		}
	}

	public override void _Process(double delta)
	{
		for (int i = 0; i < ImpactPool; i++)
		{
			if (_impactAge[i] >= ImpactSeconds)
			{
				continue;
			}

			_impactAge[i] += (float)delta;
			if (_impactAge[i] >= ImpactSeconds)
			{
				_impacts[i].Visible = false;
			}
		}
	}

	/// <summary>Places a mesh on every live projectile and hides the rest.</summary>
	public void Render(ProjectileSim projectiles)
	{
		if (projectiles == null)
		{
			return;
		}

		int drawn = 0;
		int slots = projectiles.SlotCount;

		for (int i = 0; i < slots && drawn < TracerPool; i++)
		{
			if (!projectiles.TryGetAt(i, out Projectile projectile))
			{
				continue;
			}

			ProjectileDefinition definition = WeaponCatalog.Definition(projectile.DefinitionId)?.Projectile;
			MeshInstance3D mesh = _tracers[drawn++];

			float length = definition?.TracerLengthMeters ?? 1f;
			float radius = definition?.TracerRadiusMeters ?? 0.02f;

			// Stretched along the direction of travel: a round moving at 400 m/s
			// crosses 6.7 m per tick, and a sphere on that path reads as a strobe.
			mesh.Scale = new Vector3(radius * 2f, radius * 2f, length);
			mesh.GlobalPosition = projectile.Position;
			if (projectile.Velocity.LengthSquared() > 1e-4f)
			{
				mesh.LookAt(projectile.Position + projectile.Velocity, Vector3.Up);
			}

			SetColor(mesh, definition?.TracerColor ?? Colors.White);
			mesh.Visible = true;
		}

		for (int i = drawn; i < TracerPool; i++)
		{
			_tracers[i].Visible = false;
		}
	}

	/// <summary>Marks where a round stopped.</summary>
	public void Impact(Vector3 point, HitFlags flags)
	{
		MeshInstance3D mesh = _impacts[_nextImpact];
		_impactAge[_nextImpact] = 0f;
		_nextImpact = (_nextImpact + 1) % ImpactPool;

		mesh.GlobalPosition = point;
		mesh.Scale = Vector3.One * (flags.HasFlag(HitFlags.Exploded) ? 6f : 1f);
		SetColor(mesh, flags.HasFlag(HitFlags.Killed) ? Colors.Red
			: flags.HasFlag(HitFlags.Exploded) ? Colors.Orange
			: Colors.White);
		mesh.Visible = true;
	}

	private MeshInstance3D NewMesh(Mesh mesh)
	{
		var instance = new MeshInstance3D
		{
			Mesh = mesh,
			Visible = false,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				AlbedoColor = Colors.White,
			},
		};

		AddChild(instance);
		return instance;
	}

	private static void SetColor(MeshInstance3D mesh, Color color)
	{
		if (mesh.MaterialOverride is StandardMaterial3D material)
		{
			material.AlbedoColor = color;
		}
	}
}
