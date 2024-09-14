using Godot;
using System;

[GlobalClass]
public partial class Weapons : Resource
{
    [Export]
    public StringName Name;
    [Export]
    [ExportCategory("Weapon Orientation")]
    public Vector3 Position;
    [Export]
    public Vector3 Rotation;
    [Export]
    [ExportCategory("Visual Settings")]
    public Mesh Mesh;
    [Export]
    public bool Shadow;
    [Export]
    public float DamageAmount;
}
