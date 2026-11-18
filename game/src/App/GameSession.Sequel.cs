// A mission of the remake's own campaigns in the game (docs/SEQUEL.md): Containment (1964, 1966) and the scenarios.
// The simulation is SequelSession (src/Sim/Sequel); here: making it on the loading screen, its interface (SequelHud:
// objectives, radio, clock, electricity, powers; the briefing window; the minimap markers), its drawn effects
// (View/Sequel/SequelFx: rift pulse, searchlights, wires, rotors, set pieces, the rocket, the roads, the night), its
// events (messages, effects, arcs, shakes), the end of the mission and the menus' progress.
// Ported from remake/src/main.js (the G.sequel parts) and remake/src/ui/sequel_ui.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Sequel;
using PwRemake.Sim.World;
using PwRemake.UI;
using PwRemake.UI.Sequel;
using PwRemake.View.Sequel;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>a mission of the remake's own campaigns (null otherwise)</summary>
    public SequelSession? Sequel { get; private set; }
    public SequelHud? SequelUi { get; private set; }
    SequelFx? _sequelFx;
    bool _sequelEndShown;

    static IGameSim CreateSequel(GameStart start, GameConfiguration cfg, string dataDir, PwRemake.Sim.Rules.Rules rules, ModelManifest manifest, SimFlags flags, Action<double, string> progress) =>
        SequelSession.Create(start, cfg, SequelSession.KindOf(start) == "scenario" ? rules : null, manifest, flags, progress, dataDir: dataDir);

    /// <summary>_Ready's part: the interface and the effects of the mission</summary>
    void ReadySequel()
    {
        if (Sequel == null) return;
        var R = Sequel.Runtime;
        _sequelFx = new SequelFx(this, Sequel) { Name = "SequelFx" };
        AddChild(_sequelFx);
        SequelUi = new SequelHud(this, Sequel) { Name = "SequelHud" };
        AddChild(SequelUi);
        HudView.Overlays.Add(SequelUi.Panel);
        HudView.Overlays.Add(SequelUi.Radio);
        HudView.MinimapExtra = () => R.Markers.Select(m => (m.X, m.Z, m.Color));
        FrameEvents += SequelEvents;
        // the briefing before the mission (the game waits); a loaded game was begun already
        if (Resume == null && !R.Started)
        {
            if (_args.ContainsKey("skip-briefing") || _args.ContainsKey("autoplay")) BeginSequel();
            else SequelUi.ShowBriefing(BeginSequel);
        }
    }

    /// <summary>the briefing's pause (as the mission UI's: PausedByUi)</summary>
    public void SetSequelPause(bool on) => PausedByUi = on;

    /// <summary>the briefing was read: the mission's clock and script start (a command: saved games replay it)</summary>
    public void BeginSequel()
    {
        if (Sequel == null || Sequel.Runtime.Started) return;
        Submit(new SequelCommand { What = "begin" });
    }

    /// <summary>a command power of the mission: at once, or after a click on the place (the input's callback mode)</summary>
    public void UseSequelPower(string id)
    {
        if (Sequel == null) return;
        var R = Sequel.Runtime;
        if (!SequelRuntime.POWERS.TryGetValue(id, out var P)) return;
        var st = R.PowerState(id);
        if (st != "ready") { Message(st == "cost" ? $"Not enough command points ({P.Cost} needed)" : "Not ready yet", "bad"); return; }
        if (!P.Target) { Submit(new SequelCommand { What = "power", Id = id }); return; }
        Message($"{P.Name}: click the place");
        Input.SetMode("callback", new Action<double, double, Entity?>((x, z, _) => Submit(new SequelCommand { What = "power", Id = id, X = x, Z = z })));
    }

    void SequelEvents(IReadOnlyList<WorldEvent> evs)
    {
        foreach (var e in evs)
        {
            switch (e.Type)
            {
                case "sqmsg": Message(e.Data["text"] as string ?? "", e.Data["kind"] as string ?? ""); break;
                case "sqfx":
                    {
                        var p = new Vector3((float)e.Num("x"), (float)e.Num("y"), (float)e.Num("z"));
                        var kind = e.Data["fx"] as string;
                        if (kind == "launch") { _sequelFx?.Launch(); break; }
                        if (Effects == null) break;
                        if (kind == "dust") Effects.Fx.Dust(p, (float)e.Num("scale", 2)); else Effects.Fx.Explosion(p, (float)e.Num("scale", 1));
                        break;
                    }
                case "sqarc": _sequelFx?.Arc(new Vector3((float)e.Num("ax"), (float)e.Num("ay"), (float)e.Num("az")), new Vector3((float)e.Num("bx"), (float)e.Num("by"), (float)e.Num("bz"))); break;
                case "sqshake": Camera.AddShake(1.0, e.Num("strength", 10), 100, Camera.Target, 60); break;
            }
        }
    }

    /// <summary>every frame: the end of the mission (the end screen after its delay) and the menu's progress</summary>
    void SequelFrame()
    {
        if (Sequel == null || _sequelEndShown) return;
        var end = Sequel.Runtime.Ended;
        if (end == null || _replay != null && !_replay.Done) return;
        _sequelEndShown = true;
        if (end.Value.Won) SequelProgress.Won(Sequel);
        MissionEnded(end.Value.Won, end.Value.Text, end.Value.Won ? 4 : 3);
    }

    /// <summary>the end screen's buttons of these missions: next | retry | menu (else as a campaign mission)</summary>
    bool SequelEndAction(string a)
    {
        if (Sequel == null) return false;
        var kind = Sequel.Kind;
        Func<Node> menu = kind == "scenario" ? () => new ScenarioMenu() : () => new SequelMenu();
        switch (a)
        {
            case "next":
                {
                    var next = SequelCatalog.List(kind).FirstOrDefault(m => m.Id > Start.Mission);
                    if (next == null || !Sequel.Won) { Boot.Instance.Show(menu()); return true; }
                    SequelMenu.Launch(next, Start.Tribe, Start.Difficulty, Start.Debug);
                    return true;
                }
            case "retry": SequelMenu.Launch(Sequel.Entry, Start.Tribe, Start.Difficulty, Start.Debug); return true;
            case "exit": return false;
            case "load": return false;
            default: Boot.Instance.Show(menu()); return true;
        }
    }
}
