using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

// TODO(GV2, docs/GROUND_VEHICLES.md §12): check on a display, offline and on a listen host: the
// chase camera behind the 4x4 and the helicopter, the spring arm against a wall, first person at
// the gun and in the bed, the viewmodel hidden and back. Built headless and driven by
// scripts/crew-vehicle-checks.sh, never looked at.
/// <summary>
/// What the local player sees from a crew vehicle's seat (docs/GROUND_VEHICLES.md §8.4).
/// Presentation only, on the render frame, and never on a dedicated server.
///
/// <list type="bullet">
/// <item><b>At the controls</b>, of either kind: a chase camera on a
/// <see cref="SpringArm3D"/> behind and above the hull, turned by the player's look, so
/// it does not pass through walls. The 4x4's look follows the truck
/// (<see cref="TryDrivenHullYaw"/>); the helicopter turns to the look.</item>
/// <item><b>At the gun or in the bed or the door</b>: first person, through the body's
/// own camera, which the pin has put at the seat's eye. The viewmodel is kept for a
/// passenger, whose weapon is their own, and hidden at the gun, whose is the vehicle's.</item>
/// </list>
/// </summary>
public partial class CrewSeatView : Node3D
{
	/// <summary>How far above the vehicle's origin the chase camera's arm pivots [m].</summary>
	private const float PivotHeightMeters = 2.2f;

	/// <summary>How far back the arm reaches, before a wall shortens it [m].</summary>
	private const float ArmLengthMeters = 7f;

	/// <summary>The helicopter's is longer: it is bigger, and the ground matters more from it.</summary>
	private const float HelicopterArmLengthMeters = 10f;

	/// <summary>How much below the look the camera sits, so the hull is in the lower half of the view [rad].</summary>
	private const float ArmDownRadians = 0.22f;

	/// <summary>The steepest a chase camera looks down or up [rad].</summary>
	private const float ArmPitchMinRadians = -1.2f;

	private const float ArmPitchMaxRadians = 0.6f;

	private Node3D _pivot;
	private SpringArm3D _arm;
	private Camera3D _camera;
	private CrewVehicle _armVehicle;
	private bool _chasing;
	private bool _seated;

	public override void _Ready()
	{
		_pivot = new Node3D { Name = "ChasePivot" };
		AddChild(_pivot);

		_arm = new SpringArm3D
		{
			Name = "ChaseArm",
			SpringLength = ArmLengthMeters,
			CollisionMask = CollisionLayers.World,
			Margin = 0.2f,
		};
		_pivot.AddChild(_arm);

		_camera = new Camera3D { Name = "ChaseCamera", Fov = 75f, Near = 0.05f, Far = 800f };
		_arm.AddChild(_camera);
	}

	public override void _Process(double delta)
	{
		PlayerCombat local = CombatManager.Instance?.Local;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		fps_controller character = local?.Character;

		if (character == null || crew == null || !crew.TrySeat(local.PeerId, out CrewVehicle vehicle, out int seat))
		{
			Leave(character);
			return;
		}

		SeatRole role = vehicle.Seats[seat].Role;
		_seated = true;
		SetViewmodel(character, role == SeatRole.Passenger);

		if (role != SeatRole.Driver)
		{
			if (_chasing)
			{
				_chasing = false;
				_camera.Current = false;
				character.EnterFirstPerson();
			}

			return;
		}

		if (_armVehicle != vehicle)
		{
			if (IsInstanceValid(_armVehicle))
			{
				_arm.RemoveExcludedObject(_armVehicle.GetRid());
			}

			_arm.AddExcludedObject(vehicle.GetRid());
			_arm.SpringLength = vehicle.Kind == CrewVehicleKind.Helicopter ? HelicopterArmLengthMeters : ArmLengthMeters;
			_armVehicle = vehicle;
		}

		// The hull as it is drawn, a driver's correction still being drawn away included (§8.2).
		_pivot.GlobalPosition = vehicle.DrawnPose.Position + (Vector3.Up * PivotHeightMeters);
		float pitch = Mathf.Clamp(character.Pitch - ArmDownRadians, ArmPitchMinRadians, ArmPitchMaxRadians);
		_pivot.GlobalBasis = Basis.FromEuler(new Vector3(pitch, character.Yaw, 0f));

		if (!_chasing)
		{
			_chasing = true;
			_camera.Current = true;
		}
	}

	/// <summary>Back behind the eyes, with the viewmodel, once the local player is out of every seat.</summary>
	private void Leave(fps_controller character)
	{
		if (!_seated)
		{
			return;
		}

		_seated = false;
		if (_chasing)
		{
			_chasing = false;
			_camera.Current = false;
			character?.EnterFirstPerson();
		}

		SetViewmodel(character, true);
	}

	private static void SetViewmodel(fps_controller character, bool visible)
	{
		if (character?.GetNodeOrNull<CanvasItem>("WeaponViewport") is { } viewmodel && viewmodel.Visible != visible)
		{
			viewmodel.Visible = visible;
		}
	}

	/// <summary>
	/// The heading of the 4x4 the local player is driving, as this process draws it: what
	/// the input sampler turns the look by each tick so the camera follows the truck — the
	/// device end of the boundary, as <see cref="LocalAim.LookScale"/> is (§8.4). The 4x4's
	/// step never reads the look. False in any other seat, in the helicopter, whose heading
	/// follows the look rather than the other way round, and on foot.
	/// </summary>
	public static bool TryDrivenHullYaw(out float yaw)
	{
		yaw = 0f;
		PlayerCombat local = CombatManager.Instance?.Local;
		if (local == null || CrewVehicleManager.Instance is not { } crew
			|| !crew.IsDriving(local.PeerId, out CrewVehicle vehicle) || vehicle.Kind != CrewVehicleKind.FourByFour)
		{
			return false;
		}

		yaw = vehicle.Drive.Yaw;
		return true;
	}
}
