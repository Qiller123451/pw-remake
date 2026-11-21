// The campaign menu (the browser remake's Menu.campaign), two tabs (PwTabs, as the Options window):
//   * "Campaign": the original campaign of the configuration (the tutorial and missions 1 - 16: MissionGroups.Original)
//     in playing order with their texts;
//   * "Custom campaigns" (CustomCampaignsTab): every other mission grouped as campaigns - the Booster Pack, MIRAGE's own
//     missions, the remake's campaigns (Containment, Scenarios), My maps with triggers (edited copies of missions
//     marked) and the user's campaigns (campaign.lua) - docs/MENUS.md "Campaign menu", docs/CAMPAIGN.md "Custom campaigns".
// Below the tabs, for both: your tribe ("as in the mission" or any tribe the configuration lets you play - MIRAGE adds
// the SEAS), your colour (CampaignColor: the skirmish palette and RGB picker without the colours of the mission's other
// players; remembered per campaign), the difficulty, MIRAGE's game options (sixth stage, no titans, flying units: GameStart.Gameplay), the debug
// options line, Start. Missions that do not play yet are greyed out (CampaignStatus). The tab and the choice shown last
// are shown again (settings.json campaignTab, campaignMission, customSelection).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Game;

namespace PwRemake.UI;

/// <summary>One mission of missions/index.json (written by tools/prepare).</summary>
public sealed record MissionEntry(int Id, string Key, string Folder, string Title, string Description, string Tribe,
                                  bool PointBuy, int MinCredits, string Map)
{
    public static List<MissionEntry> Load(string missionsDir)
    {
        var res = new List<MissionEntry>();
        var path = Path.Combine(missionsDir, "index.json");
        var arr = Json.TryParseFile(path) as JsonArray;
        if (arr == null) { Log.For("UI.Campaign").Warn($"no mission list {path}"); return res; }
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            string title = o["title"].Str();
            if (title == "") title = o["name"].Str(o["key"].Str());
            res.Add(new MissionEntry(o["id"].Int(), o["key"].Str(), o["folder"].Str(o["key"].Str()), title,
                o["description"].Str(), o["tribe"].Str(), o["point_buy"].Bool(), o["min_credits"].Int(), o["map"].Str()));
        }
        // the remake's own missions (game/missions: Outbreak ...), the same for every configuration; their folder is a
        // token ("remake:outbreak") every machine resolves to its own copy (RemakeMissions)
        foreach (var r in PwRemake.Sim.Campaign.RemakeMissions.List())
            if (!res.Any(x => x.Id == r.Id)) res.Add(new MissionEntry(r.Id, r.Key, r.Token, r.Title, r.Description, r.Tribe, false, 0, ""));
        return res;
    }

    /// <summary>the mission's folder on this machine (a remake mission's token resolved)</summary>
    public string FolderPath(string missionsDir) => PwRemake.Sim.Campaign.RemakeMissions.FolderOf(Folder) ?? Path.Combine(missionsDir, Folder);
    /// <summary>one of the remake's own missions (game/missions)</summary>
    public bool IsRemake => PwRemake.Sim.Campaign.RemakeMissions.IsToken(Folder);
}

public partial class CampaignMenu : MenuScreen
{
    static readonly Logger L = Log.For("UI.Campaign");
    List<MissionEntry> _missions = new();
    ItemList _list = null!;
    Label _text = null!;
    OptionButton _tribe = null!, _diff = null!;
    /// <summary>"Your colour" (CampaignColor): the shown mission's other players' colours greyed out</summary>
    PlayerColorCell _color = null!;
    string _colorKey = "campaign";
    List<TakenColor> _taken = new();
    Label _debugLine = null!;
    Button _start = null!, _coop = null!;
    readonly int? _tab;
    /// <summary>the tab control (tests, screenshots: --screen custom)</summary>
    public PwTabs Tabs { get; } = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
    CustomCampaignsTab? _custom;

    /// <summary>tab: 0 Campaign, 1 Custom campaigns; null: the tab shown last</summary>
    public CampaignMenu(int? tab = null) : base("Campaign", minWidth: 960) { _tab = tab; }

    /// <summary>the menu's Start button (the custom tab sets its caption and state)</summary>
    internal Button StartButton => _start;

    protected override void Build()
    {
        var s = GameApp.Settings;
        var cfg = GameApp.Config!;
        var index = PwRemake.Sim.Campaign.MissionEntry.LoadIndex(cfg.MissionsDir);
        var original = PwRemake.Sim.Campaign.MissionGroups.Original(cfg, index, id => CampaignStatus.Hidden(cfg.Id, id)).Select(e => e.Id).ToHashSet();
        _missions = MissionEntry.Load(cfg.MissionsDir).Where(m => original.Contains(m.Id)).ToList();
        var b = Window.Body;
        b.AddChild(PwTheme.Heading($"Campaign · {cfg.Title}", 20));

        _start = PwTheme.Btn("Start mission", Start, 170);
        Tabs.CustomMinimumSize = new Vector2(880, 400);
        b.AddChild(Tabs);
        Tabs.AddTab("Campaign", "The original campaign: the tutorial and missions 1 - 16", CampaignPage);
        Tabs.AddTab("Custom campaigns", "Every other mission: the Booster Pack" + (cfg.IsMod ? $", {cfg.Mod}'s own missions" : "") +
                    ", the remake's campaigns, your maps with triggers (and edited missions) and your own campaigns", () => _custom = new CustomCampaignsTab(this));

        var opts = new HBoxContainer();
        opts.AddThemeConstantOverride("separation", 24);
        b.AddChild(opts);
        var gt = Group("Your tribe");
        opts.AddChild(gt);
        _tribe = new OptionButton { CustomMinimumSize = new Vector2(260, 28) };
        _tribe.AddItem("As in the mission", 0);
        _tribe.SetItemMetadata(0, "");
        foreach (var t in cfg.PlayableTribes)
        {
            int i = _tribe.ItemCount;
            var icon = TribeInfo.Icon(t);
            if (icon != null) _tribe.AddIconItem(icon, TribeInfo.Name(t), i); else _tribe.AddItem(TribeInfo.Name(t), i);
            _tribe.SetItemMetadata(i, t);
            if (t == s.CampaignTribe) _tribe.Select(i);
        }
        _tribe.ItemSelected += _ => { s.CampaignTribe = _tribe.GetItemMetadata(_tribe.Selected).AsString(); Refresh(); };
        gt.AddChild(_tribe);

        // your colour (docs/CAMPAIGN.md "Your colour"): the shown mission's other players' colours can't be taken
        var gc = Group("Your colour");
        opts.AddChild(gc);
        _color = new PlayerColorCell("", 220, "As in the mission") { BlockedBy = id => MissionColors.Blocker(id, _taken)?.Name };
        _color.Chosen += id => CampaignColor.Remember(_colorKey, id);
        gc.AddChild(_color);

        var gd = Group("Difficulty");
        opts.AddChild(gd);
        _diff = new OptionButton { CustomMinimumSize = new Vector2(220, 28) };
        foreach (var (n, i) in new[] { ("Easy", 0), ("Medium", 1), ("Hard", 2), ("Nightmare (bigger raids)", 3) }) _diff.AddItem(n, i);
        _diff.Select(System.Math.Clamp(s.Difficulty, 0, 3));
        _diff.ItemSelected += i => s.Difficulty = (int)i;
        gd.AddChild(_diff);

        // MIRAGE's game options (its lobby's "MaxEpoch", "RemoveTitans" and "FlyingEnabled": tech tree filters for every player,
        // Rules.GameplayOptions) - the base game has no such filters
        if (HasGameOptions(cfg))
        {
            var go = Group("Game options");
            opts.AddChild(go);
            go.AddChild(GameOptionBox("Sixth stage (final stage: titans, great cannon ...)", () => s.CampaignGameplay.EpochSix, v => s.CampaignGameplay.EpochSix = v));
            go.AddChild(GameOptionBox("No titans", () => s.CampaignGameplay.RemoveTitans, v => s.CampaignGameplay.RemoveTitans = v));
            go.AddChild(GameOptionBox(SkirmishMenu.FlyingText, () => s.CampaignGameplay.FlyingEnabled, v => s.CampaignGameplay.FlyingEnabled = v));
            go.AddChild(GameOptionBox(SkirmishMenu.PortalsText, () => s.CampaignGameplay.Portals, v => s.CampaignGameplay.Portals = v));
            go.AddChild(GameOptionBox(SkirmishMenu.LimitsText, () => s.CampaignGameplay.BuildingLimit, v => s.CampaignGameplay.BuildingLimit = v));
        }

        var dbg = new HBoxContainer();
        dbg.AddThemeConstantOverride("separation", 10);
        _debugLine = PwTheme.Para("", 13);
        _debugLine.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        dbg.AddChild(_debugLine);
        dbg.AddChild(PwTheme.Btn("Debug options …", () => Boot.Instance.Show(new DebugOptionsWindow(() => new CampaignMenu())), 170));
        b.AddChild(dbg);
        UpdateDebugLine();

        // Co-op offline: you and one or two AI commanders, each seat its own faction and base (docs/COOP.md)
        _coop = PwTheme.Btn("Co-op …", () => { s.Save(); Boot.Instance.Show(new CoopLobby(offline: true)); }, 140);
        _coop.TooltipText = "Play a mission with one or two AI commanders at your side (missions with a co-op definition)";
        b.AddChild(Row(PwTheme.Btn("Back", () => { s.Save(); Boot.Instance.Show(new TitleScreen()); }, 140), _coop, _start));

        Tabs.Changed = i => { s.CampaignTab = i; _coop.Visible = i == 0; Refresh(); };
        Tabs.Select(Math.Clamp(_tab ?? s.CampaignTab, 0, 1));
    }

    /// <summary>the "Campaign" tab: the original campaign's missions and the chosen one's texts</summary>
    Control CampaignPage()
    {
        var s = GameApp.Settings;
        var cfg = GameApp.Config!;
        var split = new HBoxContainer { CustomMinimumSize = new Vector2(840, 340) };
        split.AddThemeConstantOverride("separation", 12);
        _list = new ItemList { CustomMinimumSize = new Vector2(330, 340), SelectMode = ItemList.SelectModeEnum.Single };
        split.AddChild(_list);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(490, 340), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(scroll);
        _text = PwTheme.Para("", 14, PwTheme.Text);
        _text.CustomMinimumSize = new Vector2(470, 0);
        _text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_text);

        int sel = 0;
        for (int i = 0; i < _missions.Count; i++)
        {
            var m = _missions[i];
            var st = CampaignStatus.Of(cfg.Id, m.Id);
            int idx = _list.AddItem(m.Title);
            if (st == MissionState.NotYet) { _list.SetItemCustomFgColor(idx, PwTheme.Dim); _list.SetItemTooltip(idx, "Not adapted for the remake yet"); }
            if (m.Id == s.CampaignMission) sel = i;
        }
        _list.ItemSelected += i => ShowMission((int)i);
        if (_missions.Count == 0) _text.Text = $"No missions in {cfg.MissionsDir}. Run the prepare tool (Game data on the title screen).";
        else _list.Select(sel);
        return split;
    }

    /// <summary>the shown tab's texts and Start again (a tab or the tribe changed)</summary>
    void Refresh()
    {
        if (Tabs.Current == 1) { _start.Text = "Start mission"; _custom?.Refresh(); return; }
        _start.Text = "Start mission";
        if (_missions.Count == 0) { _start.Disabled = true; return; }
        var sel = _list.GetSelectedItems();
        ShowMission(sel.Length > 0 ? sel[0] : 0);
    }

    /// <summary>"Your colour" for a mission folder (null: no mission - the box can't be used) and the campaign it is
    /// remembered for (CampaignColor.Key): the other players' colours greyed out, the remembered colour or the mission's
    /// own when it is one of theirs</summary>
    internal void ShowColor(string? folder, string key, string? why = null)
    {
        _colorKey = key;
        _taken = folder != null ? CampaignColor.Taken(folder) : new();
        _color.SetDefaultColor(CampaignColor.MissionDefault(folder));
        _color.Id = folder != null ? MissionColors.Resolve(CampaignColor.Remembered(key), _taken) ?? "" : "";
        _color.UpdateBlocked();
        _color.Disabled = folder == null;
        _color.TooltipText = why ?? (_taken.Count > 0 ? "Taken by the mission's other players: " + string.Join(", ", _taken.Select(t => t.Name)) : "");
    }

    /// <summary>does this configuration have MIRAGE's game options (the sixth stage, no titans)?</summary>
    public static bool HasGameOptions(PwRemake.Sim.Content.GameConfiguration cfg) => string.Equals(cfg.Mod, "MIRAGE", StringComparison.OrdinalIgnoreCase);
    /// <summary>MIRAGE's game options of a mission started now (the stored choice), null for the base game</summary>
    public static PwRemake.Sim.Rules.GameplayOptions? GameplayFor(PwRemake.Sim.Content.GameConfiguration cfg) =>
        HasGameOptions(cfg) ? (GameApp.Settings.CampaignGameplay ?? new()).Clone() : null;
    static CheckBox GameOptionBox(string text, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = text, FocusMode = FocusModeEnum.None, ButtonPressed = get(),
            TooltipText = "MIRAGE's game option, for every player of the mission (MIRAGE's default: sixth stage on, titans on)" };
        c.Toggled += on => { set(on); GameApp.Settings.Save(); };
        return c;
    }

    void UpdateDebugLine()
    {
        var d = GameApp.Settings.Debug;
        var sum = d.Summary();
        _debugLine.Text = d.On ? "Debug options on: " + (sum.Count > 0 ? string.Join(" · ", sum) : "nothing changed") : "Debug options: off";
    }

    void ShowMission(int i)
    {
        if (i < 0 || i >= _missions.Count) return;
        var m = _missions[i];
        GameApp.Settings.CampaignMission = m.Id;
        ShowColor(m.FolderPath(GameApp.Config!.MissionsDir), CampaignColor.Key(m.Id, m.Folder, null));
        var (text, playable) = MissionText(m);
        _text.Text = text;
        _start.Disabled = !playable;
    }

    /// <summary>a mission of the configuration as the menus show it (title, description, the tribe you play, the army
    /// budget, its status) and whether Start may play it (a mission not adapted yet: only with the debug options)</summary>
    public static (string Text, bool Playable) MissionText(MissionEntry m)
    {
        string tribe = GameApp.Settings.CampaignTribe;
        string who = tribe == "" || tribe == m.Tribe ? $"You play the {TribeInfo.Name(m.Tribe)}." :
            $"You play the {TribeInfo.Name(tribe)} instead of the {TribeInfo.Name(m.Tribe)}: your units and buildings are the {TribeInfo.Name(tribe)}'s equivalents.";
        var st = CampaignStatus.Of(GameApp.Config!.Id, m.Id);
        string note = st switch
        {
            MissionState.NotYet => "\n\nNot adapted for the remake yet.",
            MissionState.Partial => "\n\nPlayable in parts: " + CampaignStatus.Note(GameApp.Config!.Id, m.Id),
            _ => "",
        };
        return ($"{m.Title}\n\n{m.Description}\n\n{who}{(m.PointBuy ? $"\nArmy budget: at least {m.MinCredits} points." : "")}{note}",
                st != MissionState.NotYet || GameApp.Settings.UseDebug);
    }

    void Start()
    {
        if (Tabs.Current == 1) { _custom?.Start(); return; }
        var s = GameApp.Settings;
        var m = _missions.FirstOrDefault(x => x.Id == s.CampaignMission);
        if (m == null) return;
        s.Save();
        PlayMission(m, null, () => new CampaignMenu(0));
    }

    /// <summary>a mission of the configuration started as a campaign mission with the menu's tribe, difficulty, debug
    /// options and game options (the army screen first when it has an army budget); cc: a user campaign's mission</summary>
    public static void PlayMission(MissionEntry m, PwRemake.Sim.Game.CustomCampaignRef? cc, Func<Node> back)
    {
        var s = GameApp.Settings;
        var start = new GameStart
        {
            Configuration = GameApp.Config!.Id, Mode = GameMode.Campaign, Mission = m.Id, MissionFolder = m.Folder,
            Tribe = s.CampaignTribe, Difficulty = s.Difficulty, Debug = s.Debug.ForGame(), Seed = (ulong)System.DateTime.Now.Ticks,
            Gameplay = GameplayFor(GameApp.Config!), CustomCampaign = cc,
        };
        start.Color = CampaignColor.For(m.FolderPath(GameApp.Config!.MissionsDir), CampaignColor.Key(start));     // (the campaign's remembered colour, if free)
        L.Info($"start {start}{(cc != null ? $" (campaign {cc})" : "")}");
        StartWithArmy(m, start, back);
    }

    /// <summary>--army-mission n: the army screen of campaign mission n (screenshots, tests)</summary>
    public static void ArmyFor(string id)
    {
        var m = MissionEntry.Load(GameApp.Config!.MissionsDir).FirstOrDefault(x => x.Id.ToString() == id);
        if (m == null) { Log.For("UI.Campaign").Error($"--army-mission {id}: no such mission"); return; }
        var s = GameApp.Settings;
        StartWithArmy(m, new GameStart { Configuration = GameApp.Config!.Id, Mode = GameMode.Campaign, Mission = m.Id, MissionFolder = m.Folder, Tribe = s.CampaignTribe, Difficulty = s.Difficulty, Seed = 1, Gameplay = GameplayFor(GameApp.Config!) }, () => new CampaignMenu());
    }

    /// <summary>a mission with an army budget: the army screen first (CampaignMenu.usl:199-206; menu.js campaignArmy),
    /// then the loading screen with the bought army; other missions start at once</summary>
    public static void StartWithArmy(MissionEntry m, GameStart start, Func<Node> back)
    {
        var cfg = GameApp.Config!;
        PwRemake.Sim.Campaign.CampaignArmy? offer = null;
        if (m.PointBuy)
            try
            {
                var mj = PwRemake.Sim.Maps.PwMapReader.MissionOf(m.FolderPath(cfg.MissionsDir));
                var credits = GameApp.Settings.CampaignCredits.GetValueOrDefault($"{cfg.Id}:{m.Id}");
                offer = PwRemake.Sim.Campaign.CampaignArmy.Of(mj, SkirmishMenu.RulesOf(cfg), start.Tribe, credits);
            }
            catch (Exception ex) { Log.For("UI.Campaign").Warn($"army screen of mission {m.Id}: {ex.Message}"); }
        if (offer == null) { Boot.Instance.Show(new LoadingScreen(start, m)); return; }
        Boot.Instance.Show(new ArmyScreen(new ArmyScreen.Options
        {
            Tribe = offer.Tribe, Credits = offer.Credits, Total = offer.Total, Shown = offer.Credits, Army = offer.Defaults(), Mp = false, Buildings = offer.Buildings,
            Allowed = offer.Allowed, Limits = offer.Limits, Defaults = offer.Defaults, DebugFree = true, Title = $"My Army · {m.Title}",
            Back = () => Boot.Instance.Show(back()),
            Done = army => { start.Army = army; Boot.Instance.Show(new LoadingScreen(start, m)); },
        }));
    }

    /// <summary>--mission n [--tribe T] [--difficulty d] [--debug]: start without the menus (tests, quick starts).</summary>
    public static void QuickStart(Dictionary<string, string> args)
    {
        var cfg = GameApp.Config;
        if (cfg == null) { Log.For("UI.Campaign").Error("--mission: no configuration"); return; }
        var list = MissionEntry.Load(cfg.MissionsDir);
        int id = int.TryParse(args["mission"], out var n) ? n : 0;
        var m = list.FirstOrDefault(x => x.Id == id);
        if (m == null) { Log.For("UI.Campaign").Error($"--mission {id}: no such mission in {cfg.MissionsDir}"); return; }
        var dbg = GameApp.Settings.Debug.Clone();
        if (args.ContainsKey("debug")) { dbg.On = true; dbg.Free = true; }
        var start = new GameStart
        {
            Configuration = cfg.Id, Mode = GameMode.Campaign, Mission = m.Id, MissionFolder = m.Folder,
            Tribe = args.GetValueOrDefault("tribe", ""), Difficulty = int.TryParse(args.GetValueOrDefault("difficulty", "1"), out var d) ? d : 1,
            Debug = (args.ContainsKey("debug") || GameApp.Settings.UseDebug) ? dbg.ForGame() : null,
            Seed = ulong.TryParse(args.GetValueOrDefault("seed", ""), out var seed) ? seed : 12345,
            Gameplay = GameplayFor(cfg),
            Color = args.GetValueOrDefault("color") is { Length: > 0 } col ? col : null,     // (--color red / #rrggbb: the world refuses a taken one)
        };
        Boot.Instance.Show(new LoadingScreen(start, m));
    }
}
