// The campaign menu's "Custom campaigns" tab (docs/MENUS.md "Campaign menu", docs/CAMPAIGN.md "Custom campaigns"):
// every mission that is not the original campaign, grouped as campaigns - a header per group, its missions under it:
//   * "Booster Pack": BoosterPack 1's missions 4-1 .. 4-3 (the configuration's index ids 41-43);
//   * "MIRAGE" (only in the MIRAGE configuration): MIRAGE's own missions (the SEAS versions of 4-1 .. 4-3, "Seeking Ada");
//     a mission whose map comes from a data folder the configuration does not have is left out (MissionGroups);
//   * "The remake's campaigns": Containment (1964 - 1966) and the Scenarios - Start opens their own menus (the title
//     screen's buttons stay);
//   * "My maps": your maps with triggers (the map editor's, imported .pwmap; map.lua `requires` respected - the former
//     Custom maps menu), an edited copy of a campaign mission shown with the original's title and "(edited)";
//   * your campaigns (UserCampaign: <user>/campaigns/<name>/campaign.lua): a header with the campaign and its missions in
//     order, each open when the one before was won (settings.json customCampaignWon); "New campaign …", "Edit …"
//     (name, description, author, the missions picked and ordered; Delete), "Export …" / "Import …" (.pwcampaign).
// The right side shows the chosen entry's texts; the menu's tribe, difficulty, game options and debug options apply the
// same; Start plays it - the configuration's missions as campaign missions (CampaignMenu.PlayMission: the army screen
// when they have an army budget), maps through CustomPlay (prepared as a mission folder: CustomMission.Prepare).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor.Triggers;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Sequel;
using SimEntry = PwRemake.Sim.Campaign.MissionEntry;

namespace PwRemake.UI;

public partial class CustomCampaignsTab : VBoxContainer
{
    static readonly Logger L = Log.For("UI.Custom");
    enum K { Header, Note, Mission, Map, Remake, UserHeader, UserMission }
    sealed class Item
    {
        public K Kind;
        public string Key = "", Label = "";
        public MissionEntry? Mission;
        public MyMapInfo? Map;
        public string Remake = "";
        public UserCampaign? Campaign;
        public int Index = -1;
        public CampaignMissionRef? Ref;
        public string Group = "";
    }

    readonly CampaignMenu _menu;
    readonly List<Item> _items = new();
    readonly ItemList _list;
    readonly Label _text;
    readonly Button _edit, _export;
    readonly List<SimEntry> _index;
    readonly List<MissionEntry> _ui;
    readonly List<MyMapInfo> _maps = new();

    public CustomCampaignsTab(CampaignMenu menu)
    {
        _menu = menu;
        var cfg = GameApp.Config!;
        _index = SimEntry.LoadIndex(cfg.MissionsDir);
        _ui = MissionEntry.Load(cfg.MissionsDir);
        AddThemeConstantOverride("separation", 6);
        var split = new HBoxContainer { CustomMinimumSize = new Vector2(840, 300), SizeFlagsVertical = SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 12);
        AddChild(split);
        _list = new ItemList { CustomMinimumSize = new Vector2(330, 300), SelectMode = ItemList.SelectModeEnum.Single };
        split.AddChild(_list);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(490, 300), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(scroll);
        _text = PwTheme.Para("", 14, PwTheme.Text);
        _text.CustomMinimumSize = new Vector2(470, 0);
        _text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_text);

        _edit = PwTheme.Btn("Edit …", () => { if (Selected()?.Campaign is { } c) Dialog(CampaignEditDialog(c)); }, 120);
        _edit.TooltipText = "Change the chosen campaign: name, description, its missions and their order; Delete";
        _export = PwTheme.Btn("Export …", ExportCampaign, 120);
        _export.TooltipText = "The chosen campaign as one .pwcampaign file (campaign.lua and its maps of My maps) to send to other players";
        var neu = PwTheme.Btn("New campaign …", () => Dialog(CampaignEditDialog(null)), 170);
        neu.TooltipText = "A campaign of your own: missions of this configuration and your maps, played in order";
        var imp = PwTheme.Btn("Import …", ImportCampaign, 120);
        imp.TooltipText = "A .pwcampaign file: the campaign and its maps are added to your campaigns";
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        foreach (var bt in new[] { neu, _edit, _export, imp }) row.AddChild(bt);
        AddChild(row);

        Fill();
        _list.ItemSelected += i => Show((int)i);
    }

    // ------------------------------------------------------------------------------------------- the list
    void Fill()
    {
        var cfg = GameApp.Config!;
        var s = GameApp.Settings;
        void Add(Item it)
        {
            int idx = _list.AddItem(it.Label);
            _items.Add(it);
            if (it.Kind is K.Header or K.UserHeader) _list.SetItemCustomFgColor(idx, PwTheme.Hi);
            if (it.Kind is K.Header or K.Note) _list.SetItemSelectable(idx, false);
            if (it.Kind == K.Note) _list.SetItemCustomFgColor(idx, PwTheme.Dim);
        }
        void Header(string title) => Add(new Item { Kind = K.Header, Key = "group:" + title, Label = title, Group = title });
        void Dim(string tip) { _list.SetItemCustomFgColor(_list.ItemCount - 1, PwTheme.Dim); _list.SetItemTooltip(_list.ItemCount - 1, tip); }

        // the configuration's other missions: the Booster Pack, the mod's own, other folders'
        var groups = MissionGroups.Custom(cfg, _index, id => CampaignStatus.Hidden(cfg.Id, id));
        var titles = groups.SelectMany(g => g.Missions).GroupBy(m => m.Title.Trim()).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var g in groups)
        {
            Header(g.Title);
            foreach (var e in g.Missions)
            {
                var ui = _ui.FirstOrDefault(m => m.Id == e.Id);
                if (ui == null) continue;
                // (MIRAGE's SEAS versions of 4-1 .. 4-3 have the Booster Pack's titles: the tribe tells them apart)
                var label = ui.Title.Trim() + (titles.Contains(e.Title.Trim()) && g.Kind != MissionGroupKind.BoosterPack ? $" · {TribeInfo.Name(e.Tribe)}" : "");
                Add(new Item { Kind = K.Mission, Key = "mission:" + e.Id, Label = "   " + label, Mission = ui, Group = g.Title });
                if (CampaignStatus.Of(cfg.Id, e.Id) == MissionState.NotYet) Dim("Not adapted for the remake yet");
            }
        }

        // the remake's own campaigns (their menus: game/src/UI/Sequel)
        Header("The remake's campaigns");
        Add(new Item { Kind = K.Remake, Key = "remake:sequel", Label = "   Containment (1964 - 1966)", Remake = "sequel", Group = "The remake's campaigns" });
        Add(new Item { Kind = K.Remake, Key = "remake:scenario", Label = "   Scenarios", Remake = "scenario", Group = "The remake's campaigns" });

        // the remake's own missions in the map format (game/missions: RemakeMissions - Outbreak ...): played as campaign
        // missions (solo here; co-op: the campaign menu's Co-op … and the multiplayer lobby)
        var remake = _ui.Where(m => m.IsRemake).ToList();
        if (remake.Count > 0)
        {
            Header(RemakeMissions.GroupTitle);
            foreach (var m in remake) Add(new Item { Kind = K.Mission, Key = "mission:" + m.Id, Label = "   " + m.Title.Trim(), Mission = m, Group = RemakeMissions.GroupTitle });
        }

        // My maps with triggers (not those of another configuration: map.lua requires)
        Header("My maps");
        foreach (var p in CustomMission.List(cfg))
            try { _maps.Add(MyMapInfo.Read(p, _index)); }
            catch (Exception e) { L.Warn($"custom map {p}: {e.Message}"); }
        foreach (var m in _maps)
        {
            Add(new Item { Kind = K.Map, Key = "map:" + m.Ref, Label = "   " + m.Title, Map = m, Group = "My maps" });
            _list.SetItemTooltip(_list.ItemCount - 1, m.Edited ? $"Your edited copy of the mission ({m.Ref} in My maps)" : $"{m.Ref} in My maps");
        }
        if (_maps.Count == 0) Add(new Item { Kind = K.Note, Label = "   (no maps with triggers yet)" });

        // the user's campaigns
        var camps = UserCampaign.List();
        if (camps.Count == 0) { Header("Your campaigns"); Add(new Item { Kind = K.Note, Label = "   (none yet: New campaign …)" }); }
        foreach (var c in camps)
        {
            int won = s.CustomCampaignWon.GetValueOrDefault(c.Id);
            Add(new Item { Kind = K.UserHeader, Key = "user:" + c.Id, Label = "Campaign: " + (c.Name.Length > 0 ? c.Name : c.Id), Campaign = c, Group = c.Name });
            for (int i = 0; i < c.Missions.Count; i++)
            {
                var r = c.Resolve(c.Missions[i], _index);
                var label = $"   {i + 1}. {RefTitle(r)}{(i < won ? "  ✔" : "")}";
                Add(new Item { Kind = K.UserMission, Key = $"user:{c.Id}:{i}", Label = label, Campaign = c, Index = i, Ref = r, Group = c.Name });
                if (r.Kind == CampaignRefKind.Missing) Dim("Not found: neither a map of My maps nor a mission of this configuration");
                else if (!UserCampaign.IsOpen(i, won) && !s.UseDebug) Dim("Win the mission before it first");
            }
        }

        // the entry chosen last
        int sel = _items.FindIndex(it => it.Key == s.CustomSelection && it.Kind is not (K.Header or K.Note));
        if (sel < 0) sel = _items.FindIndex(it => it.Kind is not (K.Header or K.Note));
        if (sel >= 0) { _list.Select(sel); _list.EnsureCurrentIsVisible(); }
    }

    /// <summary>the title of a campaign's mission (the configuration's mission, the map's name)</summary>
    string RefTitle(CampaignMissionRef r)
    {
        if (r.Mission != null) return (_ui.FirstOrDefault(m => m.Id == r.Mission.Id)?.Title ?? r.Mission.Title).Trim();
        if (r.MapPath != null)
            try { return MyMapInfo.Read(r.MapPath, _index).Title; }
            catch (Exception e) { L.Debug($"{r.MapPath}: {e.Message}"); }
        return r.Ref + " (not found)";
    }

    Item? Selected()
    {
        var sel = _list.GetSelectedItems();
        return sel.Length > 0 && sel[0] < _items.Count ? _items[sel[0]] : null;
    }

    /// <summary>the chosen entry's texts and Start again (the tab was shown, the tribe changed)</summary>
    public void Refresh()
    {
        var sel = _list.GetSelectedItems();
        if (sel.Length > 0) Show(sel[0]);
        else { _text.Text = "Choose a mission on the left."; _menu.StartButton.Disabled = true; _edit.Disabled = _export.Disabled = true; }
    }

    void Show(int i)
    {
        if (i < 0 || i >= _items.Count) return;
        var it = _items[i];
        var s = GameApp.Settings;
        var cfg = GameApp.Config!;
        s.CustomSelection = it.Key;
        _edit.Disabled = _export.Disabled = it.Campaign == null;
        var start = _menu.StartButton;
        start.Text = "Start mission";
        switch (it.Kind)
        {
            case K.Mission:
            {
                var (text, ok) = CampaignMenu.MissionText(it.Mission!);
                if (it.Mission!.IsRemake && RemakeMissions.ById(it.Mission.Id) is { } rm)
                    text += rm.CoopFile != null ? $"\n\nCo-op: up to {rm.MaxPlayers} players, each with a camp by one of the three ramps - the campaign tab's Co-op … (AI commanders) or Multiplayer → Campaign (co-op)." : "";
                _text.Text = $"{it.Group}\n\n{text}";
                start.Disabled = !ok;
                _menu.ShowColor(it.Mission.FolderPath(cfg.MissionsDir), CampaignColor.Key(it.Mission.Id, it.Mission.Folder, null));
                break;
            }
            case K.Map:
                _text.Text = MapText(it.Map!);
                start.Disabled = false;
                _menu.ShowColor(CustomPlay.FolderOrNull(it.Map!.Path), "maps");
                break;
            case K.Remake:
            {
                bool seq = it.Remake == "sequel";
                int n = SequelCatalog.List(it.Remake).Count;
                _text.Text = seq
                    ? $"Containment (1964 - 1966)\n\nThe remake's own campaign: the SEAS of the 1960s against the dinosaurs that come through the rifts. 1964: missions 1-11, 1966: missions 12-16 ({n} missions; open up to mission {Math.Min(s.SequelUnlocked, Math.Max(n, 1))})." +
                      $"\n\nPlayed with the {SequelCatalog.SEQUEL_MOD} configuration (its SEAS). Start opens its menu."
                    : $"Scenarios\n\nStandalone missions in ParaWorld's own world, apart from both campaigns ({n} scenarios), each played as any tribe.\n\nStart opens their menu (also on the title screen: Scenarios).";
                start.Text = "Open …";
                start.Disabled = false;
                _menu.ShowColor(null, "remake", "Containment and the scenarios give every player the colour of its story role");
                break;
            }
            case K.UserHeader:
            {
                var c = it.Campaign!;
                int won = s.CustomCampaignWon.GetValueOrDefault(c.Id);
                int cur = c.Current(won);
                var lines = c.Missions.Select((m, k) => $"{k + 1}. {RefTitle(c.Resolve(m, _index))}{(k < won ? "  (won)" : UserCampaign.IsOpen(k, won) ? "  (open)" : "")}");
                var problems = c.Problems(true, cfg, _index);
                _text.Text = $"{(c.Name.Length > 0 ? c.Name : c.Id)}{(c.Author.Length > 0 ? $"\nBy {c.Author}." : "")}\n\n{(c.Description.Length > 0 ? c.Description + "\n\n" : "")}" +
                             $"Missions, played in order (each opens when the one before is won):\n{string.Join("\n", lines)}" +
                             (problems.Count > 0 ? "\n\n" + string.Join("\n", problems) : "") +
                             (cur >= 0 ? $"\n\nStart plays mission {cur + 1}." : "") + $"\n\n{c.Folder}";
                start.Disabled = cur < 0 || c.Resolve(c.Missions[cur], _index).Kind == CampaignRefKind.Missing;
                _menu.ShowColor(cur >= 0 ? FolderOf(c.Resolve(c.Missions[cur], _index)) : null, "user:" + c.Id);
                break;
            }
            case K.UserMission:
            {
                var c = it.Campaign!;
                int won = s.CustomCampaignWon.GetValueOrDefault(c.Id);
                bool open = UserCampaign.IsOpen(it.Index, won) || s.UseDebug;
                var r = it.Ref!;
                string text; bool ok = open;
                if (r.Mission != null && _ui.FirstOrDefault(m => m.Id == r.Mission.Id) is { } ui) { var (t, p) = CampaignMenu.MissionText(ui); text = t; ok &= p; }
                else if (r.MapPath != null)
                    try { text = MapText(MyMapInfo.Read(r.MapPath, _index)); }
                    catch (Exception e) { text = $"{r.Ref}: the map cannot be read: {e.Message}"; ok = false; }
                else { text = $"\"{r.Ref}\" is neither a map of My maps nor a mission of this configuration."; ok = false; }
                _text.Text = $"{(c.Name.Length > 0 ? c.Name : c.Id)} · mission {it.Index + 1} of {c.Missions.Count}\n\n{text}" +
                             (!UserCampaign.IsOpen(it.Index, won) ? "\n\nWin the mission before it first." + (s.UseDebug ? " (open: the debug options are on)" : "") : "");
                start.Disabled = !ok;
                _menu.ShowColor(FolderOf(r), "user:" + c.Id);
                break;
            }
            default:
                _text.Text = "";
                start.Disabled = true;
                _menu.ShowColor(null, "campaign");
                break;
        }
    }

    /// <summary>the mission folder of a user campaign's mission (the configuration's mission, a map of My maps prepared
    /// as one), null when there is none</summary>
    string? FolderOf(CampaignMissionRef r)
    {
        if (r.Mission != null) return _ui.FirstOrDefault(m => m.Id == r.Mission.Id)?.FolderPath(GameApp.Config!.MissionsDir);
        return r.MapPath != null ? CustomPlay.FolderOrNull(r.MapPath) : null;
    }

    static string MapText(MyMapInfo m)
    {
        string size = "";
        try
        {
            var h = SkirmishMaps.Header(m.Path);
            var mp = MapSource.MpSettingsOf(h);
            size = $"{h.Hx * 2} x {h.Hy * 2} m, {h.SettingName}, {(int)Math.Max(1, mp.MaxPlayers)} players.\n\n";
        }
        catch (Exception e) { L.Debug($"{m.Path}: {e.Message}"); }
        var edited = m.Edited ? $"Your edited copy of {(m.Original?.Title.Trim() is { Length: > 0 } t ? $"\"{t}\"" : "a campaign mission")} (saved by the map editor in My maps; the campaign plays the original).\n\n" : "";
        var desc = m.Description.Length > 0 ? m.Description : m.Edited ? m.MissionDescription : "";
        return $"{m.Title}\n\n{edited}{(desc.Length > 0 ? desc + "\n\n" : "")}{(m.Author.Length > 0 ? $"By {m.Author}.\n" : "")}{size}" +
               "Played as a mission: you on the first player slot a human may play, the computer on the others (or the map's own players); its triggers run." +
               $"\n\n{m.Path}";
    }

    // ------------------------------------------------------------------------------------------- Start
    public void Start()
    {
        var it = Selected();
        if (it == null) return;
        var s = GameApp.Settings;
        s.Save();
        switch (it.Kind)
        {
            case K.Mission: CampaignMenu.PlayMission(it.Mission!, null, () => new CampaignMenu(1)); break;
            case K.Map: CustomPlay.Play(it.Map!.Path, it.Map.Title); break;
            case K.Remake: Boot.Instance.Show(it.Remake == "sequel" ? new Sequel.SequelMenu(() => new CampaignMenu(1)) : new Sequel.ScenarioMenu(() => new CampaignMenu(1))); break;
            case K.UserHeader: CustomPlay.PlayUser(it.Campaign!, it.Campaign!.Current(s.CustomCampaignWon.GetValueOrDefault(it.Campaign.Id))); break;
            case K.UserMission: CustomPlay.PlayUser(it.Campaign!, it.Index); break;
        }
    }

    // ------------------------------------------------------------------------------------------- campaigns: dialogs
    void Dialog(MenuDialog d) => _menu.AddChild(d);
    void Message(string caption, string text) => Dialog(MenuDialog.Message(caption, text));

    void ExportCampaign()
    {
        if (Selected()?.Campaign is not { } c) return;
        var fd = FileDialog(true, c.Id + UserCampaign.Ext, file =>
        {
            try
            {
                if (!file.EndsWith(UserCampaign.Ext, StringComparison.OrdinalIgnoreCase)) file += UserCampaign.Ext;
                var packed = c.Export(file, _index);
                int missions = c.Missions.Count - c.Missions.Count(m => packed.Contains(m, StringComparer.OrdinalIgnoreCase));
                Message("Export campaign", $"\"{c.Name}\" was exported to {file}:\ncampaign.lua and {packed.Count} map(s) of My maps." +
                        (missions > 0 ? $"\n{missions} mission(s) of the configuration are not packed (every installation has them)." : ""));
            }
            catch (Exception e) { L.Error($"export of the campaign {c.Folder}", e); Message("Export campaign", "The campaign could not be exported: " + e.Message); }
        });
        _menu.AddChild(fd);
        fd.PopupCentered();
    }

    void ImportCampaign()
    {
        var fd = FileDialog(false, "", file =>
        {
            try
            {
                var c = UserCampaign.Import(file);
                GameApp.Settings.CustomSelection = "user:" + c.Id;
                GameApp.Settings.Save();
                Boot.Instance.Show(new CampaignMenu(1));
            }
            catch (Exception e) { L.Error($"import of the campaign {file}", e); Message("Import campaign", "The campaign could not be imported: " + e.Message); }
        });
        _menu.AddChild(fd);
        fd.PopupCentered();
    }

    static FileDialog FileDialog(bool save, string file, Action<string> ok)
    {
        var fd = new FileDialog
        {
            FileMode = save ? Godot.FileDialog.FileModeEnum.SaveFile : Godot.FileDialog.FileModeEnum.OpenFile, Access = Godot.FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.pwcampaign ; ParaWorld Remake campaign" }, Title = save ? "Export the campaign as .pwcampaign" : "Import a .pwcampaign campaign",
            CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), CurrentFile = file, UseNativeDialog = true, Size = new Vector2I(900, 600),
        };
        fd.FileSelected += p => { fd.QueueFree(); ok(p); };
        fd.Canceled += fd.QueueFree;
        return fd;
    }

    /// <summary>"New campaign" (c null) / "Edit": name, author, description, the missions picked from the configuration's
    /// missions and My maps and put in order; Save writes campaign.lua, Delete removes the campaign's folder</summary>
    MenuDialog CampaignEditDialog(UserCampaign? c)
    {
        var cfg = GameApp.Config!;
        var d = new MenuDialog(c == null ? "New campaign" : "Edit campaign", 860);
        var b = d.Window.Body;
        var name = new LineEdit { Text = c?.Name ?? "My campaign", PlaceholderText = "the campaign's name" };
        var author = new LineEdit { Text = c?.Author ?? "", PlaceholderText = "you (optional)" };
        var desc = new TextEdit { Text = c?.Description ?? "", PlaceholderText = "what the campaign is about (optional)", CustomMinimumSize = new Vector2(0, 60), WrapMode = TextEdit.LineWrappingMode.Boundary };
        b.AddChild(MenuDialog.Row("Name", name));
        b.AddChild(MenuDialog.Row("Author", author));
        b.AddChild(MenuDialog.Row("Description", desc));

        // what can be picked: the configuration's missions (original, then the other groups), then My maps with triggers
        var avail = new List<(string Ref, string Label)>();
        var hidden = new Func<int, bool>(id => CampaignStatus.Hidden(cfg.Id, id));
        foreach (var e in MissionGroups.Original(cfg, _index, hidden)) avail.Add((e.Key, (_ui.FirstOrDefault(m => m.Id == e.Id)?.Title ?? e.Title).Trim()));
        foreach (var g in MissionGroups.Custom(cfg, _index, hidden))
            foreach (var e in g.Missions) avail.Add((e.Key, $"{g.Title}: {(_ui.FirstOrDefault(m => m.Id == e.Id)?.Title ?? e.Title).Trim()}{(e.Tribe == "SEAS" ? " · SEAS" : "")}"));
        foreach (var m in _maps) avail.Add((m.Ref, "My maps: " + m.Title));

        var chosen = new List<string>(c?.Missions ?? new List<string>());
        var lists = new HBoxContainer();
        lists.AddThemeConstantOverride("separation", 10);
        b.AddChild(lists);
        VBoxContainer Col(string caption)
        {
            var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var l = new Label { Text = caption };
            l.AddThemeColorOverride("font_color", PwTheme.Hi);
            v.AddChild(l);
            lists.AddChild(v);
            return v;
        }
        var left = Col("Missions and maps");
        var availList = new ItemList { CustomMinimumSize = new Vector2(340, 260), SelectMode = ItemList.SelectModeEnum.Single };
        foreach (var (_, label) in avail) availList.AddItem(label);
        left.AddChild(availList);
        var mid = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        mid.AddThemeConstantOverride("separation", 6);
        lists.AddChild(mid);
        var right = Col("The campaign, in playing order");
        var campList = new ItemList { CustomMinimumSize = new Vector2(340, 260), SelectMode = ItemList.SelectModeEnum.Single };
        right.AddChild(campList);
        string LabelOf(string r) => avail.FirstOrDefault(a => string.Equals(a.Ref, r, StringComparison.OrdinalIgnoreCase)).Label ?? r + " (not found)";
        void Redraw(int sel)
        {
            campList.Clear();
            for (int i = 0; i < chosen.Count; i++) campList.AddItem($"{i + 1}. {LabelOf(chosen[i])}");
            if (sel >= 0 && sel < chosen.Count) campList.Select(sel);
        }
        int CampSel() => campList.GetSelectedItems() is { Length: > 0 } s ? s[0] : -1;
        void AddChosen()
        {
            if (availList.GetSelectedItems() is not { Length: > 0 } s) return;
            int at = CampSel() >= 0 ? CampSel() + 1 : chosen.Count;
            chosen.Insert(at, avail[s[0]].Ref);
            Redraw(at);
        }
        availList.ItemActivated += _ => AddChosen();
        mid.AddChild(PwTheme.Btn("Add →", AddChosen, 110));
        mid.AddChild(PwTheme.Btn("← Remove", () => { int i = CampSel(); if (i < 0) return; chosen.RemoveAt(i); Redraw(Math.Min(i, chosen.Count - 1)); }, 110));
        mid.AddChild(PwTheme.Btn("Up", () => { int i = CampSel(); if (i <= 0) return; (chosen[i - 1], chosen[i]) = (chosen[i], chosen[i - 1]); Redraw(i - 1); }, 110));
        mid.AddChild(PwTheme.Btn("Down", () => { int i = CampSel(); if (i < 0 || i + 1 >= chosen.Count) return; (chosen[i + 1], chosen[i]) = (chosen[i], chosen[i + 1]); Redraw(i + 1); }, 110));
        Redraw(-1);

        var problem = PwTheme.Para("", 13, PwTheme.Lost);
        b.AddChild(problem);
        void Save()
        {
            var nc = c ?? new UserCampaign();
            var before = (nc.Name, nc.Description, nc.Author, nc.Missions);
            nc.Name = name.Text.Trim(); nc.Description = desc.Text.Trim(); nc.Author = author.Text.Trim(); nc.Missions = chosen.ToList();
            var p = nc.Problems(false);
            if (p.Count > 0) { (nc.Name, nc.Description, nc.Author, nc.Missions) = before; problem.Text = string.Join(" ", p); return; }
            try
            {
                nc.Save();
                // (a changed order or list: the missions won so far stay won as far as they are the same missions)
                d.Close();
                GameApp.Settings.CustomSelection = "user:" + nc.Id;
                GameApp.Settings.Save();
                Boot.Instance.Show(new CampaignMenu(1));
            }
            catch (Exception e) { L.Error($"campaign {nc.Name}", e); problem.Text = "The campaign could not be saved: " + e.Message; }
        }
        var buttons = new List<Button> { PwTheme.Btn("Save", Save, 140) };
        if (c != null)
            buttons.Add(PwTheme.Btn("Delete …", () => Dialog(MenuDialog.Confirm("Delete campaign", $"Delete the campaign \"{c.Name}\" ({c.Folder})? Its maps in My maps stay; maps it brought with it (an imported campaign's) are deleted with it.", "Delete", () =>
            {
                try { c.Delete(); GameApp.Settings.CustomCampaignWon.Remove(c.Id); GameApp.Settings.Save(); }
                catch (Exception e) { L.Error($"delete campaign {c.Folder}", e); }
                d.Close();
                Boot.Instance.Show(new CampaignMenu(1));
            })), 140));
        buttons.Add(PwTheme.Btn("Cancel", d.Close, 120));
        b.AddChild(MenuDialog.Buttons(buttons.ToArray()));
        return d;
    }
}

/// <summary>A window over a menu (the darkened menu behind it): the custom campaign dialogs, messages, questions.</summary>
public partial class MenuDialog : Control
{
    public PwWindow Window { get; }

    public MenuDialog(string caption, float width)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = PwTheme.Theme;
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.55f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(center);
        Window = new PwWindow(caption, width, Math.Max(width, 760));
        center.AddChild(Window);
    }

    public void Close() => QueueFree();

    public static HBoxContainer Row(string label, Control c, float labelWidth = 120)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        h.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(labelWidth, 0) });
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(c);
        return h;
    }

    public static HBoxContainer Buttons(params Button[] bs)
    {
        var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        h.AddThemeConstantOverride("separation", 12);
        foreach (var b in bs) h.AddChild(b);
        return h;
    }

    public static MenuDialog Message(string caption, string text)
    {
        var d = new MenuDialog(caption, 480);
        d.Window.Body.AddChild(PwTheme.Para(text, 14, PwTheme.Text));
        d.Window.Body.AddChild(Buttons(PwTheme.Btn("OK", d.Close, 120)));
        return d;
    }

    public static MenuDialog Confirm(string caption, string text, string yes, Action go)
    {
        var d = new MenuDialog(caption, 480);
        d.Window.Body.AddChild(PwTheme.Para(text, 14, PwTheme.Text));
        d.Window.Body.AddChild(Buttons(PwTheme.Btn(yes, () => { d.Close(); go(); }, 140), PwTheme.Btn("Cancel", d.Close, 120)));
        return d;
    }
}

/// <summary>Playing the custom tab's maps and the user campaigns' missions (was CustomMapsMenu.Play): a map of My maps
/// with triggers is prepared as a mission folder once per content (CustomMission.Prepare: the user's cache folder), then
/// started as a campaign mission (GameMode.Campaign, MissionFolder = that folder, id 98) with the campaign menu's
/// tribe, difficulty, debug options and game options; its end screen comes back to the custom tab, Retry plays it again,
/// saved games of it load while the prepared folder is there. A user campaign's win opens its next mission
/// (settings.json customCampaignWon), the end screen's Continue plays it.</summary>
public static class CustomPlay
{
    static readonly Logger L = Log.For("UI.Custom");

    /// <summary>the map prepared as a mission folder (made once per content: CustomMission.Prepare); throws</summary>
    public static string Prepare(string mapPath)
    {
        var cfg = GameApp.Config!;
        var rules = SkirmishMenu.RulesOf(cfg);
        return CustomMission.Prepare(mapPath, AppPaths.CacheDir, cfg.PlayableTribes.ToList(),
            t => rules.StartOf(t)?.Units is { Count: > 0 } u ? u : new List<string> { t.ToLowerInvariant() + "_worker" });
    }

    /// <summary>the map's mission folder for the menu's "Your colour" (its players' colours), null when it cannot be made</summary>
    public static string? FolderOrNull(string mapPath)
    {
        try { return Prepare(mapPath); }
        catch (Exception e) { L.Debug($"{mapPath}: {e.Message}"); return null; }
    }

    /// <summary>prepares the map as a mission and starts it; cc: a user campaign's mission</summary>
    public static void Play(string mapPath, string? name = null, CustomCampaignRef? cc = null)
    {
        var cfg = GameApp.Config!;
        string folder;
        try { folder = Prepare(mapPath); }
        catch (Exception e)
        {
            L.Error($"the custom map could not be prepared: {mapPath}", e);
            Boot.Instance.Show(new CampaignMenu(1));
            return;
        }
        var s = GameApp.Settings;
        var start = new GameStart
        {
            Configuration = cfg.Id, Mode = GameMode.Campaign, Mission = CustomMission.Id, MissionFolder = folder, Tribe = s.CampaignTribe, Difficulty = s.Difficulty,
            Debug = s.UseDebug ? s.Debug.ForGame() : null, Seed = (ulong)DateTime.Now.Ticks, Gameplay = CampaignMenu.GameplayFor(cfg), CustomCampaign = cc,
        };
        start.Color = CampaignColor.For(folder, CampaignColor.Key(start));       // ("Your colour" of My maps / the user campaign, if free)
        L.Info($"custom map {mapPath}: {start}{(cc != null ? $" (campaign {cc})" : "")}");
        Boot.Instance.Show(new LoadingScreen(start, EntryOf(start, name)));
    }

    /// <summary>the mission entry of a custom map's start (the loading screen, saved games)</summary>
    public static MissionEntry EntryOf(GameStart start, string? name = null)
    {
        string title = name ?? "Custom map";
        if (name == null)
            try { title = PwMapReader.ReadHeader(start.MissionFolder).Name; } catch (Exception) { }
        return new MissionEntry(start.Mission, CustomMission.Key, start.MissionFolder, title, "Custom map (your map with triggers)", "", false, 0, "");
    }

    /// <summary>mission i of a user campaign: the configuration's mission as a campaign mission, a map as a custom map</summary>
    public static void PlayUser(UserCampaign c, int i)
    {
        var cfg = GameApp.Config!;
        if (i < 0 || i >= c.Missions.Count) { Boot.Instance.Show(new CampaignMenu(1)); return; }
        var index = PwRemake.Sim.Campaign.MissionEntry.LoadIndex(cfg.MissionsDir);
        var r = c.Resolve(c.Missions[i], index);
        var cc = new CustomCampaignRef { Campaign = c.Id, Index = i };
        GameApp.Settings.CustomSelection = $"user:{c.Id}:{i}";
        if (r.Mission != null && MissionEntry.Load(cfg.MissionsDir).FirstOrDefault(m => m.Id == r.Mission.Id) is { } ui)
            CampaignMenu.PlayMission(ui, cc, () => new CampaignMenu(1));
        else if (r.MapPath != null)
        {
            string? title = null;
            try { title = MyMapInfo.Read(r.MapPath, index).Title; } catch (Exception) { }
            Play(r.MapPath, title, cc);
        }
        else { L.Error($"campaign {c.Id}: mission {i + 1} \"{c.Missions[i]}\" not found"); Boot.Instance.Show(new CampaignMenu(1)); }
    }

    /// <summary>a mission of a user campaign was won: the next one opens</summary>
    public static void Won(GameStart start)
    {
        if (start.CustomCampaign is not { } cc) return;
        var w = GameApp.Settings.CustomCampaignWon;
        w[cc.Campaign] = UserCampaign.AfterWin(cc.Index, w.GetValueOrDefault(cc.Campaign));
        L.Info($"campaign {cc.Campaign}: mission {cc.Index + 1} won ({w[cc.Campaign]} won)");
        GameApp.Settings.Save();
    }

    /// <summary>the end screen's Continue of a user campaign's mission: the next mission (after a win), else the custom tab</summary>
    public static void Continue(GameStart start, bool won)
    {
        var cc = start.CustomCampaign;
        var c = cc == null || UserCampaign.Dir == null ? null : UserCampaign.List().FirstOrDefault(x => x.Id == cc.Campaign);
        if (c != null && won && cc!.Index + 1 < c.Missions.Count) { PlayUser(c, cc.Index + 1); return; }
        if (c != null) GameApp.Settings.CustomSelection = "user:" + c.Id;
        Boot.Instance.Show(new CampaignMenu(1));
    }
}
