// The light and ground of the model viewer and the view tests: a sun with shadows and a sky-coloured ambient light,
// close to the remake's renderer (engine/renderer.js: hemisphere light 0xe6eef2 / 0x5a5236, sun 0xfff2d8).
using Godot;
using PwRemake.Sim.Content;

namespace PwRemake.View.Tools;

public static class ViewerStage
{
    public static DirectionalLight3D Setup(Node3D parent, bool ground = true)
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.55f, 0.66f, 0.78f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.78f, 0.80f, 0.76f),
            AmbientLightEnergy = 0.75f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        parent.AddChild(new WorldEnvironment { Environment = env, Name = "Environment" });
        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.95f, 0.85f), LightEnergy = 1.1f, ShadowEnabled = true,
            DirectionalShadowMaxDistance = 160, Name = "Sun",
        };
        parent.AddChild(sun);
        sun.Rotation = new Vector3(Mathf.DegToRad(-50), Mathf.DegToRad(-35), 0);
        if (ground)
        {
            var g = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(400, 400) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.47f, 0.30f), Roughness = 1, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled },
                Name = "Ground",
            };
            parent.AddChild(g);
        }
        return sun;
    }

    /// <summary>Night light for the viewer (the game's atmosphere does this itself).</summary>
    public static void SetNight(Node3D parent, bool night)
    {
        var env = parent.GetNode<WorldEnvironment>("Environment").Environment;
        var sun = parent.GetNode<DirectionalLight3D>("Sun");
        env.AmbientLightEnergy = night ? 0.25f : 0.75f;
        env.BackgroundColor = night ? new Color(0.06f, 0.08f, 0.16f) : new Color(0.55f, 0.66f, 0.78f);
        sun.LightEnergy = night ? 0.15f : 1.1f;
        sun.LightColor = night ? new Color(0.6f, 0.7f, 1f) : new Color(1f, 0.95f, 0.85f);
    }

    /// <summary>
    /// The configuration of the view tools: a command line "--data=&lt;folder&gt;" (the converted data: gamedata.json,
    /// assets/...), else the environment variable PW_DATA, else the staged test data of the development container.
    /// </summary>
    public static GameConfiguration TestConfig()
    {
        string data = System.Environment.GetEnvironmentVariable("PW_DATA") ?? "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a";
        foreach (var a in OS.GetCmdlineUserArgs()) if (a.StartsWith("--data=")) data = a.Substring(7);
        return new GameConfiguration { Id = "view-test", Title = "View test", Data = data, Install = "" };
    }
}
