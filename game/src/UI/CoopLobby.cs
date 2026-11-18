// The lobby of a campaign mission played together (docs/COOP.md, docs/MULTIPLAYER.md "Co-op"), online and offline.
//
// Two modes (the host's choice):
//   Shared          every human commands the mission's own player - the same army (no ally is taken over)
//   Co-op campaign  2 or 3 seats, each a human or an AI commander with a faction of its own, its own base and economy;
//                   seat 3 can be empty; the story heroes are split among the humans (the host changes the split); the
//                   host's co-op difficulty scales the enemies. Missions without a co-op definition (game/coop) are
//                   greyed out in this mode.
// Offline (the campaign menu's "Co-op"): one human on seat 1 with one or two AI commanders, no network.
//
// The host picks the mission, the difficulty, MIRAGE's game options (sixth stage, no titans, flying units: GameStart.Gameplay, MIRAGE
// only) and how cutscenes end; every player takes a free human seat and picks its
// faction; the players say they are ready, the host starts. The chat below. The campaign menu's mission list and texts
// (index.json, CampaignStatus: missions not adapted yet are greyed).
//
// A mission with an army budget (point_buy) starts with the level's own army in co-op (no army screen: whose budget
// would it be?).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.App;
using PwRemake.Net;
using PwRemake.Sim.Content;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;

namespace PwRemake.UI;

public partial class CoopLobby : MenuScreen
{
    static readonly Logger L = Log.For("UI.Coop");
    static NetSession? N => NetSession.Current;
    /// <summary>offline (the campaign menu's Co-op): a lobby of this machine only</summary>
    readonly bool _offline;
    LobbyState? _local;
    bool IsHost => _offline || N?.Hosting == true;
    int MeId => _offline ? 0 : N?.Client.Me ?? -1;
    LobbyState? Lobby => _offline ? _local : N?.Client.Lobby;
    List<MissionEntry> _missions = new();
    OptionButton _mission = null!, _mode = null!, _tribe = null!, _diff = null!, _coopDiff = null!, _rule = null!;
    Label _text = null!, _err = null!, _tribeLabel = null!, _coopDiffLabel = null!;
    VBoxContainer _players = null!;
    Button _start = null!, _ready = null!;
    /// <summary>MIRAGE's game options (the host's; null in the base game)</summary>
    CheckBox? _epoch6, _titans, _flying, _portals, _limits;
    LobbyState? _shown;
    bool _filling;

    public CoopLobby(bool offline = false) : base(offline ? "Co-op" : "Multiplayer", minWidth: 1020)
    {
        _offline = offline;
        if (offline)
        {
            _local = InitialLobby("You");
            _local.CoopMode = "campaign";
            _local.Configuration = GameApp.Config?.Id ?? "";
            _local.Peers.Add(new LobbyPeer { Id = 0, Name = "You", Host = true, Ready = true, Seat = 1 });
            if (_local.Coop != null) { _local.Coop.Seats[1].Kind = "ai"; }
            // the first mission with a definition
            if (_local.Coop?.Definition == null)
            {
                var cfg = GameApp.Config!;
                var m = MissionEntry.Load(cfg.MissionsDir).FirstOrDefault(x => CoopDefinition.CampaignAvailable(cfg.Id, x.Id));
                if (m != null) { SetMission(_local, m, GameApp.Settings.CampaignTribe, GameApp.Settings.Difficulty); _local.Coop!.Seats[1].Kind = "ai"; }
            }
        }
    }

    /// <summary>the lobby a host offers first: the campaign menu's last mission, tribe and difficulty</summary>
    public static LobbyState InitialLobby(string hostName)
    {
        var s = GameApp.Settings;
        var cfg = GameApp.Config!;
        var list = MissionEntry.Load(cfg.MissionsDir).Where(m => !CampaignStatus.Hidden(cfg.Id, m.Id)).ToList();
        var m = list.FirstOrDefault(x => x.Id == s.CampaignMission) ?? list.FirstOrDefault(x => CampaignStatus.Of(cfg.Id, x.Id) != MissionState.NotYet) ?? list.FirstOrDefault();
        var l = new LobbyState { Mode = "coop", Name = $"{hostName}'s game", SceneRule = s.MpSceneRule, CoopMode = "shared" };
        if (m != null) SetMission(l, m, s.CampaignTribe, s.Difficulty);
        return l;
    }

    /// <summary>a mission chosen: the start, the players a human may command (Shared: the mission's player only) and the
    /// co-op campaign's seats from the mission's definition (null when it has none)</summary>
    public static void SetMission(LobbyState l, MissionEntry m, string tribe, int difficulty)
    {
        var s = GameApp.Settings;
        var cfg = GameApp.Config!;
        l.Campaign = new GameStart
        {
            Configuration = cfg.Id, Mode = GameMode.Campaign, Mission = m.Id, MissionFolder = m.Folder, Tribe = tribe, Difficulty = difficulty,
            Debug = s.UseDebug ? s.Debug.ForGame() : null, Seed = (ulong)(DateTime.Now.Ticks & 0x7fffffff),
            // MIRAGE's game options: kept from the mission before, else the campaign's stored choice
            Gameplay = l.Campaign?.Gameplay?.Clone() ?? CampaignMenu.GameplayFor(cfg),
        };
        l.CoopPlayers = CoopPlayersOf(m);
        var old = l.Coop;
        var def = CoopDefinition.Load(cfg.Id, m.Id);
        l.Coop = new CoopLobbyState
        {
            Difficulty = old?.Difficulty ?? 0,
            Definition = def?.Raw, MissionPlayers = CoopPlan.MissionSlots(Path.Combine(cfg.MissionsDir, m.Folder)),
            Seats = old?.Seats.Select(x => new CoopLobbySeat { Seat = x.Seat, Kind = x.Kind, Tribe = x.Tribe, Aggression = x.Aggression }).ToList() ?? new CoopLobbyState().Seats,
        };
        l.Coop.Seats[0].Tribe = tribe;
        // a definition with room for two seats only: seat 3 empty
        if (def != null && !def.Supports(3)) l.Coop.Seats[2].Kind = "empty";
    }

    /// <summary>the players of a mission a human may command in Shared mode: its human player only (taking an ally over is
    /// the co-op campaign's: docs/COOP.md)</summary>
    public static List<CoopChoice> CoopPlayersOf(MissionEntry m)
    {
        var res = new List<CoopChoice>();
        try
        {
            var mj = Json.TryParseFile(Path.Combine(GameApp.Config!.MissionsDir, m.Folder, "mission.json"));
            var pl = mj?["players"] as JsonArray;
            if (pl == null) { res.Add(new CoopChoice { Player = 0, Name = "You", Main = true }); return res; }
            int human = -1;
            for (int i = 0; i < pl.Count; i++) if (pl[i]?["control"].Str() == "human") { human = i; break; }
            if (human < 0) human = 0;
            res.Add(new CoopChoice { Player = human, Name = pl[human]?["name"].Str() is { Length: > 0 } n ? n : "Player " + human, Tribe = pl[human]?["tribe"].Str() ?? "", Main = true });
        }
        catch (Exception e) { L.Warn($"the players of mission {m.Id}: {e.Message}"); if (res.Count == 0) res.Add(new CoopChoice { Player = 0, Name = "You", Main = true }); }
        return res;
    }

    static GameTexts Tx => GameTexts.For(GameApp.Config);
    /// <summary>a hero class as a name ("Cole_s0" -> Cole; Stina rides her sabre-tooth)</summary>
    public static string HeroName(string cls) => cls.ToLowerInvariant() switch
    {
        "special_eusmilus" or "stina_s0" => "Stina", "bela_s0" => "Béla", _ => cls.EndsWith("_s0", StringComparison.OrdinalIgnoreCase) ? char.ToUpperInvariant(cls[0]) + cls[1..^3] : cls,
    };

    protected override void Build()
    {
        var cfg = GameApp.Config!;
        var b = Window.Body;
        _missions = MissionEntry.Load(cfg.MissionsDir).Where(m => !CampaignStatus.Hidden(cfg.Id, m.Id)).ToList();
        b.AddChild(PwTheme.Heading(_offline ? $"{Tx.Get("_UI_MainMenu_Button_StartCampaign", "Campaign")} · Co-op · {cfg.Title}" : $"Multiplayer · {Tx.Get("_UI_MainMenu_Button_StartCampaign", "Campaign")} (co-op) · {cfg.Title}", 20));
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 20);
        b.AddChild(top);
        // ---- the mission (the host's)
        var gm = Group("Mission");
        top.AddChild(gm);
        _mission = new OptionButton { CustomMinimumSize = new Vector2(380, 28), FitToLongestItem = false };
        foreach (var m in _missions) { _mission.AddItem(m.Title); _mission.SetItemMetadata(_mission.ItemCount - 1, m.Id); }
        _mission.ItemSelected += _ => { if (!_filling) HostChanged(); };
        gm.AddChild(_mission);
        _text = PwTheme.Para("", 13, PwTheme.Text);
        _text.CustomMinimumSize = new Vector2(380, 110);
        gm.AddChild(_text);
        // ---- the options (the host's)
        var go = Group("Settings");
        top.AddChild(go);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        go.AddChild(grid);
        _mode = new OptionButton { CustomMinimumSize = new Vector2(240, 26), TooltipText = "Shared: everybody commands the mission's own army.\nCo-op campaign: 2-3 seats, each with its own faction, base and economy; humans or AI commanders." };
        _mode.AddItem("Shared"); _mode.SetItemMetadata(0, "shared");
        _mode.AddItem("Co-op campaign"); _mode.SetItemMetadata(1, "campaign");
        _mode.ItemSelected += _ => { if (!_filling) HostChanged(); };
        _tribe = new OptionButton { CustomMinimumSize = new Vector2(240, 26) };
        _tribe.AddItem("As in the mission"); _tribe.SetItemMetadata(0, "");
        foreach (var t in cfg.PlayableTribes) { _tribe.AddItem(TribeInfo.Name(t)); _tribe.SetItemMetadata(_tribe.ItemCount - 1, t); }
        _tribe.ItemSelected += _ => { if (!_filling) HostChanged(); };
        _diff = new OptionButton { CustomMinimumSize = new Vector2(240, 26) };
        foreach (var (n, i) in new[] { ("Easy", 0), ("Medium", 1), ("Hard", 2), ("Nightmare (bigger raids)", 3) }) { _diff.AddItem(n); _diff.SetItemMetadata(_diff.ItemCount - 1, i); }
        _diff.ItemSelected += _ => { if (!_filling) HostChanged(); };
        _coopDiff = new OptionButton { CustomMinimumSize = new Vector2(240, 26), TooltipText = "How much stronger the enemies get per extra seat (more units, health and damage)" };
        for (int i = 0; i < 3; i++) { _coopDiff.AddItem(CoopScaling.DifficultyNames[i]); _coopDiff.SetItemMetadata(i, i); }
        _coopDiff.ItemSelected += _ => { if (!_filling) HostChanged(); };
        _rule = new OptionButton { CustomMinimumSize = new Vector2(240, 26), TooltipText = "How a cutscene ends for everybody (the game waits while it shows)" };
        _rule.AddItem("Everybody has seen or skipped it"); _rule.SetItemMetadata(0, "all");
        _rule.AddItem("The host decides"); _rule.SetItemMetadata(1, "host");
        _rule.ItemSelected += _ => { if (!_filling) HostChanged(); };
        _tribeLabel = new Label { Text = "Tribe of the mission's player" };
        _coopDiffLabel = new Label { Text = "Co-op difficulty" };
        foreach (var (l, c) in new (Control, Control)[] { (new Label { Text = "Mode" }, _mode), (_tribeLabel, _tribe), (new Label { Text = "Difficulty" }, _diff), (_coopDiffLabel, _coopDiff), (new Label { Text = "Cutscenes end when" }, _rule) })
        {
            if (_offline && (c == _mode || c == _rule)) continue;
            grid.AddChild(l);
            grid.AddChild(c);
        }
        // MIRAGE's game options (tech tree filters for every player of the mission: Rules.GameplayOptions)
        if (CampaignMenu.HasGameOptions(cfg))
        {
            CheckBox Chk(string text)
            {
                var c = new CheckBox { Text = text, FocusMode = FocusModeEnum.None, TooltipText = "MIRAGE's game option, for every player of the mission (the host's choice)" };
                c.Toggled += _ => { if (!_filling) HostChanged(); };
                go.AddChild(c);
                return c;
            }
            _epoch6 = Chk("Sixth stage (final stage: titans, great cannon ...)");
            _titans = Chk("No titans");
            _flying = Chk(SkirmishMenu.FlyingText);
            _portals = Chk(SkirmishMenu.PortalsText);
            _limits = Chk(SkirmishMenu.LimitsText);
        }
        go.AddChild(PwTheme.Para("Missions with an army budget start with the level's own army.", 12, PwTheme.Dim));
        // ---- the players / seats
        var gp = Group(Tx.Get("_MultiPlayerSelect_Players", "Players"));
        b.AddChild(gp);
        _players = new VBoxContainer();
        _players.AddThemeConstantOverride("separation", 4);
        gp.AddChild(_players);
        _err = PwTheme.Para("", 14, PwTheme.Lost);
        b.AddChild(_err);
        if (N != null && !_offline)
        {
            var gc = Group(Tx.Get("_MultiPlayerSelect_Chat", "Chat"));
            gc.AddChild(new LobbyChat(N.Client) { CustomMinimumSize = new Vector2(0, 130) });
            b.AddChild(gc);
        }
        _start = PwTheme.Btn(Tx.Get("_UI_PlLiWind_Button_StartGame", "Start Game"), Start, 170);
        _start.TooltipText = Tx.Get("_UI_PlLiWind_Button_StartGame_TT", "");
        _ready = PwTheme.Btn(Tx.Get("_ActionView_ButtonReady", "Ready"), () => N?.Client.SetReady(!(N.Client.MyPeer?.Ready ?? false)), 170);
        var leave = _offline ? PwTheme.Btn("Back", () => Boot.Instance.Show(new CampaignMenu()), 170)
            : PwTheme.Btn(IsHost ? Tx.Get("_UI_PlLiWind_Button_CancelGame", "Cancel Game") : Tx.Get("_UI_PlLiWind_Button_LeaveGame", "Leave Game"),
                () => { NetSession.Close(); Boot.Instance.Show(new MultiplayerMenu()); }, 170);
        b.AddChild(Row(leave, _ready, _start));
        _start.Visible = IsHost; _ready.Visible = !IsHost;
        foreach (var c in new[] { _mission, _mode, _tribe, _diff, _coopDiff, _rule }) c.Disabled = !IsHost;
        foreach (var c in new[] { _epoch6, _titans, _flying, _portals, _limits }) if (c != null) c.Disabled = !IsHost;
    }

    /// <summary>a changed lobby: offline it is the lobby, online the host sends it to everybody</summary>
    void Set(LobbyState l)
    {
        if (_offline) { _local = l; _shown = null; return; }
        N?.Client.HostSetLobby(l);
    }

    /// <summary>the host changed the mission or an option: to the players</summary>
    void HostChanged()
    {
        if (Lobby is not { } lobby || !IsHost) return;
        var l = lobby.Clone();
        int id = _mission.GetItemMetadata(Math.Max(0, _mission.Selected)).AsInt32();
        var m = _missions.FirstOrDefault(x => x.Id == id);
        if (m == null) return;
        string tribe = _tribe.GetItemMetadata(Math.Max(0, _tribe.Selected)).AsString();
        int diff = _diff.GetItemMetadata(Math.Max(0, _diff.Selected)).AsInt32();
        if (!_offline) l.CoopMode = _mode.GetItemMetadata(Math.Max(0, _mode.Selected)).AsString();
        if (l.Campaign?.Mission != m.Id) SetMission(l, m, tribe, diff);
        else { l.Campaign.Tribe = tribe; l.Campaign.Difficulty = diff; if (l.Coop != null && l.CoopMode == "shared") l.Coop.Seats[0].Tribe = tribe; }
        if (l.Coop != null) l.Coop.Difficulty = _coopDiff.GetItemMetadata(Math.Max(0, _coopDiff.Selected)).AsInt32();
        if (!_offline) l.SceneRule = _rule.GetItemMetadata(Math.Max(0, _rule.Selected)).AsString();
        var s = GameApp.Settings;
        if (_epoch6 != null && _titans != null && l.Campaign != null)
        {
            l.Campaign.Gameplay = new PwRemake.Sim.Rules.GameplayOptions { EpochSix = _epoch6.ButtonPressed, RemoveTitans = _titans.ButtonPressed, FlyingEnabled = _flying?.ButtonPressed ?? false, Portals = _portals?.ButtonPressed ?? false, BuildingLimit = _limits?.ButtonPressed ?? false };
            s.CampaignGameplay = l.Campaign.Gameplay.Clone();
        }
        s.CampaignMission = m.Id; s.CampaignTribe = tribe; s.Difficulty = diff; s.MpSceneRule = l.SceneRule; s.Save();
        Set(l);
    }

    /// <summary>the host changes a seat (kind, faction, aggression) or the hero split</summary>
    void HostSeat(Action<CoopLobbyState> change)
    {
        if (Lobby is not { Coop: not null } lobby || !IsHost) return;
        var l = lobby.Clone();
        change(l.Coop!);
        Set(l);
    }

    void Start()
    {
        if (Lobby is not { } l) return;
        var p = LobbyStart.Problem(l);
        if (p != null) { _err.Text = p; return; }
        var profile = new MissionPresenterAdapter().LoadProfile();
        if (!_offline) { N!.Client.HostStart(profile); return; }       // (the host's profile values: the same for every machine)
        // offline: this machine's game, the seats as a networked game has them (one human)
        var start = LobbyStart.Make(l, profile?.ToDictionary(x => x.Key, x => x.Value));
        var m = _missions.First(x => x.Id == start.Mission);
        L.Info($"offline co-op: {start}");
        // your own start army as in single player: a mission with an army budget opens the army screen first (seat 1's
        // tribe and the campaign's credits; the AI commanders' seats keep their `army` of the co-op definition)
        CampaignMenu.StartWithArmy(m, start, () => new CoopLobby(offline: true));
    }

    public override void _Process(double delta)
    {
        var l = Lobby;
        if (l == null || ReferenceEquals(l, _shown)) return;
        _shown = l;
        var cfg = GameApp.Config!;
        bool campaign = l.CoopMode == "campaign";
        _filling = true;
        try
        {
            // missions without a co-op definition are greyed out for the co-op campaign; missions not adapted for both
            for (int i = 0; i < _mission.ItemCount; i++)
            {
                int id = _mission.GetItemMetadata(i).AsInt32();
                var why = campaign ? CoopDefinition.CampaignNote(cfg.Id, id) : null;
                bool notYet = CampaignStatus.Of(cfg.Id, id) == MissionState.NotYet, noDef = why != null;
                _mission.SetItemDisabled(i, notYet || noDef);
                _mission.SetItemTooltip(i, notYet ? "Not adapted for the remake yet" : why ?? "");
            }
            var c = l.Campaign;
            if (c != null)
            {
                for (int i = 0; i < _mission.ItemCount; i++) if (_mission.GetItemMetadata(i).AsInt32() == c.Mission) _mission.Select(i);
                for (int i = 0; i < _tribe.ItemCount; i++) if (_tribe.GetItemMetadata(i).AsString() == c.Tribe) _tribe.Select(i);
                _diff.Select(Math.Clamp(c.Difficulty, 0, 3));
                var gpo = c.Gameplay ?? PwRemake.Sim.Rules.GameplayOptions.Default;
                if (_epoch6 != null) _epoch6.ButtonPressed = gpo.EpochSix;
                if (_titans != null) _titans.ButtonPressed = gpo.RemoveTitans;
                if (_flying != null) _flying.ButtonPressed = gpo.FlyingEnabled;
                if (_portals != null) _portals.ButtonPressed = gpo.Portals;
                if (_limits != null) _limits.ButtonPressed = gpo.BuildingLimit;
                var m = _missions.FirstOrDefault(x => x.Id == c.Mission);
                _text.Text = m == null ? $"Mission {c.Mission} is not in this machine's campaign." : $"{m.Title}\n\n{m.Description}";
            }
            _mode.Select(campaign ? 1 : 0);
            _coopDiff.Select(Math.Clamp(l.Coop?.Difficulty ?? 0, 0, 2));
            _rule.Select(l.SceneRule == "host" ? 1 : 0);
            _tribe.Visible = _tribeLabel.Visible = !campaign;
            _coopDiff.Visible = _coopDiffLabel.Visible = campaign;
        }
        finally { _filling = false; }
        foreach (var ch in _players.GetChildren()) { _players.RemoveChild(ch); ch.QueueFree(); }
        if (campaign) Seats(l); else Shared(l);
        var problem = LobbyStart.Problem(l);
        var me = l.Peer(MeId);
        _err.Text = IsHost ? problem ?? "" : (me?.Ready ?? false) ? "Waiting for the host to start." : "";
        _start.Disabled = problem != null;
        _ready.Text = (me?.Ready ?? false) ? "Not ready" : Tx.Get("_ActionView_ButtonReady", "Ready");
    }

    HBoxContainer PeerRow(LobbyPeer p)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        row.AddChild(new Label { Text = p.Name + (p.Host ? " (host)" : ""), CustomMinimumSize = new Vector2(180, 0), Modulate = p.Id == MeId ? PwTheme.Hi : Colors.White });
        if (!_offline)
        {
            row.AddChild(new Label { Text = p.Ready || p.Host ? "GO" : "-", CustomMinimumSize = new Vector2(40, 0), TooltipText = Tx.Get("_UI_PlLiWind_Row_GO_TT", "") });
            row.AddChild(new Label { Text = p.Ping > 0 ? $"{p.Ping} ms" : "", CustomMinimumSize = new Vector2(60, 0) });
        }
        return row;
    }

    /// <summary>Shared mode: everybody on the mission's player</summary>
    void Shared(LobbyState l)
    {
        var main = l.CoopPlayers?.FirstOrDefault(c => c.Main);
        foreach (var p in l.Players)
        {
            var row = PeerRow(p);
            row.AddChild(new Label { Text = $"{main?.Name ?? "the mission's player"} (shared)", CustomMinimumSize = new Vector2(300, 0) });
            if (p.Problem != null) row.AddChild(PwTheme.Para(p.Problem, 12, PwTheme.Lost));
            _players.AddChild(row);
        }
    }

    /// <summary>Co-op campaign mode: the seats, the hero split</summary>
    void Seats(LobbyState l)
    {
        var cfg = GameApp.Config!;
        var c = l.Coop;
        var def = c?.Def();
        if (c == null || def == null) { _players.AddChild(PwTheme.Para("This mission has no co-op campaign definition yet - Shared mode only.", 13, PwTheme.Dim)); return; }
        foreach (var seat in c.Seats)
        {
            int sn = seat.Seat;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.AddChild(new Label { Text = $"Seat {sn}", CustomMinimumSize = new Vector2(60, 0), Modulate = PwTheme.Hi });
            // what the seat is (the host's): a human, an AI commander, empty (seat 3); seat 1 of an offline game is yours
            var kind = new OptionButton { CustomMinimumSize = new Vector2(160, 26), Disabled = !IsHost || (_offline && sn == 1) || (sn == 3 && !def.Supports(3)) };
            var kinds = new List<(string K, string T)>();
            if (!_offline || sn == 1) kinds.Add(("human", "Human"));
            if (sn > 1 || !_offline) kinds.Add(("ai", "AI commander"));
            if (sn == 3) kinds.Add(("empty", "Empty"));
            foreach (var (k, txt) in kinds) { kind.AddItem(txt); kind.SetItemMetadata(kind.ItemCount - 1, k); if (k == seat.Kind) kind.Select(kind.ItemCount - 1); }
            kind.ItemSelected += i => HostSeat(cs => cs.Seats.First(x => x.Seat == sn).Kind = kind.GetItemMetadata((int)i).AsString());
            row.AddChild(kind);
            if (seat.Kind == "empty") { row.AddChild(new Label { Text = "(not played: nothing is placed for it)", Modulate = PwTheme.Dim }); _players.AddChild(row); continue; }
            // who is on it
            var who = seat.Kind == "human" ? l.Players.FirstOrDefault(p => p.Seat == sn) : null;
            if (seat.Kind == "ai") row.AddChild(new Label { Text = "AI commander", CustomMinimumSize = new Vector2(180, 0) });
            else if (who != null) row.AddChild(new Label { Text = who.Name + (who.Host ? " (host)" : "") + (!_offline && (who.Ready || who.Host) ? "  GO" : ""), CustomMinimumSize = new Vector2(180, 0), Modulate = who.Id == MeId ? PwTheme.Hi : Colors.White });
            else if (!_offline && N != null) { var take = PwTheme.Btn("Take this seat", () => N.Client.SetCoopSeat(sn), 180); row.AddChild(take); }
            else row.AddChild(new Label { Text = "open", CustomMinimumSize = new Vector2(180, 0), Modulate = PwTheme.Dim });
            // the faction: a human's own, the host's for an AI commander
            bool mine = who != null && who.Id == MeId;
            var tribe = new OptionButton { CustomMinimumSize = new Vector2(190, 26), Disabled = !(mine || (IsHost && seat.Kind == "ai") || (_offline && IsHost)) };
            tribe.AddItem(sn == 1 ? "As in the mission" : "As seat 1"); tribe.SetItemMetadata(0, "");
            foreach (var t in cfg.PlayableTribes) { tribe.AddItem(TribeInfo.Name(t)); tribe.SetItemMetadata(tribe.ItemCount - 1, t); if (t == seat.Tribe) tribe.Select(tribe.ItemCount - 1); }
            tribe.ItemSelected += i =>
            {
                var tr = tribe.GetItemMetadata((int)i).AsString();
                if (mine && !_offline) N?.Client.SetCoopTribe(tr);
                else HostSeat(cs => cs.Seats.First(x => x.Seat == sn).Tribe = tr);
            };
            row.AddChild(tribe);
            if (seat.Kind == "ai")
            {
                var ag = new OptionButton { CustomMinimumSize = new Vector2(140, 26), Disabled = !IsHost, TooltipText = "The AI commander's aggression at the start (changeable in the game)" };
                foreach (var a in new[] { "Defensive", "Normal", "Aggressive" }) ag.AddItem(a);
                ag.Select(Math.Clamp(seat.Aggression, 0, 2));
                ag.ItemSelected += i => HostSeat(cs => cs.Seats.First(x => x.Seat == sn).Aggression = (int)i);
                row.AddChild(ag);
            }
            _players.AddChild(row);
        }
        // the players without a seat
        foreach (var p in l.Players.Where(p => !c.Seats.Any(x => x.Seat == p.Seat && x.Kind == "human")))
        {
            var row = PeerRow(p);
            row.AddChild(new Label { Text = "no seat - take an open one", Modulate = PwTheme.Lost });
            _players.AddChild(row);
        }
        // the story heroes: who controls whom (the host changes it; only humans)
        if (def.Heroes.Count > 0)
        {
            var split = c.HeroSplit(l.Players);
            var humans = c.InUse.Where(x => x.Kind == "human").Select(x => x.Seat).ToList();
            var hr = new HBoxContainer();
            hr.AddThemeConstantOverride("separation", 10);
            hr.AddChild(new Label { Text = def.NoBuild ? "Heroes (divided evenly)" : "Heroes", CustomMinimumSize = new Vector2(150, 0), Modulate = PwTheme.Hi });
            foreach (var h in def.Heroes)
            {
                hr.AddChild(new Label { Text = HeroName(h) });
                var ob = new OptionButton { CustomMinimumSize = new Vector2(150, 26), Disabled = !IsHost || humans.Count < 2 };
                foreach (var sn in humans)
                {
                    var name = l.Players.FirstOrDefault(p => p.Seat == sn)?.Name ?? $"Seat {sn}";
                    ob.AddItem($"{sn}: {name}"); ob.SetItemMetadata(ob.ItemCount - 1, sn);
                    if (split.GetValueOrDefault(h) == sn) ob.Select(ob.ItemCount - 1);
                }
                var hh = h;
                ob.ItemSelected += i => HostSeat(cs => cs.Heroes[hh] = ob.GetItemMetadata((int)i).AsInt32());
                hr.AddChild(ob);
            }
            _players.AddChild(hr);
        }
        if (def.Notes.Length > 0) _players.AddChild(PwTheme.Para(def.Notes, 12, PwTheme.Dim));
    }
}
