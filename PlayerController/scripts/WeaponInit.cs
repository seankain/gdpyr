using Godot;
using System;

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
	}

	private void SwayWeapon(float delta)
	{
		if (WeaponType == null)
		{
			return;
		}
		mouseMovement = mouseMovement.Clamp(WeaponType.SwayMin,WeaponType.SwayMax);
		// lerp position
		position.X = Mathf.Lerp(position.X,WeaponType.Position.X - (mouseMovement.X *
		WeaponType.SwayAmountPosition)*delta,WeaponType.SwayAmountPosition);
		position.Y = Mathf.Lerp(position.Y,WeaponType.Position.Y + (mouseMovement.Y *
		WeaponType.SwayAmountPosition)*delta, WeaponType.SwayAmountPosition);
		//lerp rotation
		rotationDegrees.Y = Mathf.Lerp(rotationDegrees.Y, WeaponType.Rotation.Y + (mouseMovement.X *
		WeaponType.SwayAmountRotation)*delta,WeaponType.SwayAmountRotation);
		rotationDegrees.X = Mathf.Lerp(rotationDegrees.X, WeaponType.Rotation.X - (mouseMovement.Y *
		WeaponType.SwayAmountRotation)*delta,WeaponType.SwayAmountRotation);
	}
}
