using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Extensions;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using System;

namespace StrideSkySystem;

/// <summary>
/// Drop-in procedural sky + day/night cycle. Attach it to an entity and it spawns a
/// camera-following dome shaded by <c>SkyProcedural.sdsl</c> (zenith→horizon gradient,
/// sun disc + halo, a textured moon with phases, drifting clouds, twinkling stars and a
/// sunset/anti-twilight glow). A built-in clock rotates the sun and moon, fades the sky
/// between day and night, and (optionally) drives a directional <see cref="SunLight"/>
/// so the whole scene lights to match — no textures or cubemaps required.
/// </summary>
[ComponentCategory("Sky")]
public sealed class ProceduralSky : SyncScript
{
    /// <summary>Dome radius. Keep it below the camera far-plane and beyond your world.</summary>
    public float DomeRadius { get; set; } = 800f;

    /// <summary>Camera the dome follows. Auto-detected from the scene if left empty.</summary>
    public CameraComponent? Camera { get; set; }

    /// <summary>Optional directional light driven as the sun (direction, colour, intensity).</summary>
    public LightComponent? SunLight { get; set; }

    /// <summary>Peak sun-light intensity at midday (only used when <see cref="SunLight"/> is set).</summary>
    public float MaxSunIntensity { get; set; } = 3.0f;

    /// <summary>Current time of day in hours [0,24). 6 = sunrise, 12 = noon, 18 = sunset.</summary>
    public float TimeOfDay { get; set; } = 9f;

    /// <summary>Real seconds for one full day. 0 freezes time at <see cref="TimeOfDay"/>.</summary>
    public float DayLengthSeconds { get; set; } = 120f;

    /// <summary>Cloud cover, 0 = clear, 1 = overcast.</summary>
    public float CloudCoverage { get; set; } = 0.4f;

    /// <summary>Overall star brightness at night.</summary>
    public float StarBrightness { get; set; } = 2.5f;

    // Sky palette (linear RGB).
    public Color3 ZenithDayColor { get; set; }    = new(0.08f, 0.28f, 0.78f);
    public Color3 HorizonDayColor { get; set; }   = new(0.32f, 0.55f, 0.85f);
    public Color3 ZenithNightColor { get; set; }  = new(0.01f, 0.015f, 0.04f);
    public Color3 HorizonNightColor { get; set; } = new(0.04f, 0.05f, 0.10f);

    // ── Runtime-string ParameterKeys (names match SkyProcedural.sdsl) ────────
    private static readonly ValueParameterKey<Vector3> KeySunDirection      = ParameterKeys.NewValue<Vector3>(default, "SkyProcedural.SunDirection");
    private static readonly ValueParameterKey<Vector3> KeyMoonDirection     = ParameterKeys.NewValue<Vector3>(default, "SkyProcedural.MoonDirection");
    private static readonly ValueParameterKey<float>   KeyNightFactor       = ParameterKeys.NewValue<float>(0f, "SkyProcedural.NightFactor");
    private static readonly ValueParameterKey<Vector3> KeyZenithDayColor    = ParameterKeys.NewValue<Vector3>(default, "SkyProcedural.ZenithDayColor");
    private static readonly ValueParameterKey<Vector3> KeyHorizonDayColor   = ParameterKeys.NewValue<Vector3>(default, "SkyProcedural.HorizonDayColor");
    private static readonly ValueParameterKey<Vector3> KeyZenithNightColor  = ParameterKeys.NewValue<Vector3>(default, "SkyProcedural.ZenithNightColor");
    private static readonly ValueParameterKey<Vector3> KeyHorizonNightColor = ParameterKeys.NewValue<Vector3>(default, "SkyProcedural.HorizonNightColor");
    private static readonly ValueParameterKey<Vector3> KeySunColor          = ParameterKeys.NewValue<Vector3>(default, "SkyProcedural.SunColor");
    private static readonly ValueParameterKey<float>   KeyStarBrightness    = ParameterKeys.NewValue<float>(0f, "SkyProcedural.StarBrightness");
    private static readonly ValueParameterKey<float>   KeyCloudCoverage     = ParameterKeys.NewValue<float>(0f, "SkyProcedural.CloudCoverage");
    private static readonly ValueParameterKey<float>   KeyCloudSeed         = ParameterKeys.NewValue<float>(0f, "SkyProcedural.CloudSeed");
    private static readonly ValueParameterKey<Vector3> KeyDomeCenter        = ParameterKeys.NewValue<Vector3>(default, "SkyProcedural.DomeCenter");

    private Entity? _domeEntity;
    private Material? _material;
    private Mesh? _domeMesh;
    private LightComponent? _autoSun;

    public override void Start()
    {
        var primitive = GeometricPrimitive.Sphere.New(GraphicsDevice, 1f, 32);

        var model = new Model();
        _domeMesh = new Mesh
        {
            Draw = primitive.ToMeshDraw(),
            BoundingSphere = new BoundingSphere(Vector3.Zero, 1f),
            BoundingBox = new BoundingBox(new Vector3(-1f), new Vector3(1f)),
        };
        model.Meshes.Add(_domeMesh);

        // Emissive-only sky: the visible colour is purely the procedural emission,
        // unaffected by scene lights. We view the sphere from the inside (CullMode.None).
        var skyColor = new ComputeShaderClassColor { MixinReference = "SkyProcedural" };
        _material = Material.New(GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Emissive = new MaterialEmissiveMapFeature(skyColor) { Intensity = new ComputeFloat(1f) },
            },
        });
        _material.Passes[0].CullMode = CullMode.None;
        // Depth-read (no write) + opaque blend, so a Fog post-process skips the sky and the
        // dome sorts behind every other transparent object.
        _material.Passes[0].HasTransparency = true;
        _material.Passes[0].BlendState = BlendStates.Opaque;
        model.Materials.Add(new MaterialInstance(_material));

        _domeEntity = new Entity("SkyDome");
        _domeEntity.Add(new ModelComponent { Model = model, IsShadowCaster = false });
        _domeEntity.Transform.Scale = new Vector3(DomeRadius);
        Entity.Scene.Entities.Add(_domeEntity);

        // The procedural dome replaces the cubemap background visual.
        DisableBackgrounds(Entity.Scene);
    }

    public override void Update()
    {
        if (_domeEntity == null || _material == null) return;

        float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
        if (DayLengthSeconds > 0.01f)
        {
            TimeOfDay += 24f * dt / DayLengthSeconds;
            TimeOfDay %= 24f;
            if (TimeOfDay < 0f) TimeOfDay += 24f;
        }

        // Sun sweeps an east→up→west→down arc over the day; moon opposite.
        float a = (TimeOfDay / 24f) * MathF.PI * 2f - MathF.PI / 2f;
        var sunDir = Vector3.Normalize(new Vector3(MathF.Cos(a), MathF.Sin(a), 0.15f));
        var moonDir = -sunDir;
        float sunAlt = sunDir.Y;

        float night = 1f - SmoothStep(-0.12f, 0.15f, sunAlt);
        var sunColor = Vector3.Lerp(
            new Vector3(1.0f, 0.45f, 0.20f),   // low sun: warm red/orange
            new Vector3(1.0f, 0.95f, 0.80f),   // high sun: near-white
            SmoothStep(0.0f, 0.30f, sunAlt));

        // Follow the camera so the dome is always centred on the viewer.
        var camEntity = Camera?.Entity ?? FindCamera(Entity.Scene);
        if (camEntity != null)
        {
            camEntity.Transform.UpdateWorldMatrix();
            camEntity.Transform.GetWorldTransformation(out var camPos, out var camRot, out _);
            _domeEntity.Transform.Position = camPos;
            _domeEntity.Transform.UpdateWorldMatrix();

            // Push the mesh bounding box far along the view direction so the transparent
            // sort always ranks the sky as the furthest object (drawn first).
            if (_domeMesh != null)
            {
                var worldForward = Vector3.Transform(-Vector3.UnitZ, camRot);
                float invScale = 1f / MathF.Max(DomeRadius, 1f);
                var localCenter = worldForward * (1_000_000f * invScale);
                var extent = new Vector3(1_000_000f * invScale + 2f);
                _domeMesh.BoundingBox = new BoundingBox(localCenter - extent, localCenter + extent);
            }
        }

        // Drive the sun light to match the sky (assigned one, else the first directional light).
        var sun = SunLight ?? (_autoSun ??= FindDirectionalLight(Entity.Scene));
        if (sun != null && sun.Type is LightDirectional dirLight)
        {
            // A Stride directional light travels along the entity's forward (-Z) axis. Point +Z
            // toward the sun so -Z (the light direction) heads away from it, down to the ground.
            sun.Entity.Transform.Rotation = LookAlongZ(sunDir);
            dirLight.Color = new ColorRgbProvider(new Color3(sunColor.X, sunColor.Y, sunColor.Z));
            sun.Intensity = MaxSunIntensity * MathUtil.Clamp(SmoothStep(-0.05f, 0.25f, sunAlt), 0f, 1f);
        }

        var p = _material.Passes[0].Parameters;
        p.Set(KeyDomeCenter, _domeEntity.Transform.Position);
        p.Set(KeySunDirection, sunDir);
        p.Set(KeyMoonDirection, moonDir);
        p.Set(KeySunColor, sunColor);
        p.Set(KeyNightFactor, night);
        p.Set(KeyCloudCoverage, CloudCoverage);
        p.Set(KeyCloudSeed, 0f);
        p.Set(KeyStarBrightness, StarBrightness);
        p.Set(KeyZenithDayColor,    (Vector3)ZenithDayColor);
        p.Set(KeyHorizonDayColor,   (Vector3)HorizonDayColor);
        p.Set(KeyZenithNightColor,  (Vector3)ZenithNightColor);
        p.Set(KeyHorizonNightColor, (Vector3)HorizonNightColor);
    }

    public override void Cancel()
    {
        if (_domeEntity?.Scene != null)
            _domeEntity.Scene.Entities.Remove(_domeEntity);
        _domeEntity = null;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = MathUtil.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Rotation whose +Z axis points along <paramref name="zAxis"/> (so a light on
    /// that entity, which emits along -Z, shines the opposite way).</summary>
    private static Quaternion LookAlongZ(Vector3 zAxis)
    {
        zAxis = Vector3.Normalize(zAxis);
        var up = MathF.Abs(zAxis.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var x = Vector3.Normalize(Vector3.Cross(up, zAxis));
        var y = Vector3.Cross(zAxis, x);
        var m = Matrix.Identity;
        m.Right = x; m.Up = y; m.Backward = zAxis;
        Quaternion.RotationMatrix(ref m, out var q);
        return q;
    }

    // ── Camera + background helpers ──────────────────────────────────────────

    private static LightComponent? FindDirectionalLight(Scene? scene)
    {
        if (scene == null) return null;
        var root = scene;
        while (root.Parent != null) root = root.Parent;
        return FindDirectionalLightInScene(root);
    }

    private static LightComponent? FindDirectionalLightInScene(Scene scene)
    {
        foreach (var e in scene.Entities)
        {
            var hit = FindDirectionalLightInEntity(e);
            if (hit != null) return hit;
        }
        foreach (var child in scene.Children)
        {
            var hit = FindDirectionalLightInScene(child);
            if (hit != null) return hit;
        }
        return null;
    }

    private static LightComponent? FindDirectionalLightInEntity(Entity entity)
    {
        var lc = entity.Get<LightComponent>();
        if (lc != null && lc.Type is LightDirectional) return lc;
        foreach (var t in entity.Transform.Children)
        {
            var hit = FindDirectionalLightInEntity(t.Entity);
            if (hit != null) return hit;
        }
        return null;
    }

    private static Entity? FindCamera(Scene? scene)
    {
        if (scene == null) return null;
        var root = scene;
        while (root.Parent != null) root = root.Parent;
        return FindCameraInScene(root);
    }

    private static Entity? FindCameraInScene(Scene scene)
    {
        foreach (var e in scene.Entities)
        {
            var hit = FindCameraInEntity(e);
            if (hit != null) return hit;
        }
        foreach (var child in scene.Children)
        {
            var hit = FindCameraInScene(child);
            if (hit != null) return hit;
        }
        return null;
    }

    private static Entity? FindCameraInEntity(Entity entity)
    {
        var cc = entity.Get<CameraComponent>();
        if (cc != null && cc.Enabled) return entity;
        foreach (var t in entity.Transform.Children)
        {
            var hit = FindCameraInEntity(t.Entity);
            if (hit != null) return hit;
        }
        return null;
    }

    private static void DisableBackgrounds(Scene? scene)
    {
        if (scene == null) return;
        var root = scene;
        while (root.Parent != null) root = root.Parent;
        DisableBackgroundsInScene(root);
    }

    private static void DisableBackgroundsInScene(Scene scene)
    {
        foreach (var e in scene.Entities) DisableBackgroundOnEntity(e);
        foreach (var child in scene.Children) DisableBackgroundsInScene(child);
    }

    private static void DisableBackgroundOnEntity(Entity entity)
    {
        var bg = entity.Get<BackgroundComponent>();
        if (bg != null) bg.Enabled = false;
        foreach (var t in entity.Transform.Children) DisableBackgroundOnEntity(t.Entity);
    }
}
