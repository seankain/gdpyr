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
		mouseMovement = mouseMovement.Clamp(WeaponType.SwayMin, WeaponType.SwayMax);

		// lerp position toward sway target
		Vector3 targetPosition = WeaponType.Position;
		targetPosition.X -= mouseMovement.X * WeaponType.SwayAmountPosition;
		targetPosition.Y += mouseMovement.Y * WeaponType.SwayAmountPosition;
		WeaponMesh.Position = WeaponMesh.Position.Lerp(targetPosition, WeaponType.SwayAmountPosition * delta);

		// lerp rotation toward sway target
		Vector3 targetRotation = WeaponType.Rotation;
		targetRotation.Y += mouseMovement.X * WeaponType.SwayAmountRotation;
		targetRotation.X -= mouseMovement.Y * WeaponType.SwayAmountRotation;
		WeaponMesh.RotationDegrees = WeaponMesh.RotationDegrees.Lerp(targetRotation, WeaponType.SwayAmountRotation * delta);

		mouseMovement = Vector2.Zero;
	}
}
