// The skirmish part of the game session: the setup made into a SkirmishSession (on the loading thread), the line the
// game starts with, the end of the game (main.js checkEnd: game/systems/gameover.js decides who is out and who wins,
// the session shows the end screen 1.5 s later: "You were victorious!" / "You have been defeated!", the time, kills,
// losses and the epoch reached, Retry / View Statistics / Keep playing / Back to Main menu), and the end screen's
// buttons. The statistics screen (ui/stats.js) opens from the end screen of a skirmish and of a campaign mission.
//
// Ported from remake/src/main.js (startGame for a skirmish, checkEnd, the end screen's hooks) and ui/menu.js end
// (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.UI;
using PwRemake.UI.Mission.Logic;
using PwRemake.View.Audio;
using SimRules = PwRemake.Sim.Rules.Rules;

namespace PwRemake.App;

public partial class GameSession
{
    static readonly string[] EPOCH = { "", "I", "II", "III", "IV", "V" };
    double _endT;
    bool _endShown;

    /// <summary>--end-at &lt;game s&gt;[,lost] [--end-stats]: the game ends (won, or lost) at that game time - the end screen
    /// and, with --end-stats, the statistics over it for a screenshot (tests of the end; a skirmish through the game-over
    /// rules, a mission through its host)</summary>
    void TestEnd()
    {
        if (_testEndDone || !_args.TryGetValue("end-at", out var ea)) return;
        var p = ea.Split(',');
        if (World.Time < Json.ParseNum(p[0], 1e9)) return;
        _testEndDone = true;
        bool won = !(p.Length > 1 && p[1] == "lost");
        L.Info($"--end-at: the game ends ({(won ? "won" : "lost")})");
        Submit(new DebugToolCommand { Tool = won ? "win" : "lose" });           // (a command: a saved game replays it)
        if (_args.ContainsKey("end-stats")) GetTree().CreateTimer(4, true).Timeout += () => { if (IsInstanceValid(this)) EndAction("stats"); };
    }
    bool _testEndDone;

    /// <summary>the skirmish of a GameStart (Mode Skirmish, Skirmish = the setup), made on the loading thread</summary>
    static IGameSim CreateSkirmish(GameStart start, GameConfiguration cfg, string dataDir, SimRules rules, ModelManifest manifest, SimFlags flags, Action<double, string> progress)
    {
        var setup = start.Skirmish ?? throw new InvalidOperationException("a skirmish without its setup");
        string? ula = null;
        if (!string.IsNullOrEmpty(setup.Map))
            ula = SkirmishMaps.Resolve(cfg, setup.Map) ?? throw new FileNotFoundException($"The map was not found in the installation: {setup.Map}");
        var forest = ForestData.LoadFile(Path.Combine(dataDir, "forest.json"));
        var brushes = GroundBrushes.LoadFile(Path.Combine(dataDir, "assets", "terrain", "objbrush.json"));
        var aiData = Json.TryParseFile(Path.Combine(dataDir, "ai.json"));
        var s = setup.Clone();
        s.Debug ??= start.Debug;
        var sk = SkirmishSession.Create(s, ula, rules, manifest, forest, brushes, aiData, progress, AiControlScript(cfg), flags);
        Seats.Apply(sk.World, start.Seats);           // (a networked game: who commands which player - docs/MULTIPLAYER.md)
        return sk;
    }

    /// <summary>the installation's AI control script (MIRAGE's gifts, AiData.ReadControlScript), or null</summary>
    public static string? AiControlScript(GameConfiguration cfg)
    {
        try { var f = cfg.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl"); return f != null ? File.ReadAllText(f) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>main.js: "Build up your settlement and defeat the ..." - the players against you</summary>
    string SkirmishStartLine()
    {
        var foes = World.Players.Where(p => p != Me && Me.IsEnemy(p)).Select(p => p.Name).ToList();
        if (foes.Count == 0) return Sim.Title;
        var who = foes.Count == 1 ? foes[0] : string.Join(", ", foes.Take(foes.Count - 1)) + " and " + foes[^1];
        return World.Opts.Mode switch
        {
            "domination" => $"Domination on {Sim.Title}: hold the flags against the {who}.",
            "defender" => Me.Team == 1 ? $"Defender on {Sim.Title}: hold out against the {who}." : $"Defender on {Sim.Title}: destroy the defender, the {who}.",
            _ => $"Build up your settlement and defeat the {who}.",
        };
    }

    /// <summary>main.js checkEnd: once a second; the human player is out or has won (gameover.js) -> the end screen
    /// 1.5 s later (real time), the combat music off</summary>
    void CheckSkirmishEnd(double dt)
    {
        _endT -= dt;
        if (_endT > 0 || _endShown) return;
        _endT = 1;
        bool lost = Me.Defeated, won = Me.Won;
        if (!lost && !won) return;
        _endShown = true;
        L.Info($"skirmish over: you {(won && !lost ? "won" : "lost")} at {World.Time:0} s");
        try { GameAudio.Current?.EndGame(); } catch (Exception) { }
        int t = (int)Math.Floor(World.Time);
        Presenter.EndStats = ($"{t / 60}:{t % 60:00}", Me.Kills, Me.Lost);
        var e = new EndScreenInfo
        {
            Won = won && !lost, Skirmish = true, Title = Sim.Title, Epoch = EPOCH[Math.Clamp(Me.Epoch(), 0, 5)],
            Result = won && !lost ? Rules.Ui("_EndScreen_YouWin", "You were victorious!") : Rules.Ui("_EndScreen_YouLose", "You have been defeated!"),
        };
        void Show() { if (IsInstanceValid(this)) Presenter.ShowEndScreen(e); }
        GetTree().CreateTimer(1.5, true).Timeout += Show;
    }

    /// <summary>the end screen's buttons in a skirmish: Retry (the same setup again), Keep playing, Back to Main menu
    /// (the skirmish menu); false: not a skirmish's</summary>
    bool SkirmishEndAction(string a)
    {
        switch (a)
        {
            case "retry":
                Boot.Instance.Show(LoadingScreen.ForSkirmish(Start));
                return true;
            case "watch":
                Presenter.CloseEnd();
                return true;
            case "load":
                Boot.Instance.Show(new LoadGameScreen());
                return true;
            case "menu":
                Boot.Instance.Show(new SkirmishMenu());
                return true;
            default: return false;
        }
    }

    /// <summary>the end screen's "View Statistics" (ui/stats.js): the statistics over the game, then the end screen again</summary>
    void ShowEndStats()
    {
        Presenter.SetEndVisible(false);
        HudView.ShowStats(() => Presenter.SetEndVisible(true), Mission?.Engine?.Quests);
    }

    /// <summary>a campaign mission is won: the next mission's army budget is the bonus points earned here, never less
    /// than before (main.js G.endMission, CampaignMgr.usl:145-183)</summary>
    void RememberCredits(bool won)
    {
        var next = won ? Mission?.Next : null;
        if (next == null || Mission == null) return;
        var cr = GameApp.Settings.CampaignCredits;
        var key = $"{Start.Configuration}:{next.Id}";
        cr[key] = Math.Max(cr.GetValueOrDefault(key), Mission.Campaign.Boni);
        GameApp.Settings.Save();
    }
}
