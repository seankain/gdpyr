using Gdpyr.Fps;
using Godot;
using System;

namespace Gdpyr.Ui;

public partial class Reticle : CenterContainer
{
	[Export]
	public float DotRadius = 1f;
	[Export]
	public Color DotColor = new Color(255, 255, 255, 255);

	[Export]
	public Line2D[] ReticleLines;

	[Export]
	public CharacterBody3D PlayerController;

	[Export]
	public float ReticleSpeed = 0.25f;

	[Export]
	public float ReticleDistance = 2.0f;

	/// <summary>
	/// How far into a raise the hip reticle goes away. Early, because past this the
	/// player is aiming at the sights and not at the four lines
	/// (<see cref="Gdpyr.Sim.Ads"/>).
	/// </summary>
	private const float HiddenFromProgress = 0.4f;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		QueueRedraw();
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		// Sights up replace the hip reticle: over irons the centred weapon is the
		// sight picture, and an optic draws its own (ScopeOverlay).
		bool aiming = LocalAim.Progress >= HiddenFromProgress;
		Visible = !aiming;
		if (aiming)
		{
			return;
		}

		AdjustReticleLines();
	}

	public override void _Draw()
	{
		DrawCircle(Vector2.Zero, DotRadius, DotColor);
		//base._Draw();
	}

	private void AdjustReticleLines()
	{
		var velocity = PlayerController.GetRealVelocity();
		var origin = Vector3.Zero;
		var pos = Vector2.Zero;
		var speed = origin.DistanceTo(velocity);

		//Top
		ReticleLines[0].Position = _lerp(ReticleLines[0].Position, pos + new Vector2(0, -speed * ReticleDistance), ReticleSpeed);
		//Right
		ReticleLines[1].Position = _lerp(ReticleLines[1].Position, pos + new Vector2( speed * ReticleDistance,0), ReticleSpeed);
		//Bottom
		ReticleLines[2].Position = _lerp(ReticleLines[2].Position, pos + new Vector2(0, speed * ReticleDistance), ReticleSpeed);
		//Left
		ReticleLines[3].Position = _lerp(ReticleLines[3].Position,pos + new Vector2(-speed*ReticleDistance,0),ReticleSpeed);

	}
//TODO make extension method
	private static float _lerp(float first, float second, float amount)
	{
		return first * (1 - amount) + second * amount;
	}
	private static Vector2 _lerp(Vector2 first, Vector2 second, float amount)
	{
		float retX = _lerp(first.X, second.X, amount);
		float retY = _lerp(first.Y, second.Y, amount);
		return new Vector2(retX, retY);

	}
}
