using Godot;
using System;

public partial class Debug : PanelContainer
{

private Label property;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		Visible = false;
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("debug")){
			Visible = !Visible;
		}
    }

	private void AddDebugProperty(string title, string value){
		var property = new Label();
		this.AddChild(property);
		property.Name = title;
		property.
	}
}
