using Godot;
using System;

public partial class WeaponCamera : Camera3D
{
	[Export]
	public Camera3D PlayerBaseCamera;
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		this.Transform = PlayerBaseCamera.Transform;
	}
}
