// "Game data": what the game plays with and how to make it. The configurations are made by the prepare tool from the
// player's own ParaWorld installation (tools/prepare, Prepare.bat): it needs the toolkit's converted data (models,
// textures, interface, rules - made once by the ParaWorld Toolkit's launcher) and converts the campaign missions to
// Lua. This screen lists what is found, and can run the tool.
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;

namespace PwRemake.UI;

public partial class SetupScreen : MenuScreen
{
    static readonly Logger L = Log.For("UI.Setup");
    Label _out = null!;

    public SetupScreen() : base("Game data", minWidth: 680) { }

    protected override void Build()
    {
        var b = Window.Body;
        b.AddChild(PwTheme.Heading("Game data", 22));
        b.AddChild(PwTheme.Para(
            "The remake plays with your own copy of ParaWorld: its rules, models, sounds and missions are read from your " +
            "installation and the ParaWorld Toolkit's converted data. The prepare tool makes a game configuration for the " +
            "official game and one for MIRAGE (when it is installed) and converts the campaign missions to Lua.", 13));

        var g = Group("Configurations");
        b.AddChild(g);
        if (GameApp.Configurations.Count == 0)
            g.AddChild(PwTheme.Para($"None yet in {GameApp.ContentDir}.", 13, PwTheme.Lost));
        foreach (var c in GameApp.Configurations)
        {
            var p = c.Problems();
            g.AddChild(PwTheme.Para($"{c.Title} ({c.Id}): {(p.Count == 0 ? "ready" : string.Join("; ", p))}\n    data {c.Data}\n    prepared {c.Prepared}", 12,
                p.Count == 0 ? PwTheme.Text : PwTheme.Lost));
        }

        var h = Group("Prepare");
        b.AddChild(h);
        h.AddChild(PwTheme.Para(
            "Run Prepare.bat in the game folder (or the button below). It asks for the ParaWorld folder (e.g. E:\\Paraworld) " +
            "and finds the toolkit's data in <ParaWorld>\\export\\remake or in %APPDATA%\\ParaWorldToolkit. " +
            "Python 3 and the ParaWorld Toolkit are needed once for this step (docs/CONTENT.md).", 12));
        _out = PwTheme.Para("", 11, PwTheme.Dim);
        h.AddChild(_out);
        b.AddChild(Row(
            PwTheme.Btn("Run the prepare tool", RunPrepare, 220),
            PwTheme.Btn("Read again", () => { GameApp.ReloadContent(); Boot.Instance.Show(new SetupScreen()); }, 140),
            PwTheme.Btn("Back", () => Boot.Instance.Show(new TitleScreen()), 120)));
    }

    void RunPrepare()
    {
        var root = AppPaths.RootDir;
        var script = Path.Combine(root, "tools", "prepare", "prepare.py");
        if (!File.Exists(script)) { _out.Text = $"not found: {script}"; return; }
        var output = new Godot.Collections.Array();
        string py = OS.GetName() == "Windows" ? "python" : "python3";
        var args = new List<string> { script, "--content", GameApp.ContentDir };
        L.Info($"running {py} {string.Join(" ", args)}");
        _out.Text = "running … (this takes a few minutes; the game waits)";
        int code = OS.Execute(py, args.ToArray(), output, true);
        var sb = new StringBuilder();
        foreach (var o in output) sb.Append(o.AsString());
        var text = sb.ToString();
        foreach (var line in text.Split('\n')) if (line.Trim() != "") L.Info("prepare: " + line.TrimEnd());
        L.Info($"prepare tool exit code {code}");
        GameApp.ReloadContent();
        Boot.Instance.Show(new SetupScreen());
    }
}
