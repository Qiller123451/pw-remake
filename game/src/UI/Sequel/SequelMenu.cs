// The menus of the remake's own campaigns (docs/SEQUEL.md "Menus"; menu.js sequel() / scenarios()):
//   * "Containment (1964)": the 1964 and 1966 missions in order, each with its place, date, briefing and what it brings
//     in; a mission opens when the one before it was won (the debug options open all); the difficulty. It is played
//     with the MIRAGE configuration (its SEAS): the menu offers to switch to it.
//   * "Scenarios": the five scenarios, all open, played as any of the four tribes (the base game: its three) - the menu
//     marks the tribes that won each.
// Missions that do not play yet are greyed out (SequelStatus), as the campaign menu does (CampaignStatus).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Sequel;
using PwRemake.UI.Loading;

namespace PwRemake.UI.Sequel;

public partial class SequelMenu : MenuScreen
{
    static readonly Logger L = Log.For("UI.Sequel");
    protected readonly string Kind;
    protected List<SequelEntry> Missions = new();
    ItemList _list = null!;
    Label _text = null!;
    OptionButton _diff = null!;
    protected OptionButton? TribeBox;
    Button _start = null!;

    public SequelMenu() : this("sequel", "Containment") { }
    protected SequelMenu(string kind, string caption) : base(caption, minWidth: 940) { Kind = kind; }

    protected override void Build()
    {
        var s = GameApp.Settings;
        var cfg = GameApp.Config;
        Missions = SequelCatalog.List(Kind);
        var b = Window.Body;
        b.AddChild(PwTheme.Heading(Kind == "scenario" ? "Scenarios" : "Containment (1964)", 20));
        b.AddChild(PwTheme.Para(Kind == "scenario"
            ? "Standalone missions in ParaWorld's own world, apart from both campaigns. Play each as any tribe."
            : "The remake's own campaign: the SEAS of the 1960s against the dinosaurs that come through the rifts. 1964: missions 1-11, 1966: missions 12-16.", 13, PwTheme.Dim));
        // Containment needs the MIRAGE configuration (its SEAS); offer to switch
        bool mirage = cfg != null && string.Equals(cfg.Mod, SequelCatalog.SEQUEL_MOD, StringComparison.OrdinalIgnoreCase);
        if (Kind == "sequel" && !mirage)
        {
            var m = GameApp.Configurations.FirstOrDefault(c => string.Equals(c.Mod, SequelCatalog.SEQUEL_MOD, StringComparison.OrdinalIgnoreCase) && c.Problems().Count == 0);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 16);
            var note = PwTheme.Para($"Containment is played with the {SequelCatalog.SEQUEL_MOD} configuration (its SEAS).", 13, PwTheme.Lost);
            note.CustomMinimumSize = new Vector2(560, 0);
            note.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            note.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(note);
            if (m != null)
            {
                var sw = PwTheme.Btn($"Switch to {m.Title}", () => { GameApp.UseConfiguration(m); GameApp.Settings.Save(); Boot.Instance.Show(new SequelMenu()); }, 200);
                sw.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                row.AddChild(sw);
            }
            b.AddChild(row);
        }

        var g = Group("Mission");
        b.AddChild(g);
        var split = new HBoxContainer { CustomMinimumSize = new Vector2(860, 320) };
        split.AddThemeConstantOverride("separation", 12);
        g.AddChild(split);
        _list = new ItemList { CustomMinimumSize = new Vector2(330, 320), SelectMode = ItemList.SelectModeEnum.Single };
        split.AddChild(_list);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(510, 320), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        split.AddChild(scroll);
        _text = PwTheme.Para("", 14, PwTheme.Text);
        _text.CustomMinimumSize = new Vector2(490, 0);
        scroll.AddChild(_text);

        int sel = 0, chosen = Kind == "scenario" ? s.ScenarioMission : s.SequelMission;
        for (int i = 0; i < Missions.Count; i++)
        {
            var m = Missions[i];
            string label = Kind == "scenario" ? $"{m.Id}. {m.Title}" : $"{(m.Id == 12 ? "1966 · " : "")}{m.Id}. {m.Title}";
            if (Kind == "scenario" && s.ScenarioWon.TryGetValue(m.Id.ToString(), out var won) && won.Count > 0) label += "  ✔ " + string.Join(" ", won.Select(TribeInfo.Name));
            int idx = _list.AddItem(label);
            var st = SequelStatus.Of(Kind, m.Id);
            bool locked = Kind == "sequel" && m.Id > s.SequelUnlocked && !s.UseDebug;
            if (st == MissionState.NotYet) { _list.SetItemCustomFgColor(idx, PwTheme.Dim); _list.SetItemTooltip(idx, "Not adapted for the remake yet"); }
            else if (locked) { _list.SetItemCustomFgColor(idx, PwTheme.Dim); _list.SetItemTooltip(idx, "Win the mission before it first"); }
            if (m.Id == chosen) sel = i;
        }
        _list.ItemSelected += i => ShowMission((int)i);

        var opts = new HBoxContainer();
        opts.AddThemeConstantOverride("separation", 24);
        b.AddChild(opts);
        if (Kind == "scenario")
        {
            var gt = Group("Your tribe");
            opts.AddChild(gt);
            TribeBox = new OptionButton { CustomMinimumSize = new Vector2(260, 28) };
            foreach (var t in SequelCatalog.SCENARIO_TRIBES.Where(t => cfg?.PlayableTribes.Contains(t) ?? t != "SEAS"))
            {
                int i = TribeBox.ItemCount;
                var icon = TribeInfo.Icon(t);
                if (icon != null) TribeBox.AddIconItem(icon, TribeInfo.Name(t), i); else TribeBox.AddItem(TribeInfo.Name(t), i);
                TribeBox.SetItemMetadata(i, t);
                if (t == s.ScenarioTribe) TribeBox.Select(i);
            }
            TribeBox.ItemSelected += _ => s.ScenarioTribe = TribeBox.GetItemMetadata(TribeBox.Selected).AsString();
            gt.AddChild(TribeBox);
        }
        var gd = Group("Difficulty");
        opts.AddChild(gd);
        _diff = new OptionButton { CustomMinimumSize = new Vector2(220, 28) };
        foreach (var (n, i) in new[] { ("Easy", 0), ("Normal", 1), ("Hard", 2) }) _diff.AddItem(n, i);
        _diff.Select(Math.Clamp(s.Difficulty, 0, 2));
        _diff.ItemSelected += i => s.Difficulty = (int)i;
        gd.AddChild(_diff);
        var dbg = PwTheme.Btn("Debug options …", () => Boot.Instance.Show(new DebugOptionsWindow(() => Kind == "scenario" ? new ScenarioMenu() : new SequelMenu())), 170);
        opts.AddChild(dbg);

        _start = PwTheme.Btn("Start mission", Start, 170);
        b.AddChild(Row(PwTheme.Btn("Back", () => { s.Save(); Boot.Instance.Show(new TitleScreen()); }, 140), _start));
        if (Missions.Count == 0) { _text.Text = $"No missions in {SequelCatalog.Dir(Kind)}."; _start.Disabled = true; }
        else { _list.Select(sel); ShowMission(sel); }
        if (Kind == "sequel" && !mirage) _start.Disabled = true;
    }

    void ShowMission(int i)
    {
        if (i < 0 || i >= Missions.Count) return;
        var s = GameApp.Settings;
        var m = Missions[i];
        if (Kind == "scenario") s.ScenarioMission = m.Id; else s.SequelMission = m.Id;
        var st = SequelStatus.Of(Kind, m.Id);
        bool locked = Kind == "sequel" && m.Id > s.SequelUnlocked && !s.UseDebug;
        var classes = Kind == "sequel" ? SequelCatalog.Classes() : null;
        var news = m.Unlocks.Select(u => classes?.All.Get(u)?.Desc).Where(d => !string.IsNullOrEmpty(d)).ToList();
        var t = $"{m.Title}\n{m.Place}{(m.Date != "" ? " — " + m.Date : "")}\n\n{string.Join("\n\n", m.Briefing)}";
        if (news.Count > 0) t += $"\n\nNew: {string.Join(", ", news)}";
        if (Kind == "scenario" && s.ScenarioWon.TryGetValue(m.Id.ToString(), out var won) && won.Count > 0) t += $"\n\nWon as: {string.Join(", ", won.Select(TribeInfo.Name))}";
        if (st == MissionState.NotYet) t += "\n\nNot adapted for the remake yet.";
        else if (st == MissionState.Partial) t += "\n\nPlayable in parts: " + SequelStatus.Note(Kind, m.Id);
        if (locked) t += "\n\nWin the mission before it first.";
        _text.Text = t;
        bool mirageOk = Kind != "sequel" || string.Equals(GameApp.Config?.Mod, SequelCatalog.SEQUEL_MOD, StringComparison.OrdinalIgnoreCase);
        _start.Disabled = !mirageOk || ((st == MissionState.NotYet || locked) && !s.UseDebug);
    }

    void Start()
    {
        var s = GameApp.Settings;
        var m = Missions.FirstOrDefault(x => x.Id == (Kind == "scenario" ? s.ScenarioMission : s.SequelMission));
        if (m == null) return;
        s.Save();
        Launch(m, Kind == "scenario" ? s.ScenarioTribe : "", s.Difficulty, s.UseDebug ? s.Debug.ForGame() : null);
    }

    /// <summary>the loading screen of a mission of these campaigns</summary>
    public static void Launch(SequelEntry m, string tribe, int difficulty, DebugOptions? debug, ulong seed = 0)
    {
        var start = new GameStart
        {
            Configuration = GameApp.Config!.Id, Mode = m.Kind == "scenario" ? GameMode.Scenario : GameMode.Sequel, Mission = m.Id,
            MissionFolder = m.Kind == "scenario" ? "scenarios/" + System.IO.Path.GetFileName(m.Folder) : "containment/" + System.IO.Path.GetFileName(m.Folder),
            Tribe = m.Kind == "scenario" ? (tribe == "" ? "Hu" : tribe) : "", Difficulty = Math.Clamp(difficulty, 0, 2), Debug = debug,
            Seed = seed != 0 ? seed : (ulong)DateTime.Now.Ticks,
        };
        L.Info($"start {start}");
        Boot.Instance.Show(LoadingScreen.ForSequel(start, m));
    }

    /// <summary>--sequel n / --scenario n [--tribe T] [--difficulty d] [--debug]: start without the menus (tests)</summary>
    public static void QuickStart(Dictionary<string, string> args)
    {
        string kind = args.ContainsKey("scenario") ? "scenario" : "sequel";
        int id = int.TryParse(args.GetValueOrDefault(kind, "1"), out var n) ? n : 1;
        var m = SequelCatalog.Find(kind, id);
        if (m == null) { L.Error($"--{kind} {id}: no such mission"); return; }
        if (kind == "sequel" && !string.Equals(GameApp.Config?.Mod, SequelCatalog.SEQUEL_MOD, StringComparison.OrdinalIgnoreCase))
        {
            var mc = GameApp.Configurations.FirstOrDefault(c => string.Equals(c.Mod, SequelCatalog.SEQUEL_MOD, StringComparison.OrdinalIgnoreCase));
            if (mc != null) GameApp.UseConfiguration(mc);
        }
        var dbg = GameApp.Settings.Debug.Clone();
        if (args.ContainsKey("debug")) { dbg.On = true; dbg.Free = true; }
        Launch(m, args.GetValueOrDefault("tribe", "Hu"), int.TryParse(args.GetValueOrDefault("difficulty", "1"), out var d) ? d : 1,
            args.ContainsKey("debug") || GameApp.Settings.UseDebug ? dbg.ForGame() : null, ulong.TryParse(args.GetValueOrDefault("seed", ""), out var sd) ? sd : 12345);
    }
}

/// <summary>The scenarios' menu (menu.js scenarios()): the same as Containment's with the tribe to play.</summary>
public partial class ScenarioMenu : SequelMenu
{
    public ScenarioMenu() : base("scenario", "Scenarios") { }
}

/// <summary>What a won mission opens (runtime.js unlockNext): the next Containment mission; the tribes that won a scenario.</summary>
public static class SequelProgress
{
    public static void Won(SequelSession s)
    {
        var set = GameApp.Settings;
        if (s.Kind == "scenario")
        {
            var k = s.Entry.Id.ToString();
            if (!set.ScenarioWon.TryGetValue(k, out var l)) set.ScenarioWon[k] = l = new List<string>();
            if (!l.Contains(s.Runtime.Tribe)) l.Add(s.Runtime.Tribe);
        }
        else set.SequelUnlocked = Math.Max(set.SequelUnlocked, s.Entry.Id + 1);
        try { set.Save(); } catch (Exception e) { Log.For("UI.Sequel").Warn("settings: " + e.Message); }
    }
}
