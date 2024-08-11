using Godot;
using System;

public partial class Debug : PanelContainer
{

	private Label property;
	private VBoxContainer PropertyContainer;
	private string FramesPerSecond;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		Visible = false;
		PropertyContainer = GetNode<VBoxContainer>("MarginContainer/VBoxContainer");
		
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		if(Visible){
		FramesPerSecond = $"{1f/delta}";
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (@event.IsActionPressed("debug"))
		{
			Visible = !Visible;
		}
	}

	private void AddDebugProperty(string title, string value)
	{
		property = new Label();
		PropertyContainer.AddChild(property);
		property.Name = title;
		property.Text = $"{title} {value}";
	}
}
