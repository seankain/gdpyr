using Godot;
using System;
using System.Runtime.CompilerServices;

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
		WeaponMesh = GetNode<MeshInstance3D>("MeshInstance3D");
		EquipWeapon(MeleeWeapon);
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{

	}

    public override void _PhysicsProcess(double delta)
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
