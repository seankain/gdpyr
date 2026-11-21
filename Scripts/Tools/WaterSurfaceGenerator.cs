using System;
using Godot;

namespace Gdpyr.Tools;

/// <summary>
/// A river surface laid along a <see cref="Path3D"/>: a ribbon <see cref="Width"/> metres
/// wide, flat across, following the curve. Ported from IvanVuytsik/simple-river
/// (<c>water_surface_generator.gd</c>, MIT) and meant for <c>Levels/materials/River.gdshader</c>.
///
/// The ribbon is built in the path's local space and drawn with the path's transform, so
/// moving the <see cref="Path3D"/> moves the water: its Y is the water level. UV.x runs 0→1
/// along the curve and UV.y 0→1 across it; each vertex's tangent is the curve's direction
/// there, which the shader's flow reads.
///
/// Differences from the GDScript original:
/// <list type="bullet">
/// <item>The <see cref="MeshInstance3D"/> is an internal child with no owner, so it is
/// rebuilt on load instead of being saved into the scene (the original saved one more
/// copy each time the scene was opened and saved).</item>
/// <item>In the editor the mesh follows edits to the curve's points and to the path's
/// transform; the original only noticed a different <see cref="Curve3D"/> being assigned.</item>
/// <item>The curve's direction comes from <see cref="Curve3D.SampleBakedWithRotation"/>.
/// The original asked for <c>sample_baked_tangent</c>, which <see cref="Curve3D"/> does
/// not have, so it always fell back to a finite difference.</item>
/// <item>With no <see cref="Path"/> set, a parent <see cref="Path3D"/> is used.</item>
/// </list>
/// </summary>
[Tool]
[GlobalClass]
public partial class WaterSurfaceGenerator : Node
{
	private static readonly StringName DoubleSidedParam = "double_sided";

	[Export]
	public ShaderMaterial RiverMaterial
	{
		get => _riverMaterial;
		set
		{
			_riverMaterial = value;
			QueueRebuild();
		}
	}

	/// <summary>The path to follow. Unset: the parent, when it is a <see cref="Path3D"/>.</summary>
	[Export]
	public Path3D Path
	{
		get => _path;
		set
		{
			_path = value;
			QueueRebuild();
		}
	}

	/// <summary>Metres across. Make it wider than the wet bed so the edges are buried in the banks.</summary>
	[Export(PropertyHint.Range, "0.1,200,0.1,or_greater")]
	public float Width
	{
		get => _width;
		set
		{
			_width = value;
			QueueRebuild();
		}
	}

	[Export(PropertyHint.Range, "1,1000,1,or_greater")]
	public int Steps
	{
		get => _steps;
		set
		{
			_steps = Math.Max(1, value);
			QueueRebuild();
		}
	}

	/// <summary>Each of <see cref="Steps"/> is cut into <c>1 + (int)(Smoothness * 4)</c> segments.</summary>
	[Export(PropertyHint.Range, "0,1,0.25")]
	public float Smoothness
	{
		get => _smoothness;
		set
		{
			_smoothness = value;
			QueueRebuild();
		}
	}

	/// <summary>
	/// Off: the direction at a point is a finite difference over 1% of the curve either side
	/// of it, as the original always did in practice.
	/// </summary>
	[Export]
	public bool PathRotationAccurate
	{
		get => _pathRotationAccurate;
		set
		{
			_pathRotationAccurate = value;
			QueueRebuild();
		}
	}

	[Export]
	public bool DoubleSided
	{
		get => _doubleSided;
		set
		{
			_doubleSided = value;
			QueueRebuild();
		}
	}

	/// <summary>Extra vertex columns across the ribbon (for vertex waves).</summary>
	[Export(PropertyHint.Range, "0,32,1")]
	public int WidthSubdivisions
	{
		get => _widthSubdivisions;
		set
		{
			_widthSubdivisions = Math.Max(0, value);
			QueueRebuild();
		}
	}

	/// <summary>The generated ribbon. Null until the node is ready.</summary>
	public MeshInstance3D Surface => _meshInstance;

	private ShaderMaterial _riverMaterial;
	private Path3D _path;
	private float _width = 5.0f;
	private int _steps = 30;
	private float _smoothness = 0.5f;
	private bool _pathRotationAccurate = true;
	private bool _doubleSided = true;
	private int _widthSubdivisions;

	private MeshInstance3D _meshInstance;
	private bool _rebuildQueued;

	// What the mesh was last built from, so the editor can notice an edit in place.
	private Curve3D _builtCurve;
	private int _builtCurveSignature;
	private Transform3D _builtPathTransform;

	public override void _Ready()
	{
		_meshInstance = new MeshInstance3D { Name = "Surface", TopLevel = true };
		// Internal and unowned: never written into the scene file.
		AddChild(_meshInstance, false, InternalMode.Front);

		// At run time the path does not move, so one build is enough.
		SetProcess(Engine.IsEditorHint());
		QueueRebuild();
	}

	public override void _Process(double delta)
	{
		Path3D path = ResolvePath();
		if (path == null)
		{
			return;
		}

		if (path.Curve != _builtCurve
			|| CurveSignature(path.Curve) != _builtCurveSignature
			|| path.GlobalTransform != _builtPathTransform)
		{
			UpdateMesh();
		}
	}

	private void QueueRebuild()
	{
		if (_rebuildQueued || !IsInsideTree())
		{
			return;
		}

		_rebuildQueued = true;
		Callable.From(UpdateMesh).CallDeferred();
	}

	private Path3D ResolvePath()
	{
		if (IsInstanceValid(_path))
		{
			return _path;
		}

		return GetParent() as Path3D;
	}

	/// <summary>Rebuild the ribbon now from the path's current curve and transform.</summary>
	public void UpdateMesh()
	{
		_rebuildQueued = false;

		if (!IsInstanceValid(_meshInstance) || !_meshInstance.IsInsideTree())
		{
			return;
		}

		Path3D path = ResolvePath();
		if (path == null || !path.IsInsideTree())
		{
			return;
		}

		Curve3D curve = path.Curve;
		_builtCurve = curve;
		_builtCurveSignature = CurveSignature(curve);
		_builtPathTransform = path.GlobalTransform;
		_meshInstance.GlobalTransform = _builtPathTransform;

		ArrayMesh mesh = curve != null && curve.PointCount >= 2
			? GenerateRiverMesh(curve, _width, _steps, _smoothness, _pathRotationAccurate, _widthSubdivisions)
			: null;

		_meshInstance.Mesh = mesh;
		if (mesh == null)
		{
			return;
		}

		if (_riverMaterial != null)
		{
			_riverMaterial.SetShaderParameter(DoubleSidedParam, _doubleSided);
			_meshInstance.MaterialOverride = _riverMaterial;
		}
		else
		{
			_meshInstance.MaterialOverride = new StandardMaterial3D
			{
				CullMode = _doubleSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				AlbedoColor = new Color(0.5f, 0.7f, 1.0f, 0.5f),
			};
		}
	}

	/// <summary>
	/// The ribbon for a curve, in the curve's space. One row of
	/// <c>widthSubdivisions + 2</c> vertices per step along it, normals up, tangents along it.
	/// </summary>
	public static ArrayMesh GenerateRiverMesh(Curve3D curve, float width, int steps, float smoothness,
		bool pathRotationAccurate, int widthSubdivisions = 0)
	{
		if (curve == null || curve.PointCount < 2)
		{
			return null;
		}

		if (curve.BakeInterval <= 0f)
		{
			curve.BakeInterval = 0.1f;
		}

		float curveLength = curve.GetBakedLength();
		if (curveLength <= 0f)
		{
			return null;
		}

		int subdivisions = (int)(smoothness * 4.0f);
		int actualSteps = Math.Max(1, steps) * (subdivisions + 1);
		int rows = actualSteps + 1;
		int columns = widthSubdivisions + 2;
		bool closedLoop = curve.Closed;

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		Vector3 firstForward = Vector3.Zero;
		for (int i = 0; i < rows; i++)
		{
			float t = (float)i / actualSteps;
			float offset = t * curveLength;
			Vector3 position = curve.SampleBaked(offset, true);

			Vector3 forward = SampleForward(curve, t, offset, curveLength, pathRotationAccurate);
			if (i == 0)
			{
				firstForward = forward;
			}
			else if (closedLoop && i == rows - 1)
			{
				forward = firstForward;
			}

			Vector3 right = forward.Cross(Vector3.Up).Normalized();
			var tangent = new Plane(forward, 1.0f);

			for (int j = 0; j < columns; j++)
			{
				float across = (float)j / (columns - 1);
				st.SetNormal(Vector3.Up);
				st.SetTangent(tangent);
				st.SetUV(new Vector2(t, across));
				st.AddVertex(position + right * width * (across - 0.5f));
			}
		}

		for (int i = 0; i < rows - 1; i++)
		{
			int row = i * columns;
			for (int j = 0; j < columns - 1; j++)
			{
				int v0 = row + j;
				int v1 = v0 + 1;
				int v2 = v0 + columns;
				int v3 = v2 + 1;

				st.AddIndex(v0);
				st.AddIndex(v2);
				st.AddIndex(v1);

				st.AddIndex(v1);
				st.AddIndex(v2);
				st.AddIndex(v3);
			}
		}

		return st.Commit();
	}

	/// <summary>The curve's unit direction of travel at <paramref name="offset"/>.</summary>
	private static Vector3 SampleForward(Curve3D curve, float t, float offset, float curveLength, bool pathRotationAccurate)
	{
		if (pathRotationAccurate)
		{
			// The posture's basis looks down -Z along the curve, as PathFollow3D does.
			Vector3 forward = -curve.SampleBakedWithRotation(offset, true).Basis.Z;
			if (forward.LengthSquared() > 1e-8f)
			{
				return forward.Normalized();
			}
		}

		float tForward = Math.Min(t + 0.01f, 1.0f);
		float tBackward = Math.Max(t - 0.01f, 0.0f);
		Vector3 ahead = curve.SampleBaked(tForward * curveLength);
		Vector3 behind = curve.SampleBaked(tBackward * curveLength);
		return (ahead - behind).Normalized();
	}

	/// <summary>Changes when any point, handle or tilt of the curve does.</summary>
	private static int CurveSignature(Curve3D curve)
	{
		if (curve == null)
		{
			return 0;
		}

		var hash = new HashCode();
		hash.Add(curve.PointCount);
		hash.Add(curve.Closed);
		hash.Add(curve.BakeInterval);
		for (int i = 0; i < curve.PointCount; i++)
		{
			hash.Add(curve.GetPointPosition(i));
			hash.Add(curve.GetPointIn(i));
			hash.Add(curve.GetPointOut(i));
			hash.Add(curve.GetPointTilt(i));
		}

		return hash.ToHashCode();
	}
}
