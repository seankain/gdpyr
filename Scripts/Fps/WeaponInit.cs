using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;
using System;
using System.Collections.Generic;

namespace Gdpyr.Fps;

/// <summary>
/// The first-person viewmodel: which weapon is in shot, where it is held, and its
/// sway.
///
/// Since M2 the weapon it shows is read from the simulation rather than from the
/// keyboard, and since the sights went in, where it is held is read from there
/// too. The equipped slot is simulated state — the server has an opinion
/// about it, a replayed tick reproduces it — so a viewmodel that switched itself
/// on a keypress would be showing a weapon the player is not holding whenever the
/// two disagreed (docs/NETCODE.md §3.1). Sway stays on the render frame and stays
/// cosmetic.
///
/// A weapon with a <see cref="WeaponDefinition.Viewmodel"/> is drawn by that scene instead of
/// the mesh — arms and weapon, animated by its fire and reload from the same simulated state
/// (<see cref="AnimatedViewmodel"/>, docs/VIEWMODELS.md).
/// </summary>
public partial class WeaponInit : Node3D
{
	public WeaponDefinition WeaponType;

	public MeshInstance3D WeaponMesh;
	public MeshInstance3D WeaponShadow;

	/// <summary>Each weapon's animated viewmodel, made the first time it is drawn and kept.</summary>
	private readonly Dictionary<WeaponDefinition, AnimatedViewmodel> _animated = new();

	/// <summary>The animated viewmodel in shot, or null when the weapon is drawn as a mesh.</summary>
	private AnimatedViewmodel _shownAnimated;

	/// <summary>The weapon's animated viewmodel while one is in shot (<see cref="WeaponDefinition.Viewmodel"/>); null otherwise.</summary>
	public AnimatedViewmodel Animated => _shownAnimated;

	/// <summary>
	/// How far into a scoped weapon's raise the viewmodel leaves the shot. Matches
	/// where <see cref="Ui.ScopeOverlay"/> starts to cover the screen, so the gun
	/// goes as the eyepiece arrives rather than before or after it.
	/// </summary>
	private const float ScopeHidesViewmodelAt = 0.55f;

	private Vector2 mouseMovement = Vector2.Zero;
	private Vector3 rotationDegrees = Vector3.Zero;
	private Vector3 position = Vector3.Zero;

	private Vector3 weaponScale = Vector3.One;

	[Export]
	private NoiseTexture2D swayNoise;
	private float swaySpeed = 1.2f;

	private float idleSwayAdjustment;
	private float idleSwayRotationStrength;
	private float randomSwayAmount;
	private float swayTime = 0f;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		if (Bootstrap.IsDedicatedServer)
		{
			// Belt and braces: since M1 the character frees its whole weapon rig on
			// anything but the local player, so a dedicated server never reaches this.
			// The viewmodel is cosmetic and must never feed the simulation
			// (docs/NETCODE.md §3.1), and a dedicated-server export strips its textures
			// to placeholders, so `swayNoise` is null here and SwayWeapon would throw.
			SetProcess(false);
			SetProcessInput(false);
			return;
		}

		WeaponMesh = GetNode<MeshInstance3D>("MeshInstance3D");
	}

	// Sway is cosmetic and stays on the render frame: it must never feed the
	// simulation, and it should be as smooth as the display allows rather than
	// stepping at the 60 Hz simulation tick (docs/NETCODE.md §3.1).
	public override void _Process(double delta)
	{
		FollowSimulation();
		AimWeapon();
		SwayWeapon((float)delta);
		AnimateWeapon(delta);
	}


    public override void _Input(InputEvent @event)
    {
        if(@event is InputEventMouseMotion m)
		{
			this.mouseMovement = m.Relative;
		}
    }

	/// <summary>
	/// Shows whatever the local player's simulated weapon state says is equipped.
	/// Polled rather than pushed: the viewmodel is allowed to lag the simulation by
	/// a frame, and nothing here may call back into it.
	/// </summary>
	private void FollowSimulation()
	{
		byte? equipped = CombatManager.Instance?.Local?.EquippedDefinitionId;
		if (equipped.HasValue)
		{
			EquipWeapon(WeaponCatalog.Definition(equipped.Value));
		}
	}

	/// <summary>
	/// Brings the sights up: the viewmodel is drawn somewhere between where the
	/// weapon is carried and where it is aimed, at whatever fraction of the raise
	/// the simulation has reached (<see cref="Ads"/>).
	///
	/// Behind an optic the viewmodel is what the player would be looking *through*,
	/// so it is taken out of shot once the scope's surround has the screen —
	/// <see cref="Ui.ScopeOverlay"/> fades in over the same part of the raise. Over
	/// irons there is nothing to hide: the weapon centring *is* the sight picture.
	/// </summary>
	private void AimWeapon()
	{
		if (WeaponType == null)
		{
			return;
		}

		LocalAim.TryRead(out AimStats stats, out float progress);

		Position = WeaponType.Position.Lerp(WeaponType.AimPosition, progress);
		Rotation = WeaponType.Rotation.Lerp(WeaponType.AimRotation, progress);

		bool inShot = !stats.IsScoped || progress < ScopeHidesViewmodelAt;
		if (WeaponMesh != null)
		{
			WeaponMesh.Visible = inShot && _shownAnimated == null;
		}

		if (_shownAnimated != null)
		{
			_shownAnimated.Visible = inShot;
		}
	}

	/// <summary>
	/// Poses an animated viewmodel from the local player's equipped weapon, at the tick being
	/// drawn: the last simulated one and however far the render frame is into the next.
	/// </summary>
	private void AnimateWeapon(double delta)
	{
		if (_shownAnimated == null)
		{
			return;
		}

		PlayerCombat local = CombatManager.Instance?.Local;
		if (local == null)
		{
			_shownAnimated.Draw(WeaponState.Empty, default, 0.0, delta, 0f);
			return;
		}

		double now = (NetworkManager.Instance?.Tick ?? 0u) + Engine.GetPhysicsInterpolationFraction();
		_shownAnimated.Draw(local.Equipped, local.EquippedStats, now, delta, LocalAim.Progress);
	}

	/// <summary>
	/// The end of the barrel as it is drawn, in world space, and the line it points along
	/// (docs/MUZZLE_EFFECTS.md §2.2): the weapon's <see cref="WeaponDefinition.ViewmodelMuzzle"/>
	/// when it has one, else the face of the mesh's bounds the camera looks out of, where a
	/// bore would be on it (<see cref="Muzzles.BarrelEnd"/>). False while there is nothing
	/// drawn to fire from — no weapon yet, or a scope's eyepiece has the screen and the
	/// viewmodel is out of shot.
	/// </summary>
	public bool TryGetMuzzle(out Vector3 muzzle, out Vector3 forward)
	{
		muzzle = default;
		forward = default;
		Camera3D camera = GetParentOrNull<Node3D>()?.GetParentOrNull<Camera3D>() ?? GetViewport()?.GetCamera3D();
		if (WeaponType == null || camera == null)
		{
			return false;
		}

		forward = (-camera.GlobalBasis.Z).Normalized();
		if (_shownAnimated != null)
		{
			return _shownAnimated.Root.IsVisibleInTree() && _shownAnimated.TryGetMuzzle(camera, out muzzle);
		}

		if (WeaponMesh?.Mesh == null || !WeaponMesh.IsVisibleInTree())
		{
			return false;
		}

		Transform3D mesh = WeaponMesh.GlobalTransform;
		Vector3 local = WeaponType.ViewmodelMuzzle;
		if (local == Vector3.Zero)
		{
			Aabb bounds = WeaponMesh.Mesh.GetAabb();
			Basis toMesh = mesh.Basis.Inverse();
			local = Muzzles.BarrelEnd(bounds.Position, bounds.Size, toMesh * forward, toMesh * camera.GlobalBasis.Y);
		}

		muzzle = mesh * local;
		return true;
	}

	private void EquipWeapon(WeaponDefinition weapon)
	{
		if (weapon == null || weapon == WeaponType)
		{
			return;
		}
		WeaponType = weapon;
		LoadWeapon();
	}

	private void LoadWeapon()
	{
		WeaponMesh.Mesh = WeaponType.Mesh;
		ShowAnimated(WeaponType);
		Position = WeaponType.Position;
		Rotation = WeaponType.Rotation;
		// reset the sway state to the new weapon's rest transform
		position = WeaponType.Position;
		rotationDegrees = WeaponType.Rotation;
		weaponScale = WeaponType.Scale;
		idleSwayAdjustment = WeaponType.IdleSwayAdjustment;
		idleSwayRotationStrength = WeaponType.IdleSwayRotationStrength;
		randomSwayAmount = WeaponType.RandomSwayAmount;
	}

	/// <summary>Puts the weapon's animated viewmodel in shot, making it the first time; hides any other.</summary>
	private void ShowAnimated(WeaponDefinition weapon)
	{
		if (_shownAnimated != null)
		{
			_shownAnimated.Visible = false;
			_shownAnimated = null;
		}

		if (weapon.Viewmodel == null)
		{
			return;
		}

		if (!_animated.TryGetValue(weapon, out AnimatedViewmodel animated))
		{
			animated = AnimatedViewmodel.Instantiate(weapon.Viewmodel, weapon.ViewmodelFovDegrees);
			if (animated == null)
			{
				GD.PushWarning($"[viewmodel] {weapon.Name}'s viewmodel is not a 3D scene; drawing its mesh");
				return;
			}

			AddChild(animated.Root);
			_animated[weapon] = animated;
		}

		animated.Visible = true;
		_shownAnimated = animated;
	}

	private void SwayWeapon(float delta)
	{
		if (WeaponType == null)
		{
			return;
		}
		var swayRandom = GetSwayNoise();
		float swayRandomAdjusted = swayRandom * idleSwayAdjustment;
		swayTime += delta*(swaySpeed+swayRandom);
		float randomSwayX = (float)Mathf.Sin(swayTime * 1.5 + swayRandomAdjusted)/randomSwayAmount;
		float randomSwayY = (float)Mathf.Sin(swayTime - swayRandomAdjusted)/randomSwayAmount;

		mouseMovement = mouseMovement.Clamp(WeaponType.SwayMin,WeaponType.SwayMax);
		// lerp position
		position.X = Mathf.Lerp(position.X,WeaponType.Position.X - (mouseMovement.X *
		WeaponType.SwayAmountPosition + randomSwayX)*delta,WeaponType.SwayAmountPosition);
		position.Y = Mathf.Lerp(position.Y,WeaponType.Position.Y + (mouseMovement.Y *
		WeaponType.SwayAmountPosition + randomSwayY)*delta, WeaponType.SwayAmountPosition);
		//lerp rotation
		rotationDegrees.Y = Mathf.Lerp(rotationDegrees.Y, WeaponType.Rotation.Y + (mouseMovement.X *
		WeaponType.SwayAmountRotation + (randomSwayY * idleSwayRotationStrength))*delta,WeaponType.SwayAmountRotation);
		rotationDegrees.X = Mathf.Lerp(rotationDegrees.X, WeaponType.Rotation.X - (mouseMovement.Y *
		WeaponType.SwayAmountRotation + (randomSwayX * idleSwayRotationStrength))*delta,WeaponType.SwayAmountRotation);
	}

	private float GetSwayNoise()
	{
		// I replaced the part where the tutorial got the player position
		// because I don't have access to that in my setup and it didn't seem
		// like it was really necessary here 
		// // It turns it into a one liner but I might change this later
		return swayNoise.Noise.GetNoise2D(Random.Shared.Next(),Random.Shared.Next());
	}
}
