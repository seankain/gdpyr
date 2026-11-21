using System;
using Godot;

[Tool]
[GlobalClass]
public partial class WaterSurfaceGenerator : Node
{
    private static readonly StringName SampleBakedTangentMethod = "sample_baked_tangent";
    private static readonly StringName DoubleSidedParam = "double_sided";

    [Export]
    public ShaderMaterial RiverMaterial { get; set; }

    [Export]
    public Path3D Path
    {
        get => _path;
        set
        {
            _path = value;
            if (Engine.IsEditorHint())
                UpdateMesh();
        }
    }

    [Export]
    public double Width
    {
        get => _width;
        set
        {
            _width = value;
            if (Engine.IsEditorHint())
                UpdateMesh();
        }
    }

    [Export]
    public int Steps
    {
        get => _steps;
        set
        {
            _steps = value;
            if (Engine.IsEditorHint())
                UpdateMesh();
        }
    }

    [Export]
    public double Smoothness
    {
        get => _smoothness;
        set
        {
            _smoothness = value;
            if (Engine.IsEditorHint())
                UpdateMesh();
        }
    }

    [Export]
    public bool PathRotationAccurate
    {
        get => _pathRotationAccurate;
        set
        {
            _pathRotationAccurate = value;
            if (Engine.IsEditorHint())
                UpdateMesh();
        }
    }

    [Export]
    public bool DoubleSided
    {
        get => _doubleSided;
        set
        {
            _doubleSided = value;
            if (Engine.IsEditorHint())
                UpdateMesh();
        }
    }

    [Export]
    public int WidthSubdivisions
    {
        get => _widthSubdivisions;
        set
        {
            _widthSubdivisions = Math.Max(0, value);
            if (Engine.IsEditorHint())
                UpdateMesh();
        }
    }

    private Path3D _path;
    private double _width = 5.0;
    private int _steps = 30;
    private double _smoothness = 0.5;
    private bool _pathRotationAccurate = true;
    private bool _doubleSided = true;
    private int _widthSubdivisions = 0;
    private MeshInstance3D _meshInstance;
    private Curve3D _curve;

    public override void _Ready()
    {
        _meshInstance = new MeshInstance3D();
        _meshInstance.Visible = true;
        AddChild(_meshInstance);
        _meshInstance.Owner = Owner ?? this;

        Callable.From(UpdateMesh).CallDeferred();
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint())
            CheckCurveChanges();
    }

    private void CheckCurveChanges()
    {
        if (!IsInstanceValid(_path))
            return;

        Curve3D newCurve = _path.Curve;
        if (newCurve != _curve)
        {
            _curve = newCurve;
            UpdateMesh();
        }
    }

    private void UpdateMesh()
    {
        if (!IsInstanceValid(_meshInstance))
            return;

        if (!IsInstanceValid(_path))
            return;

        if (_path.Curve == null)
            return;

        if (_path.Curve.PointCount < 2)
            return;

        ArrayMesh mesh = GenerateRiverMesh(_path.Curve, _width, _steps, _smoothness, _pathRotationAccurate, _widthSubdivisions);

        if (mesh != null)
        {
            PostProcessTangents(mesh, _path.Curve, _pathRotationAccurate, _widthSubdivisions);

            _meshInstance.Mesh = mesh;
            if (RiverMaterial != null)
            {
                RiverMaterial.SetShaderParameter(DoubleSidedParam, _doubleSided);
                _meshInstance.MaterialOverride = RiverMaterial;
            }
            else
            {
                var material = new StandardMaterial3D();
                material.CullMode = _doubleSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back;
                material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                material.AlbedoColor = new Color(0.5f, 0.7f, 1.0f, 0.5f);
                _meshInstance.MaterialOverride = material;
            }
        }
        else
        {
            _meshInstance.Mesh = null;
        }
    }

    private static void PostProcessTangents(ArrayMesh mesh, Curve3D curve, bool pathRotationAccurate, int widthSubdivisions)
    {
        if (mesh == null || mesh.GetSurfaceCount() == 0)
            return;

        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        float[] tangents = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();

        double curveLength = curve.GetBakedLength();
        bool closedLoop = curve.Closed;
        int verticesPerSegment = widthSubdivisions + 2;
        int vertexCount = vertices.Length / verticesPerSegment;
        int actualSteps = !closedLoop ? vertexCount - 1 : vertexCount;

        var curveTangents = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            double t = (double)i / actualSteps;
            double offset = t * curveLength;
            curveTangents[i] = SampleForwardVector(curve, t, offset, curveLength, pathRotationAccurate);
        }

        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 tangent = curveTangents[i];

            for (int j = 0; j < verticesPerSegment; j++)
            {
                int vertexIdx = i * verticesPerSegment + j;
                int tIdx = vertexIdx * 4;
                tangents[tIdx] = tangent.X;
                tangents[tIdx + 1] = tangent.Y;
                tangents[tIdx + 2] = tangent.Z;
                tangents[tIdx + 3] = 1.0f;
            }
        }

        arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        mesh.ClearSurfaces();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    private static ArrayMesh GenerateRiverMesh(Curve3D curve, double width, int steps, double smoothness, bool pathRotationAccurate, int widthSubdivisions = 0)
    {
        if (curve == null || curve.PointCount < 2)
            return null;

        if (curve.BakeInterval == 0)
            curve.BakeInterval = 0.1f;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        double curveLength = curve.GetBakedLength();

        int subdivisions = (int)(smoothness * 4.0);
        int actualSteps = steps * (subdivisions + 1);
        int vertexCount = actualSteps + 1;

        Vector3 firstTangent = default;
        bool closedLoop = curve.Closed;
        int verticesPerSegment = widthSubdivisions + 2;

        for (int i = 0; i < vertexCount; i++)
        {
            double t = (double)i / actualSteps;
            double offset = t * curveLength;
            Vector3 position = curve.SampleBaked((float)offset);

            Vector3 forwardVector = SampleForwardVector(curve, t, offset, curveLength, pathRotationAccurate);

            if (i == 0)
                firstTangent = forwardVector;
            else if (closedLoop && i == vertexCount - 1)
                forwardVector = firstTangent;

            Vector3 rightVector = forwardVector.Cross(Vector3.Up).Normalized();

            double uvT = offset / curveLength;
            for (int j = 0; j < verticesPerSegment; j++)
            {
                double widthT = (double)j / (widthSubdivisions + 1);
                Vector3 vertexPos = position - rightVector * (float)width * 0.5f + rightVector * (float)width * (float)widthT;

                st.SetNormal(Vector3.Up);
                st.SetUV(new Vector2((float)uvT, (float)widthT));
                st.AddVertex(vertexPos);
            }
        }

        int triangleCount = vertexCount - 1;
        for (int i = 0; i < triangleCount; i++)
        {
            int baseIdx = i * verticesPerSegment;

            for (int j = 0; j < widthSubdivisions + 1; j++)
            {
                int v0 = baseIdx + j;
                int v1 = baseIdx + j + 1;
                int v2 = baseIdx + verticesPerSegment + j;
                int v3 = baseIdx + verticesPerSegment + j + 1;

                st.AddIndex(v0);
                st.AddIndex(v2);
                st.AddIndex(v1);

                st.AddIndex(v1);
                st.AddIndex(v2);
                st.AddIndex(v3);
            }
        }

        st.GenerateTangents();

        return st.Commit();
    }

    // Shared by GenerateRiverMesh and PostProcessTangents; identical to the two
    // duplicated branches in the GDScript original.
    private static Vector3 SampleForwardVector(Curve3D curve, double t, double offset, double curveLength, bool pathRotationAccurate)
    {
        if (pathRotationAccurate && curve.HasMethod(SampleBakedTangentMethod))
            return curve.Call(SampleBakedTangentMethod, offset).AsVector3().Normalized();

        double tForward = Math.Min(t + 0.01, 1.0);
        double tBackward = Math.Max(t - 0.01, 0.0);
        Vector3 posForward = curve.SampleBaked((float)(tForward * curveLength));
        Vector3 posBackward = curve.SampleBaked((float)(tBackward * curveLength));
        return (posForward - posBackward).Normalized();
    }
}
