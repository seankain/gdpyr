using Godot;
using System;
using System.ComponentModel.DataAnnotations;

public partial class fps_controller : CharacterBody3D
{
	[Export]
	public float MoveSpeed = 5.0f;

    [Export]
    public float CrouchMoveSpeed = 2.0f;

	[Export]
	public float CrouchSpeed = 2.0f;
	public const float JumpVelocity = 4.5f;

	public float MouseSensitivity = 0.1f;

	public bool _mouse_input = false;
	public double _rotation_input;
	public double _tilt_input;

	public Vector3 _mouse_rotation = Vector3.Zero;

	[Export]
	public float TiltLowerLimit = Mathf.DegToRad(-90);
	[Export]
	public float TiltUpperLimit = Mathf.DegToRad(90);

	[Export]
	public Camera3D camera;
	[Export]
	public AnimationPlayer animationPlayer;
	[Export]
	CollisionObject3D selfCollider;
	[Export]
	public ShapeCast3D headShapeCast;
	private Node3D _head; 

	private bool _isCrouching = false;

	private float _speed;

	// Get the gravity from the project settings to be synced with RigidBody nodes.
	public float gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();

	public override void _Ready()
	{
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_head = GetNode<Node3D>("CameraController");
		headShapeCast.AddException(selfCollider);
		_speed = MoveSpeed;
	}

	public override void _PhysicsProcess(double delta)
	{
		Vector3 velocity = Velocity;

		// Add the gravity.
		if (!IsOnFloor()){
			velocity.Y -= gravity * (float)delta;
		}

		UpdateCamera(delta);

		// Handle Jump.
		if (Input.IsActionJustPressed("jump") && IsOnFloor()){
			velocity.Y = JumpVelocity;
		}

		// Get the input direction and handle the movement/deceleration.
		// As good practice, you should replace UI actions with custom gameplay actions.
		Vector2 inputDir = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
		Vector3 direction = (Transform.Basis * new Vector3(inputDir.X, 0, inputDir.Y)).Normalized();
		if (direction != Vector3.Zero)
		{
			velocity.X = direction.X * _speed;
			velocity.Z = direction.Z * _speed;
		}
		else
		{
			velocity.X = Mathf.MoveToward(Velocity.X, 0, _speed);
			velocity.Z = Mathf.MoveToward(Velocity.Z, 0, _speed);
		}

		Velocity = velocity;
		MoveAndSlide();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		_mouse_input = @event is InputEventMouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured;
		if (_mouse_input)
		{
			_rotation_input = -((InputEventMouseMotion)@event).Relative.X * MouseSensitivity;
			_tilt_input = -((InputEventMouseMotion)@event).Relative.Y * MouseSensitivity;
		}
		if(@event.IsActionPressed("crouch")){
			ToggleCrouch(true);
		}
		else if(@event.IsActionReleased("crouch")){
			ToggleCrouch(false);

		}
		
		

		//TODO open menu
		if (@event is InputEventKey eventKey)
		{
			if (eventKey.Pressed && eventKey.Keycode == Key.Escape)
			{
				GetTree().Quit();
			}
		}
	}

	public void UpdateCamera(double delta){
		_mouse_rotation.X += (float)(_tilt_input * delta);
		_mouse_rotation.X = Mathf.Clamp(_mouse_rotation.X,TiltLowerLimit,TiltUpperLimit);
		_mouse_rotation.Y += (float)(_rotation_input * delta);
		_mouse_rotation.Z = 0;
		Basis = Basis.FromEuler(new Vector3(0,_mouse_rotation.Y,0));
		camera.Basis = Basis.FromEuler(new Vector3(_mouse_rotation.X,0,0));
		_rotation_input = 0f;
		_tilt_input = 0f;
		
	}

	private void SetMoveSpeed(PlayerMoveState moveState){
		switch(moveState){
			case PlayerMoveState.CROUCHING:
			    _speed = CrouchMoveSpeed;
			    break;
			case PlayerMoveState.LADDER:
			    //TODO
				_speed = MoveSpeed;
			    break;
			default:
			_speed = MoveSpeed;
			break;
		}
	}

	public void ToggleCrouch(bool crouching){
		if(crouching){
			GD.Print("crouch");
			animationPlayer.Play("crouch",-1,CrouchSpeed);
			SetMoveSpeed(PlayerMoveState.CROUCHING);
		}
		else{
			if(headShapeCast.IsColliding()){return;}
			GD.Print("uncrouch");
			animationPlayer.Play("crouch",-1,-CrouchSpeed,true);
			SetMoveSpeed(PlayerMoveState.DEFAULT);
			
		}
	}
}

public enum PlayerMoveState
{
DEFAULT,
CROUCHING,
LADDER
}
