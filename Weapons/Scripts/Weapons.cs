using Godot;
using System;

[GlobalClass]
public partial class Weapons : Resource
{
    public enum WeaponSlot
    {
        Melee,
        Sidearm,
        Large
    }

    [Export]
    public StringName Name;
    [Export]
    public WeaponSlot Slot;
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
    [ExportCategory("WeaponSway")]
    [Export]
    public Vector2 SwayMin = new Vector2(-20,20);
    [ExportCategory("WeaponSway")]
    [Export]
    public Vector2 SwayMax = new Vector2(-20,20);
    [ExportCategory("WeaponSway")]
    [Export]
    public float SwayAmountPosition = 0.1f;
    [ExportCategory("WeaponSway")]
    [Export]
    public float SwayAmountRotation = 30.0f;
    

}
