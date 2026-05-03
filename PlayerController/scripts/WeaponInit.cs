using Godot;
using System;

public partial class WeaponInit : Node3D
{
	[Export]
	public Weapons WeaponType;

	public MeshInstance3D WeaponMesh;
	public MeshInstance3D WeaponShadow;

	private Vector2 mouseMovement = Vector2.Zero;
	private Vector3 rotationDegrees = Vector3.Zero;
	private Vector3 position = Vector3.Zero;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
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
    }


	private void LoadWeapon()
	{
		WeaponMesh.Mesh = WeaponType.Mesh;
		WeaponMesh.Position = WeaponType.Position;
		WeaponMesh.RotationDegrees = WeaponType.Rotation;
	}

	private void SwayWeapon(float delta)
	{
		mouseMovement = mouseMovement.Clamp(WeaponType.SwayMin,WeaponType.SwayMax);
		// lerp position
		position = WeaponType.Position;
		position.X = Mathf.Lerp(position.X,WeaponType.Position.X - (mouseMovement.X * 
		WeaponType.SwayAmountPosition)*delta,WeaponType.SwayAmountPosition);
		position.Y = Mathf.Lerp(position.Y,WeaponType.Position.Y + (mouseMovement.Y *
		WeaponType.SwayAmountPosition)*delta, WeaponType.SwayAmountPosition);
		WeaponType.Position = position;
		//lerp rotation
		rotationDegrees.Y = Mathf.Lerp(rotationDegrees.Y, WeaponType.Rotation.Y + (mouseMovement.X *
		WeaponType.SwayAmountRotation)*delta,WeaponType.SwayAmountRotation);
		rotationDegrees.X = Mathf.Lerp(rotationDegrees.X, WeaponType.Rotation.X - (mouseMovement.Y *
		WeaponType.SwayAmountRotation)*delta,WeaponType.SwayAmountRotation);
		WeaponType.Rotation = rotationDegrees;
	}
}
