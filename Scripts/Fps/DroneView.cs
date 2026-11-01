using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// What a drone looks like (docs/CONTROL_POINTS.md §4.3): a small quadcopter for every
/// drone this process knows about, and — for the player flying one — the view from its
/// nose. Presentation only. On the server the drones are the authority's own; on a client
/// they are the last state message's, drawn on along their heading for the time since it
/// arrived, so a 30 Hz sample of a 24 m/s drone does not step half a metre at a time.
/// </summary>
public partial class DroneView : Node3D
{
	/// <summary>How far ahead of a message a drone is drawn before it is held where it was [s]: a lost message or two.</summary>
	private const float MaxExtrapolationSeconds = 0.15f;

	private readonly MeshInstance3D[] _bodies = new MeshInstance3D[DroneRules.MaxInFlight];
	private Camera3D _camera;
	private bool _flying;
	private uint _stateTick;
	private ulong _stateAtMsec;

	/// <summary>Whether this process's player is looking out of a drone now.</summary>
	public bool Flying => _flying;

	public override void _Ready()
	{
		var material = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.2f, 0.22f, 0.25f),
			Roughness = 0.6f,
		};

		for (int i = 0; i < _bodies.Length; i++)
		{
			var body = new MeshInstance3D
			{
				Name = $"Drone{i}",
				Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.12f, 0.5f) },
				MaterialOverride = material,
				Visible = false,
			};

			// Four rotor discs at the corners: a quadcopter at a glance, which a box is not.
			for (int r = 0; r < 4; r++)
			{
				body.AddChild(new MeshInstance3D
				{
					Position = new Vector3((r & 1) == 0 ? -0.3f : 0.3f, 0.08f, (r & 2) == 0 ? -0.3f : 0.3f),
					Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.16f, Height = 0.02f, RadialSegments = 12 },
					MaterialOverride = material,
				});
			}

			AddChild(body);
			_bodies[i] = body;
		}

		_camera = new Camera3D { Name = "DroneCamera", Fov = 90f, Near = 0.05f, Far = 600f };
		AddChild(_camera);
	}

	public override void _Process(double delta)
	{
		CombatManager combat = CombatManager.Instance;
		NetworkManager net = NetworkManager.Instance;
		if (combat == null || net == null)
		{
			return;
		}

		// A client's drones are a message old: carried on along their headings by the
		// time since it was applied, a little at most.
		float ahead = 0f;
		if (!net.IsServer)
		{
			ulong now = Time.GetTicksMsec();
			if (combat.DroneStateTick != _stateTick)
			{
				_stateTick = combat.DroneStateTick;
				_stateAtMsec = now;
			}

			ahead = Mathf.Clamp((now - _stateAtMsec) / 1000f, 0f, MaxExtrapolationSeconds);
		}

		int local = combat.Local?.PeerId ?? 0;
		int piloted = -1;
		for (int i = 0; i < _bodies.Length; i++)
		{
			Drone drone = combat.DroneAt(i);
			MeshInstance3D body = _bodies[i];
			if (!drone.Active)
			{
				body.Visible = false;
				continue;
			}

			Vector3 position = drone.Position + (drone.Velocity * ahead);
			body.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(drone.Pitch, drone.Yaw, 0f)), position);

			// The pilot does not see the drone they are inside of.
			bool own = local != 0 && drone.PilotPeerId == local;
			body.Visible = !own;
			if (own)
			{
				piloted = i;
				_camera.GlobalTransform = body.GlobalTransform;
			}
		}

		SetFlying(piloted >= 0, combat.Local);
	}

	/// <summary>
	/// Into the drone's camera and out of it. The first-person viewmodel is a layer of its
	/// own over the screen, so it is hidden while the pilot looks out of something else.
	/// </summary>
	private void SetFlying(bool flying, PlayerCombat local)
	{
		if (flying == _flying)
		{
			return;
		}

		_flying = flying;
		fps_controller character = local?.Character;
		if (character?.GetNodeOrNull<CanvasItem>("WeaponViewport") is { } viewmodel)
		{
			viewmodel.Visible = !flying;
		}

		if (flying)
		{
			_camera.Current = true;
			return;
		}

		_camera.Current = false;

		// Back behind the eyes only for somebody still on the ground force: a pilot who
		// changed side mid-flight has the strategist's camera by now, and keeps it.
		if (local?.Team == Team.GroundForce)
		{
			character?.EnterFirstPerson();
		}
	}
}
