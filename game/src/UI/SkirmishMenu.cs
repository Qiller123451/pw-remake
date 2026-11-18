// The skirmish setup screen (the original's pre-game window in skirmish mode: PlayerListWindow with its Players and
// Settings pages, LobbySettingsPage, PlayerInfoSlot; docs/spec/ui_groups7_9.md §10): the map and the game options on
// top, the player slots below (the remake's sixteen, in a scroll box) - tribe, army (the human's army budget preset or
// "My Army", a computer player's personality), AI level, team, colour (the palette or any RGB colour: PlayerColorCell),
// handicap, headquarters position. The setup is the plain JSON object of
// src/Sim/Game/Skirmish.cs (a lobby fills the same object later), kept per configuration in the settings
// (settings.json "skirmish") and started as GameStart { Mode = Skirmish, Skirmish = setup } through the loading screen.
//
// Ported from remake/src/ui/skirmish.js (pw-exporter 98c7703); the map list from the installation (SkirmishMaps)
// instead of the toolkit's maps/index.json, the preview: MapPreview (ui/mappreview.js).
//
// The network mode (SkirmishMenu(network: true), docs/MULTIPLAYER.md) is the lobby of a PvP game - the original's
// PlayerListWindow in multiplayer: the setup is the host's (NetSession: every change goes to the players), a slot can
// be Open (waiting for a human to join), a human's slot shows its name and GO (ready); a player changes only the tribe,
// team, colour, army preset and start position of its own slot; the lobby's chat below; the host's Start Game when
// everybody is ready, a player's Ready; Leave Game / Cancel Game.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Net;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using SimColors = PwRemake.Sim.Rules.Colors;

namespace PwRemake.UI;

public partial class SkirmishMenu : MenuScreen
{
    static readonly Logger L = Log.For("UI.Skirmish");
    static readonly Dictionary<double, string> POINT_NAMES = new() { [-1] = "Predefined", [1000] = "1000", [3000] = "3000", [10000] = "10000" };
    static List<SkirmishMap>? s_maps;
    static string? s_mapsOf;

    SkirmishSetup _setup = null!;
    PointBuy? _pb;
    List<SkirmishMap> _maps = new();
    MpSettings _mp = new() { MaxPlayers = MapGen.MAX_PLAYERS, GameTypes = new() { "Deathmatch" } };
    string? _mapError;

    OptionButton _map = null!, _mode = null!, _defMin = null!, _teams = null!, _points = null!;
    CheckBox _warp = null!, _fog = null!, _lock = null!, _hcap = null!, _hq = null!, _epoch6 = null!, _titans = null!, _flying = null!, _portals = null!, _limits = null!;
    /// <summary>MIRAGE's "FlyingEnabled" (the label of the skirmish, campaign and co-op menus)</summary>
    public const string FlyingText = "Flying units (pteranodons, cargolifter, dirigible, helicopter, jetpack)";
    /// <summary>MIRAGE's "Portals"</summary>
    public const string PortalsText = "Portal termini (units travel between two of them; not with the warp gate)";
    /// <summary>MIRAGE's "BldgMaxCapacity" (World.BuildingLimits)</summary>
    public const string LimitsText = "Building limit (at most the tech tree's number of each building: towers, temples, walls ...)";
    SpinBox _seed = null!;
    Control _seedRow = null!;
    Label _desc = null!, _err = null!, _debugLine = null!;
    MapPreview _preview = null!;
    GridContainer _slots = null!;
    ScrollContainer _slotScroll = null!;
    /// <summary>a slot's colour picker is open: lobby updates wait (rebuilding the rows would close it)</summary>
    readonly List<PlayerColorCell> _colorCells = new();
    bool PickerOpen => _colorCells.Exists(c => c.Open);
    Button _start = null!;
    readonly Dictionary<string, Label> _heads = new();
    bool _filling;

    // ---- the network mode (a PvP lobby)
    readonly bool _net;
    static NetSession? N => NetSession.Current;
    bool IsHost => !_net || N?.Hosting == true;
    string MyName => N?.Client.Name ?? "";
    LobbyState? _lobbyShown;
    bool _noSave;
    string _sent = "";
    Button? _ready;
    Label? _netInfo;

    public SkirmishMenu() : this(false) { }
    public SkirmishMenu(bool network) : base(network ? "Multiplayer" : "Skirmish", minWidth: 1180) { _net = network; }

    /// <summary>the lobby a host offers first: the stored setup with slot 0 and 1 open for humans (the host takes the
    /// first: NetHost.AssignSlots)</summary>
    public static LobbyState InitialLobby(string hostName)
    {
        var s = StoredSetup();
        s.Slots[0].Type = "open"; s.Slots[0].Owner = null;
        if (s.Slots[1].Type != "ai") { s.Slots[1].Type = "open"; }
        else { s.Slots[1].Type = "open"; }
        return new LobbyState { Mode = "skirmish", Name = $"{hostName}'s game", Skirmish = s, SceneRule = GameApp.Settings.MpSceneRule };
    }

    /// <summary>the setup of the configuration as the settings keep it (filled up to the current form)</summary>
    public static SkirmishSetup StoredSetup()
    {
        var cfg = GameApp.Config!;
        var s = Skirmish.LoadSetup(GameApp.Settings.Skirmish.GetValueOrDefault(cfg.Id));
        // the configuration's tribes only (the SEAS: MIRAGE)
        foreach (var sl in s.Slots) if (sl.Tribe != "Random" && !cfg.PlayableTribes.Contains(sl.Tribe)) sl.Tribe = sl.Type == "human" ? cfg.PlayableTribes.FirstOrDefault() ?? "Hu" : "Random";
        return s;
    }
    static PwRemake.Sim.Rules.Rules? s_rules;
    static string? s_rulesOf;
    /// <summary>the rules of a configuration for the menus (the army budget's prices, texts), read once</summary>
    public static PwRemake.Sim.Rules.Rules RulesOf(PwRemake.Sim.Content.GameConfiguration cfg)
    {
        if (s_rules == null || s_rulesOf != cfg.Data) { s_rules = PwRemake.Sim.Rules.Rules.Load(cfg.Data); s_rulesOf = cfg.Data; }
        return s_rules;
    }
    void Save()
    {
        if (!_net) { GameApp.Settings.Skirmish[GameApp.Config!.Id] = _setup.Clone(); GameApp.Settings.Save(); return; }
        if (_noSave || N?.Client.Lobby is not { } lobby) return;
        if (IsHost)
        {
            // the host: the whole setup to the players (only when it changed - the echo of the host's own change comes back)
            var s = GameApp.Settings;
            _setup.Debug = s.UseDebug ? s.Debug.ForGame() : null;
            var l = lobby.Clone();
            l.Skirmish = _setup.Clone();
            var json = NetProtocol.ToNode(l.Skirmish)!.ToJsonString();
            if (json == _sent) return;
            _sent = json;
            N.Client.HostSetLobby(l);
            return;
        }
        // a player: its own slot
        var mine = _setup.Slots.FirstOrDefault(x => x.Type == "human" && x.Owner == MyName);
        if (mine == null) return;
        var f = new System.Text.Json.Nodes.JsonObject { ["tribe"] = mine.Tribe, ["team"] = mine.Team, ["color"] = mine.Color, ["hq"] = mine.Hq, ["preset"] = mine.Preset ?? "standard" };
        var fj = f.ToJsonString();
        if (fj == _sent) return;
        _sent = fj;
        N.Client.SetSlot(f);
    }

    /// <summary>may this machine change slot i (single player, the host: every slot; a player: its own)</summary>
    bool CanEdit(int i) => IsHost || (_setup.Slots[i].Type == "human" && _setup.Slots[i].Owner == MyName);

    protected override void Build()
    {
        var cfg = GameApp.Config!;
        _setup = _net && N?.Client.Lobby?.Skirmish is { } ls ? ls.Clone() : StoredSetup();
        _lobbyShown = N?.Client.Lobby;
        try { _pb = new PointBuy(RulesOf(cfg), null); }
        catch (Exception ex) { L.Warn("the army budget's price lists: " + ex.Message); }
        if (s_maps == null || s_mapsOf != cfg.Id)
        {
            try { s_maps = SkirmishMaps.List(cfg); s_mapsOf = cfg.Id; }
            catch (Exception ex) { L.Error("the map list", ex); s_maps = new(); }
            L.Info($"{s_maps.Count} skirmish maps in {cfg.Install}: {string.Join(", ", s_maps.Select(m => m.Path))}");
        }
        _maps = s_maps;
        var b = Window.Body;
        b.AddChild(PwTheme.Heading(_net ? $"Multiplayer · Skirmish · {cfg.Title}" : $"Skirmish · {cfg.Title}", 20));
        if (_net)
        {
            _netInfo = PwTheme.Para("", 13, PwTheme.Dim, HorizontalAlignment.Center);
            b.AddChild(_netInfo);
        }

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 24);
        b.AddChild(top);
        // ---- the map
        var gm = Group("Map");
        top.AddChild(gm);
        var mapRow = new HBoxContainer();
        mapRow.AddThemeConstantOverride("separation", 12);
        gm.AddChild(mapRow);
        _preview = new MapPreview(200);
        mapRow.AddChild(_preview);
        var info = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        info.AddThemeConstantOverride("separation", 6);
        mapRow.AddChild(info);
        _map = new OptionButton { CustomMinimumSize = new Vector2(330, 28), FitToLongestItem = false };
        _map.ItemSelected += i => { if (_filling) return; _setup.Map = _map.GetItemMetadata((int)i).AsString(); _setup.MapName = MapOf(_setup.Map)?.Name; ShowMap(); Refresh(); };
        info.AddChild(_map);
        _seed = new SpinBox { MinValue = 1, MaxValue = 999999, Value = Math.Max(1, _setup.Seed), CustomMinimumSize = new Vector2(120, 0) };
        _seed.ValueChanged += v => { _setup.Seed = (ulong)v; Save(); };
        _seedRow = Row(new Label { Text = "Seed" }, _seed);
        ((HBoxContainer)_seedRow).Alignment = BoxContainer.AlignmentMode.Begin;
        info.AddChild(_seedRow);
        _desc = PwTheme.Para("", 13, PwTheme.Text);
        _desc.CustomMinimumSize = new Vector2(330, 0);
        info.AddChild(_desc);

        // ---- the settings
        var gs = Group("Settings");
        top.AddChild(gs);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 4);
        gs.AddChild(grid);
        _mode = Opt(Skirmish.MODES.Select(m => (m.Name, m.Id)), 180);
        _mode.ItemSelected += i => { if (_filling) return; Skirmish.ApplyMode(_setup, Meta(_mode)); FillMaps(); Refresh(); };
        _defMin = Opt(Enumerable.Range(1, 60).Select(v => ((v * 5).ToString(), (v * 5).ToString())), 180);
        _defMin.ItemSelected += _ => { if (_filling) return; _setup.DefenderMin = double.Parse(Meta(_defMin)); Refresh(); };
        _teams = Opt(Array.Empty<(string, string)>(), 180);
        _teams.ItemSelected += _ => { if (_filling) return; _setup.Teams = int.Parse(Meta(_teams)); Refresh(); };
        _points = Opt(Skirmish.POINTS.Select(p => (POINT_NAMES[p], p.ToString(System.Globalization.CultureInfo.InvariantCulture))), 180);
        _points.ItemSelected += _ => { if (_filling) return; _setup.Points = double.Parse(Meta(_points), System.Globalization.CultureInfo.InvariantCulture); Refresh(); };
        foreach (var (l, c) in new (string, Control)[] { ("Game type", _mode), ("Defender countdown (minutes)", _defMin), ("Teams", _teams), ("Points", _points) })
        {
            grid.AddChild(new Label { Text = l });
            grid.AddChild(c);
        }
        CheckBox Chk(string text, Action<bool> set)
        {
            var c = new CheckBox { Text = text, FocusMode = FocusModeEnum.None };
            c.Toggled += on => { if (_filling) return; set(on); Refresh(); };
            gs.AddChild(c);
            return c;
        }
        _warp = Chk("Warp gate victory (epoch V: hold a finished warp gate for 10 minutes)", on =>
        {
            _setup.Warpgate = on;
            // (MIRAGE's portals only without the warp gate)
            if (_portals != null) { _portals.Disabled = on; _portals.Modulate = on ? new Color(1, 1, 1, 0.5f) : Colors.White; }
        });
        _fog = Chk("Disable fog of war", on => _setup.Fog = !on);
        _lock = Chk("Lock diplomacy settings", on => _setup.LockDiplomacy = on);
        // unchecking the handicap resets every slot to 100 % (LSP:611-630), unchecking "choose HQ" numbers them 1.. (LSP:567-588)
        _hcap = Chk("Allow player handicap", on => { _setup.Handicap = on; if (!on) foreach (var sl in _setup.Slots) sl.Hp = 100; });
        _hq = Chk("Choose headquarters position", on => { _setup.ChooseHQ = on; if (!on) for (int i = 0; i < _setup.Slots.Count; i++) _setup.Slots[i].Hq = i; });
        // MIRAGE's game options that are tech tree filters (GameplayOptions; StartLocation.usl MirageInitialized)
        _epoch6 = Chk("Sixth stage (final stage: titans, great cannon ...)", on => _setup.EpochSix = on);
        _titans = Chk("No titans", on => _setup.RemoveTitans = on);
        _flying = Chk(FlyingText, on => _setup.FlyingEnabled = on);
        _portals = Chk(PortalsText, on => _setup.Portals = on);
        _limits = Chk(LimitsText, on => _setup.BuildingLimit = on);
        var dbg = new HBoxContainer();
        dbg.AddThemeConstantOverride("separation", 10);
        _debugLine = PwTheme.Para("", 12);
        _debugLine.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _debugLine.CustomMinimumSize = new Vector2(260, 0);
        dbg.AddChild(_debugLine);
        var dbgBtn = PwTheme.Btn("Debug options …", () => { Save(); Boot.Instance.Show(new DebugOptionsWindow(() => new SkirmishMenu(_net))); }, 170);
        dbgBtn.Disabled = !IsHost;
        dbg.AddChild(dbgBtn);
        gs.AddChild(dbg);
        var d = GameApp.Settings.Debug; var sum = d.Summary();
        _debugLine.Text = GameApp.Settings.UseDebug && d.On ? "Debug options on: " + (sum.Count > 0 ? string.Join(" · ", sum) : "nothing changed") : "Debug options: off";

        // ---- the players
        var gp = Group("Players");
        b.AddChild(gp);
        _slots = new GridContainer { Columns = 8 };
        _slots.AddThemeConstantOverride("h_separation", 10);
        _slots.AddThemeConstantOverride("v_separation", 4);
        // (16 slots: a scroll box as high as the screen allows - less in the network lobby, its chat below)
        int rows = Math.Clamp((int)((GetViewportRect().Size.Y - (_net ? 700 : 540)) / 30), 5, Skirmish.MAX_SLOTS);
        _slotScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, rows * 30 + 24), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _slotScroll.AddChild(_slots);
        gp.AddChild(_slotScroll);
        _err = PwTheme.Para("", 14, PwTheme.Lost);
        b.AddChild(_err);
        var tx = GameTexts.For(cfg);
        if (_net && N != null)
        {
            // the lobby's chat (the original's PlayerListWindow: "Chat")
            var gc = Group(tx.Get("_MultiPlayerSelect_Chat", "Chat"));
            gc.AddChild(new LobbyChat(N.Client) { CustomMinimumSize = new Vector2(0, 150) });
            b.AddChild(gc);
        }
        if (!_net)
        {
            _start = PwTheme.Btn("Start game", StartGame, 170);
            b.AddChild(Row(PwTheme.Btn("Back", () => { Save(); Boot.Instance.Show(new TitleScreen()); }, 140), _start));
        }
        else
        {
            _start = PwTheme.Btn(tx.Get("_UI_PlLiWind_Button_StartGame", "Start Game"), StartGame, 170);
            _start.TooltipText = tx.Get("_UI_PlLiWind_Button_StartGame_TT", "");
            _start.Visible = IsHost;
            _ready = PwTheme.Btn(tx.Get("_ActionView_ButtonReady", "Ready"), () => N?.Client.SetReady(!(N.Client.MyPeer?.Ready ?? false)), 170);
            _ready.TooltipText = tx.Get("_UI_PlLiWind_Row_GO_TT", "");
            _ready.Visible = !IsHost;
            var leave = PwTheme.Btn(IsHost ? tx.Get("_UI_PlLiWind_Button_CancelGame", "Cancel Game") : tx.Get("_UI_PlLiWind_Button_LeaveGame", "Leave Game"),
                () => { NetSession.Close(); Boot.Instance.Show(new MultiplayerMenu()); }, 170);
            leave.TooltipText = tx.Get("_UI_PlLiWind_Button_LeaveGame_TT", "");
            b.AddChild(Row(leave, _ready, _start));
        }
        // (screenshots and checks: --open-slots n - slots 1..n-1 computer players; --pick-color i - slot i's colour picker open)
        var ua = GameApp.UserArgs();
        if (!_net && ua.TryGetValue("open-slots", out var os) && int.TryParse(os, out int on))
        {
            for (int i = 1; i < _setup.Slots.Count; i++) if (i < on) { if (_setup.Slots[i].Type != "ai") Skirmish.OpenSlot(_setup, i); } else _setup.Slots[i].Type = "closed";
            _setup.Teams = Math.Max(_setup.Teams, 2);
        }
        FillMaps();
        ShowMap();
        Refresh();
        if (ua.TryGetValue("pick-color", out var pc) && int.TryParse(pc, out int pi))
        {
            async void OpenLater()
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var cell = _colorCells.FirstOrDefault(c => _slots.GetChildren().IndexOf(c) / COLUMNS.Length - 1 == pi) ?? _colorCells.LastOrDefault();
                if (cell == null) return;
                _slotScroll.EnsureControlVisible(cell);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                cell.OpenPicker();
            }
            OpenLater();
        }
    }

    static OptionButton Opt(IEnumerable<(string Text, string Value)> items, float w)
    {
        var o = new OptionButton { CustomMinimumSize = new Vector2(w, 26), FocusMode = FocusModeEnum.None };
        foreach (var (t, v) in items) { o.AddItem(t); o.SetItemMetadata(o.ItemCount - 1, v); }
        return o;
    }
    static string Meta(OptionButton o) => o.Selected < 0 ? "" : o.GetItemMetadata(o.Selected).AsString();
    static void Pick(OptionButton o, string v) { for (int i = 0; i < o.ItemCount; i++) if (o.GetItemMetadata(i).AsString() == v) { o.Select(i); return; } if (o.ItemCount > 0) o.Select(0); }

    SkirmishMap? MapOf(string path) => _maps.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------ maps: those of the chosen game type, the random jungle
    void FillMaps()
    {
        _filling = true;
        _map.Clear();
        if (_setup.Mode == "deathmatch") { _map.AddItem($"Random jungle (2 - {MapGen.MAX_PLAYERS})"); _map.SetItemMetadata(0, ""); }
        string? last = null;
        foreach (var m in _maps)
        {
            if (!m.SetupModes.Contains(_setup.Mode)) continue;
            if (m.Folder != last) { _map.AddSeparator(m.Folder); last = m.Folder; }
            _map.AddItem($"{m.Name} ({m.Players})");
            _map.SetItemMetadata(_map.ItemCount - 1, m.Path);
            _map.SetItemTooltip(_map.ItemCount - 1, m.Path);
        }
        string had = _setup.Map;
        int sel = -1;
        for (int i = 0; i < _map.ItemCount; i++) if (!_map.IsItemSeparator(i) && string.Equals(_map.GetItemMetadata(i).AsString(), _setup.Map, StringComparison.OrdinalIgnoreCase)) sel = i;
        if (sel < 0) for (int i = 0; i < _map.ItemCount && sel < 0; i++) if (!_map.IsItemSeparator(i)) sel = i;
        if (sel >= 0) { _map.Select(sel); _setup.Map = _map.GetItemMetadata(sel).AsString(); }
        _setup.MapName = MapOf(_setup.Map)?.Name;
        _filling = false;
        if (!string.Equals(had, _setup.Map, StringComparison.OrdinalIgnoreCase)) ShowMap();
    }

    void ShowMap()
    {
        _mapError = null;
        _seedRow.Visible = _setup.Map == "";
        if (_setup.Map == "")
        {
            _mp = new MpSettings { MaxPlayers = MapGen.MAX_PLAYERS, GameTypes = new() { "Deathmatch" } };
            _preview.Show(null);
            int n = Math.Max(2, Skirmish.ActiveSlots(_setup).Count), k = MapGen.SizeFor(n);
            _desc.Text = $"A new jungle for every seed: up to {MapGen.MAX_PLAYERS} bases, forests, herds of dinosaurs. It grows with the players: " +
                         $"up to {MapGen.SIZE_PLAYERS[0]} {MapGen.SIZES[0]:0} m, {MapGen.SIZE_PLAYERS[1]} {MapGen.SIZES[1]:0} m, {MapGen.SIZE_PLAYERS[2]} {MapGen.SIZES[2]:0} m" +
                         $" (now {n} players: {MapGen.SIZES[k]:0} × {MapGen.SIZES[k]:0} m).";
            return;
        }
        var path = SkirmishMaps.Resolve(GameApp.Config!, _setup.Map);
        try
        {
            if (path == null) throw new System.IO.FileNotFoundException("not in the installation");
            var md = SkirmishMaps.Header(path);
            _mp = MapSource.MpSettingsOf(md);
            _preview.Show(path);
            var e = MapOf(_setup.Map);
            var (name, text, author) = e != null ? (e.Name, e.Text, e.Author) : (md.Name, "", "");
            _desc.Text = $"{name}\n{md.SettingName} · {md.W} × {md.H} m · {_mp.MaxPlayers} players · " +
                         string.Join(", ", _mp.GameTypes.Select(t => Skirmish.ModeOfType(t) is { } m ? Skirmish.ModeName(m) : t)) +
                         (author != "" ? $"\nby {author}" : "") + (text != "" ? "\n" + (text.Length > 160 ? text[..160] + "…" : text) : "");
        }
        catch (Exception ex)
        {
            _mapError = "This map could not be read: " + ex.Message;
            _desc.Text = _mapError;
            _preview.Show(null);
        }
    }

    // ------------------------------------------------------------ the options and the slots
    int MaxPlayers => (int)Math.Max(1, _mp.MaxPlayers);

    /// <summary>the tribes a slot may play: those of its start location on the map (Restrictions/Base/Tribes), with
    /// random head quarters those of any start location; and the configuration's (PIS:383-415)</summary>
    List<string> TribesOf(SkirmishSlot sl)
    {
        var cfgTribes = GameApp.Config!.PlayableTribes;
        List<string> SlotTribes(int hq) => (_mp.Slots.TryGetValue(hq.ToString(), out var ms) && ms.Tribes != null ? ms.Tribes : Skirmish.TRIBES.ToList()).Where(t => Skirmish.TRIBES.Contains(t) && cfgTribes.Contains(t)).ToList();
        var list = _setup.ChooseHQ ? SlotTribes(sl.Hq) : Enumerable.Range(0, MaxPlayers).SelectMany(SlotTribes).Distinct().ToList();
        return list.Count > 0 ? list : cfgTribes.ToList();
    }

    void Refresh()
    {
        if (_setup.Map == "") ShowMap();          // (the random jungle's size follows the players)
        _filling = true;
        var m = _setup.Mode; int act = Skirmish.ActiveSlots(_setup).Count;
        Pick(_mode, m);
        Pick(_defMin, ((int)_setup.DefenderMin).ToString());
        _defMin.Disabled = m != "defender";
        // teams: 1..players, at least 2 with locked diplomacy (CheckNumTeams LSP:453-474); Defender: two sides
        int lo = _setup.LockDiplomacy || m == "defender" ? 2 : 1, hi = m == "defender" ? 2 : Math.Max(lo, act);
        _setup.Teams = Math.Min(hi, Math.Max(lo, _setup.Teams));
        _teams.Clear();
        for (int v = lo; v <= hi; v++) { _teams.AddItem(v.ToString()); _teams.SetItemMetadata(_teams.ItemCount - 1, v.ToString()); }
        Pick(_teams, _setup.Teams.ToString());
        _teams.Disabled = m == "defender";
        if (_pb == null || !_pb.Ready) _setup.Points = -1;              // game data from before the army budget
        Pick(_points, _setup.Points.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _points.Disabled = _pb == null || !_pb.Ready;
        void Box(CheckBox c, bool v, bool off) { c.ButtonPressed = v; c.Disabled = off; c.Modulate = off ? new Color(1, 1, 1, 0.5f) : Colors.White; }
        Box(_warp, _setup.Warpgate, false);
        Box(_fog, !_setup.Fog, false);
        Box(_lock, _setup.LockDiplomacy, m != "deathmatch" || _setup.Teams < 2);
        // (the handicap is the tech tree's /Filters/Special/Upgrades/handicap/handicap_<hp>: MIRAGE's tree has none - the
        // option would do nothing)
        bool hasHcap = _pb != null && RulesOf(GameApp.Config!).Base.Filter("/Filters/Special/Upgrades/handicap/handicap_150") != null;
        if (!hasHcap) { _setup.Handicap = false; _hcap.TooltipText = "The game data of this configuration has no handicaps"; }
        Box(_hcap, _setup.Handicap, !hasHcap);
        Box(_hq, _setup.ChooseHQ, m == "defender");
        // (only MIRAGE's tree has the xenage filters: in the base game both do nothing)
        var tb = _pb != null ? RulesOf(GameApp.Config!).Base : null;
        bool hasE6 = tb?.Filter("SEAS/Upgrades/xenage/epoch_six_enabled") != null, hasNoT = tb?.Filter("SEAS/Upgrades/xenage/titans_disabled") != null;
        Box(_epoch6, _setup.EpochSix, !hasE6); _epoch6.Visible = hasE6;
        Box(_titans, _setup.RemoveTitans, !hasNoT); _titans.Visible = hasNoT;
        bool hasFly = tb?.Filter("SEAS/Upgrades/xenage/flying_disabled") != null;
        Box(_flying, _setup.FlyingEnabled, !hasFly); _flying.Visible = hasFly;
        // (MIRAGE: "Portals" only when the warp gate is off - Mirage_UI_scripts.usl SetDefaultStuff)
        bool hasPortals = tb?.Filter("SEAS/Upgrades/seas_portal_terminus/portal_terminus_visible") != null;
        Box(_portals, _setup.Portals, !hasPortals || _setup.Warpgate); _portals.Visible = hasPortals;
        // (MIRAGE's "Building limit": only a tree with limits - BuildingCount filters)
        bool hasLimits = PwRemake.Sim.Rules.GameplayOptions.HasBuildingLimits(tb);
        Box(_limits, _setup.BuildingLimit, !hasLimits); _limits.Visible = hasLimits;
        _filling = false;
        FillSlots();
        ShowProblem();
        Save();
        // (the network mode: only the host's controls of the whole setup; the slots decide row by row)
        if (_net && !IsHost)
            foreach (var c in new Control[] { _map, _mode, _defMin, _teams, _points, _warp, _fog, _lock, _hcap, _hq, _epoch6, _titans, _flying, _portals, _limits, _seed })
                switch (c) { case OptionButton o: o.Disabled = true; break; case BaseButton bb: bb.Disabled = true; break; case SpinBox sb: sb.Editable = false; break; }
    }

    /// <summary>the network mode: the lobby as the host sent it (a change of the host or a player), shown without
    /// sending it back</summary>
    public override void _Process(double delta)
    {
        if (!_net) return;
        var n = N;
        if (n == null) return;
        var l = n.Client.Lobby;
        if (l != null && !ReferenceEquals(l, _lobbyShown) && l.Skirmish != null && !PickerOpen)
        {
            _lobbyShown = l;
            _setup = l.Skirmish.Clone();
            _sent = IsHost ? NetProtocol.ToNode(_setup)!.ToJsonString() : _sent;
            _noSave = true;
            try { FillMaps(); ShowMap(); Refresh(); }
            finally { _noSave = false; }
        }
        if (_netInfo != null && l != null)
        {
            var peers = string.Join(" · ", l.Players.Select(p => $"{p.Name}{(p.Host ? " (host)" : "")}{(p.Ready || p.Host ? " - GO" : "")}{(p.Ping > 0 ? $" {p.Ping} ms" : "")}{(p.Problem != null ? " - " + p.Problem : "")}"));
            _netInfo.Text = $"{l.Name} · port {n.Host?.Port.ToString() ?? "?"} · {peers}";
        }
        if (_ready != null) _ready.Text = (n.Client.MyPeer?.Ready ?? false) ? "Not ready" : GameTexts.For(GameApp.Config).Get("_ActionView_ButtonReady", "Ready");
    }

    static readonly (string Key, string Text, float W)[] COLUMNS =
    {
        ("name", "Player", 120), ("tribe", "Tribe", 190), ("army", "Army", 190), ("level", "AI level", 70), ("team", "Team", 90), ("color", "Colour", 200), ("hp", "HP", 80), ("hq", "HQ", 80),
    };

    void FillSlots()
    {
        foreach (var c in _slots.GetChildren()) { _slots.RemoveChild(c); c.QueueFree(); }
        _heads.Clear();
        _colorCells.Clear();
        foreach (var (k, t, w) in COLUMNS)
        {
            var l = new Label { Text = t, CustomMinimumSize = new Vector2(w, 0) };
            l.AddThemeColorOverride("font_color", PwTheme.Hi);
            _heads[k] = l;
            _slots.AddChild(l);
        }
        for (int i = 0; i < _setup.Slots.Count && i < Skirmish.MAX_SLOTS; i++) SlotRow(i);
    }

    void SlotRow(int i)
    {
        var sl = _setup.Slots[i];
        bool closed = sl.Type == "closed", ai = sl.Type == "ai";
        var tribes = TribesOf(sl);
        if (!closed && sl.Tribe != "Random" && !tribes.Contains(sl.Tribe)) sl.Tribe = tribes.Count > 1 ? "Random" : tribes[0];
        sl.Team = Math.Min(Math.Max(1, sl.Team), _setup.Teams);
        void Changed() { if (!_filling) Callable.From(Refresh).CallDeferred(); }
        // the player
        if (_net && sl.Type == "human")
        {
            var peer = N?.Client.Lobby?.Peers.FirstOrDefault(p => p.Name == sl.Owner);
            _slots.AddChild(new Label { Text = (sl.Owner ?? "?") + (peer != null && (peer.Ready || peer.Host) ? "  GO" : ""), CustomMinimumSize = new Vector2(120, 0), Modulate = sl.Owner == MyName ? PwTheme.Hi : Colors.White });
        }
        else if (_net && !IsHost) _slots.AddChild(new Label { Text = sl.Type == "open" ? "Open" : sl.Type == "ai" ? "Computer" : "Closed", CustomMinimumSize = new Vector2(120, 0) });
        else if (_net)
        {
            var ty = Opt(new[] { ("Open", "open"), ("Computer", "ai"), ("Closed", "closed") }, 120);
            Pick(ty, sl.Type);
            ty.ItemSelected += _ => { var v = Meta(ty); if (v == "ai") Skirmish.OpenSlot(_setup, i); else sl.Type = v; Changed(); };
            _slots.AddChild(ty);
        }
        else if (i == 0) _slots.AddChild(new Label { Text = "You", CustomMinimumSize = new Vector2(120, 0), Modulate = PwTheme.Hi });
        else
        {
            var ty = Opt(new[] { ("Computer", "ai"), ("Closed", "closed") }, 120);
            Pick(ty, sl.Type);
            ty.ItemSelected += _ => { if (Meta(ty) == "ai") Skirmish.OpenSlot(_setup, i); else sl.Type = "closed"; Changed(); };
            _slots.AddChild(ty);
        }
        if (closed || sl.Type == "open") { for (int k = 1; k < COLUMNS.Length; k++) _slots.AddChild(new Control()); return; }
        int first = _slots.GetChildCount();
        // the tribe (with its icon)
        var tribe = new OptionButton { CustomMinimumSize = new Vector2(190, 26), FocusMode = FocusModeEnum.None };
        if (tribes.Count > 1) { tribe.AddItem("Random"); tribe.SetItemMetadata(0, "Random"); }
        foreach (var t in tribes)
        {
            var ic = TribeInfo.Icon(t);
            if (ic != null) tribe.AddIconItem(ic, TribeInfo.Name(t)); else tribe.AddItem(TribeInfo.Name(t));
            tribe.SetItemMetadata(tribe.ItemCount - 1, t);
        }
        Pick(tribe, sl.Tribe);
        tribe.ItemSelected += _ => { sl.Tribe = Meta(tribe); Changed(); };
        _slots.AddChild(tribe);
        // the army: a computer player's personality, the human's preset or own army (PIS:566-642)
        if (ai)
        {
            var pe = Opt(Skirmish.PERSONALITIES.Select(p => (p.Name, p.Name)), 190);
            Pick(pe, sl.Personality);
            pe.ItemSelected += _ => { sl.Personality = Meta(pe); sl.Behaviour = null; Changed(); };
            _slots.AddChild(pe);
        }
        else _slots.AddChild(ArmyCell(sl, i));
        // the AI level
        if (ai)
        {
            var lv = Opt(Enumerable.Range(0, 10).Select(v => (v.ToString(), v.ToString())), 70);
            Pick(lv, ((int)sl.Level).ToString());
            lv.ItemSelected += _ => { sl.Level = int.Parse(Meta(lv)); Changed(); };
            _slots.AddChild(lv);
        }
        else _slots.AddChild(new Control());
        // the team (Defender: who defends)
        if (_setup.Mode == "defender") _slots.AddChild(new Label { Text = !_setup.ChooseHQ ? "?" : sl.Hq == 0 ? "Defender" : "Attacker" });
        else
        {
            var tm = Opt(Enumerable.Range(1, _setup.Teams).Select(v => (v.ToString(), v.ToString())), 90);
            Pick(tm, sl.Team.ToString());
            tm.ItemSelected += _ => { sl.Team = int.Parse(Meta(tm)); Changed(); };
            _slots.AddChild(tm);
        }
        // the colour: the palette (the original's eight of ACColors.txt, the remake's eight) or any RGB colour; while the
        // picker is open the colours are checked at once (two alike: refused, the column red), the choice is saved when
        // it closes
        var cols = new PlayerColorCell(sl.Color, 200);
        cols.Picking += id => { sl.Color = id; ShowProblem(); };
        cols.Chosen += id => { sl.Color = id; Changed(); };
        _colorCells.Add(cols);
        _slots.AddChild(cols);
        // the handicap
        var hp = Opt(Enumerable.Range(5, 11).Select(v => ($"{v * 10} %", (v * 10).ToString())), 80);
        Pick(hp, sl.Hp.ToString());
        hp.Disabled = !_setup.Handicap;
        hp.ItemSelected += _ => { sl.Hp = int.Parse(Meta(hp)); Changed(); };
        _slots.AddChild(hp);
        // the headquarters position
        if (_setup.ChooseHQ)
        {
            var hq = Opt(Enumerable.Range(0, MaxPlayers).Select(v => ((v + 1).ToString(), v.ToString())), 80);
            Pick(hq, sl.Hq.ToString());
            hq.ItemSelected += _ => { sl.Hq = int.Parse(Meta(hq)); Changed(); };
            _slots.AddChild(hq);
        }
        else _slots.AddChild(new Label { Text = "random", Modulate = new Color(1, 1, 1, 0.6f) });
        // (the network mode: another's slot is shown, not changed)
        if (!CanEdit(i))
            for (int k = first; k < _slots.GetChildCount(); k++) Disable(_slots.GetChild(k));
    }

    static void Disable(Node n)
    {
        if (n is PlayerColorCell pc) { pc.Disabled = true; return; }
        if (n is BaseButton b) b.Disabled = true;
        foreach (var c in n.GetChildren()) Disable(c);
    }

    /// <summary>the human's army (PIS:566-598): with points the default presets of that budget (Standard, Boomer, Rusher)
    /// or the army bought on the "My Army" page; "Predefined" without points</summary>
    Control ArmyCell(SkirmishSlot sl, int i)
    {
        var D = _pb;
        if (_setup.Points < 0 || D == null || !D.Ready) return new Label { Text = "Predefined", CustomMinimumSize = new Vector2(190, 0), Modulate = new Color(1, 1, 1, 0.7f) };
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(190, 0) };
        var kinds = new List<(string, string)> { ("Standard", "standard"), ("Boomer", "boomer"), ("Rusher", "rusher") };
        if (sl.OwnArmy(_setup.Points)) kinds.Add(("My Army", "custom"));
        var o = Opt(kinds, 140);
        Pick(o, sl.Preset == "custom" && !sl.OwnArmy(_setup.Points) ? "standard" : sl.Preset ?? "standard");
        o.ItemSelected += _ => { sl.Preset = Meta(o); Callable.From(Refresh).CallDeferred(); };
        row.AddChild(o);
        if (sl.Tribe != "Random" && !_net)
        {
            var btn = PwTheme.Btn("…", () => OpenArmy(i), 40);
            btn.TooltipText = "My Army: buy the start army with the points";
            row.AddChild(btn);
        }
        return row;
    }

    void OpenArmy(int i) { Save(); Boot.Instance.Show(ArmyOf(i)); }

    /// <summary>the "My Army" page of a slot of the stored setup (also --screen army: slot 0)</summary>
    public static ArmyScreen ArmyOf(int i)
    {
        var setup = StoredSetup();
        if (setup.Points < 0) setup.Points = 1000;
        var sl = setup.Slots[i]; var tribe = sl.Tribe == "Random" ? GameApp.Config!.PlayableTribes.First() : sl.Tribe; double credits = setup.Points;
        var PB = new PointBuy(RulesOf(GameApp.Config!), null);
        double total = PB.Total(credits, tribe);
        Army Fresh() => PB.Load(PB.Preset(tribe, credits, sl.Preset is { Length: > 0 } p && p != "custom" ? p : "standard"), tribe, total);
        return new ArmyScreen(new ArmyScreen.Options
        {
            Tribe = tribe, Credits = credits, Total = total, Army = sl.OwnArmy(credits) ? sl.Army!.Clone() : Fresh(), Mp = true, Buildings = true,
            Back = () => Boot.Instance.Show(new SkirmishMenu()),
            Done = army =>
            {
                var s = StoredSetup();
                var t = s.Slots[i];
                t.Army = army; t.ArmyTribe = tribe; t.ArmyPoints = credits; t.Preset = "custom";
                GameApp.Settings.Skirmish[GameApp.Config!.Id] = s; GameApp.Settings.Save();
                Boot.Instance.Show(new SkirmishMenu());
            },
        });
    }

    // ------------------------------------------------------------ validation (PLW:334-456: the offending column turns red)
    static readonly (string Re, string Col)[] COLUMN = { ("room for", "name"), ("colour", "color"), ("headquarters", "hq"), ("defend", "hq"), ("teams", "team") };
    string? Problem()
    {
        if (_mapError != null) return _mapError;
        if (!(_mp.GameTypes.Count > 0 ? _mp.GameTypes : new List<string> { "Deathmatch" }).Select(Skirmish.ModeOfType).Contains(_setup.Mode)) return $"This map has no {Skirmish.ModeName(_setup.Mode)} game.";
        if (Skirmish.ActiveSlots(_setup).Count < 2) return "A skirmish needs at least two players.";
        return Skirmish.Validate(_setup, MaxPlayers);
    }
    string? ShowProblem()
    {
        var err = Problem();
        _err.Text = err ?? "";
        var col = err == null ? null : COLUMN.FirstOrDefault(c => err.Contains(c.Re)).Col;
        foreach (var (k, l) in _heads) l.AddThemeColorOverride("font_color", k == col ? PwTheme.Lost : PwTheme.Hi);
        if (_net && N?.Client.Lobby is { } lob && err == null && IsHost)
        {
            var cl = lob.Clone(); cl.Skirmish = _setup;
            err = LobbyStart.Problem(cl);
            _err.Text = err ?? "";
        }
        _start.Disabled = err != null;
        return err;
    }

    // ------------------------------------------------------------ start
    void StartGame()
    {
        if (ShowProblem() != null) return;
        if (_net)
        {
            // the host starts: every machine gets the start with its seat and opens the loading screen (NetSession)
            Save();
            N?.Client.HostStart(null);
            return;
        }
        Save();
        var s = GameApp.Settings;
        var setup = _setup.Clone();
        var start = new GameStart
        {
            Configuration = GameApp.Config!.Id, Mode = GameMode.Skirmish, Skirmish = setup,
            Debug = s.UseDebug ? s.Debug.ForGame() : null, Seed = setup.Seed,
        };
        L.Info($"start {start}: {setup.ToJson()}");
        Boot.Instance.Show(LoadingScreen.ForSkirmish(start));
    }

    /// <summary>--skirmish [map] [--players Hu,Aje,...] [--teams 1,2] [--levels 5,5] [--colors blue,#ff00ff] [--points n] [--mode m] [--seed n]
    /// [--no-fog] [--debug]: start a skirmish without the menus (tests; main.js quickConfig ?players=...)</summary>
    public static void QuickStart(Dictionary<string, string> args)
    {
        var cfg = GameApp.Config;
        if (cfg == null) { L.Error("--skirmish: no configuration"); return; }
        var setup = StoredSetup();
        var map = args.GetValueOrDefault("skirmish", "");
        if (map == "random") { setup.Map = ""; setup.MapName = null; }
        else if (map != "")
        {
            // a file name is enough: the first map of the list whose path ends with it
            var m = SkirmishMaps.List(cfg).FirstOrDefault(x => x.Path.EndsWith(map, StringComparison.OrdinalIgnoreCase) || x.File.Equals(map + ".ula", StringComparison.OrdinalIgnoreCase) || x.File.Equals(map + MapFiles.RemakeExt, StringComparison.OrdinalIgnoreCase));
            setup.Map = m?.Path ?? map;
            setup.MapName = m?.Name;
        }
        else if (setup.Map == "" && SkirmishMaps.List(cfg).FirstOrDefault() is { } first) { setup.Map = first.Path; setup.MapName = first.Name; }
        string[] Arr(string k) => args.GetValueOrDefault(k, "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var players = Arr("players");
        if (players.Length > 0)
            for (int i = 0; i < setup.Slots.Count; i++)
            {
                var sl = setup.Slots[i];
                if (i >= players.Length) { sl.Type = "closed"; continue; }
                sl.Type = i == 0 ? "human" : "ai"; sl.Tribe = players[i];
                if (i < Arr("teams").Length) sl.Team = int.Parse(Arr("teams")[i]);
                if (i > 0 && i - 1 < Arr("levels").Length) sl.Level = int.Parse(Arr("levels")[i - 1]);
                if (i > 0 && i - 1 < Arr("aib").Length) sl.Behaviour = Arr("aib")[i - 1];
                if (i < Arr("hq").Length) { sl.Hq = int.Parse(Arr("hq")[i]); setup.ChooseHQ = true; }
                if (i < Arr("colors").Length) sl.Color = Arr("colors")[i];          // palette ids or #rrggbb
            }
        var act = Skirmish.ActiveSlots(setup);
        if (act.Count > 0) setup.Teams = Math.Max(1, act.Max(x => x.Slot.Team));
        if (args.TryGetValue("mode", out var mo)) Skirmish.ApplyMode(setup, mo);
        if (args.TryGetValue("points", out var pt)) setup.Points = Json.ParseNum(pt, -1);
        if (args.TryGetValue("seed", out var sd) && ulong.TryParse(sd, out var seed)) setup.Seed = seed;
        if (args.ContainsKey("no-fog")) setup.Fog = false;
        var dbg = GameApp.Settings.Debug.Clone();
        if (args.ContainsKey("debug")) { dbg.On = true; dbg.Free = true; }
        var start = new GameStart
        {
            Configuration = cfg.Id, Mode = GameMode.Skirmish, Skirmish = setup, Seed = setup.Seed,
            Debug = (args.ContainsKey("debug") || GameApp.Settings.UseDebug) ? dbg.ForGame() : null,
        };
        L.Info($"--skirmish: {start}");
        Boot.Instance.Show(LoadingScreen.ForSkirmish(start));
    }
}
