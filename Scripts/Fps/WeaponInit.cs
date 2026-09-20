using Gdpyr.Core;
using Godot;
using System;

namespace Gdpyr.Fps;

public partial class WeaponInit : Node3D
{
	[Export]
	public Weapons MeleeWeapon;
	[Export]
	public Weapons SidearmWeapon;
	[Export]
	public Weapons LargeWeapon;

	public Weapons WeaponType;

	public MeshInstance3D WeaponMesh;
	public MeshInstance3D WeaponShadow;

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
		EquipWeapon(MeleeWeapon);
	}

	// Sway is cosmetic and stays on the render frame: it must never feed the
	// simulation, and it should be as smooth as the display allows rather than
	// stepping at the 60 Hz simulation tick (docs/NETCODE.md §3.1).
	public override void _Process(double delta)
	{
		SwayWeapon((float)delta);
	}


    public override void _Input(InputEvent @event)
    {
        if(@event is InputEventMouseMotion m)
		{
			this.mouseMovement = m.Relative;
		}
		if (@event.IsActionPressed("weapon_melee"))
		{
			EquipWeapon(MeleeWeapon);
		}
		else if (@event.IsActionPressed("weapon_sidearm"))
		{
			EquipWeapon(SidearmWeapon);
		}
		else if (@event.IsActionPressed("weapon_large"))
		{
			EquipWeapon(LargeWeapon);
		}
    }


	private void EquipWeapon(Weapons weapon)
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
