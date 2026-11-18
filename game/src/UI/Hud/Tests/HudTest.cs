// The HUD on a real mission world, without the game session: the mission (1 by default) is built headless from the
// test content (MissionSession with PW_CONTENT, default /home/claude/testcontent), stepped a little, and a fake host
// (FakeHost below: selection, commands into the world, a camera, a fog of war from the player's sight) drives the HUD
// over a plain background. Checks of the logic against values of the JS (pyramid places, minimap relief / colours /
// orientation), of the menus and the tooltips on the real tech tree, of commands that reach the world (a unit queued
// from the produce menu, a stance, a level up by the pyramid), of mouse input pushed into the viewport (a pyramid card
// clicked, the minimap clicked, a flyout card's tooltip) - and screenshots.
//
// Run (rendering on llvmpipe in the container):
//   xvfb-run -a -s "-screen 0 1600x900x24" godot --path game res://src/UI/Hud/Tests/HudTest.tscn -- --shots=<dir>
//     [--mission=<id>] [--config=<content config dir>] [--size=3840x2160] [--scale=<ui scale>] [--prefix=<name>]
// Exit code 0 = all checks passed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.Mission;
using PwRemake.UI.Mission.Logic;
using PwRemake.View.Terrain;

namespace PwRemake.UI.Hud.Tests;

public partial class HudTest : Control
{
    static readonly Logger L = Log.For("UI.Hud.Test");
    int _fails, _checks;
    string _shots = "", _prefix = "hud_";
    MissionSession _s = null!;
    FakeHost _host = null!;
    Hud _hud = null!;

    /// <summary>The session's side as the test plays it.</summary>
    public sealed class FakeHost : IHudHost
    {
        readonly MissionSession _s;
        public FakeHost(MissionSession s, MissionPresenter? mission)
        {
            _s = s; Mission = mission;
            Camera = s.StartCamera;
            var size = s.World.Map.Size;
            FogGrid = new FogGrid(size);
        }
        public World World => _s.World;
        public Player Me => _s.Human!;
        public bool Campaign => true;
        public bool Multiplayer { get; set; }
        public readonly List<Command> Sent = new();
        public void Submit(Command c) { Sent.Add(c); _s.World.Enqueue(c); }
        public List<Entity> Sel = new();
        public IReadOnlyList<Entity> Selection => Sel;
        public int SelectionChanges;
        public Action? OnSelection;
        public void SetSelection(IReadOnlyList<Entity> list, bool silent = false) { Sel = list.ToList(); SelectionChanges++; OnSelection?.Invoke(); }
        public (double X, double Z, double Yaw) Camera { get; set; }
        public void MoveCamera(double x, double z) => Camera = (x, z, Camera.Yaw);
        /// <summary>a trapezoid around the camera's point, its far edge "up" on the minimap (the view's footprint)</summary>
        public IReadOnlyList<(double X, double Z)>? ViewCorners()
        {
            var (cx, cz, yaw) = Camera;
            double fx = -Math.Sin(MinimapModel.SnapDiamond(yaw)), fz = -Math.Cos(MinimapModel.SnapDiamond(yaw)), rx = -fz, rz = fx;
            (double, double) P(double f, double r) => (cx + fx * f + rx * r, cz + fz * f + rz * r);
            return new[] { P(55, -55), P(55, 55), P(-25, 28), P(-25, -28) };
        }
        public readonly FogGrid FogGrid;
        public IFogOfWar? Fog => FogGrid;
        public int FogVersion => FogGrid.Version;
        /// <summary>main.js updateFow: the player's units and finished buildings see</summary>
        public void UpdateFog()
        {
            var v = new List<(double, double, double)>();
            foreach (var u in World.Units) if (u.Alive && !u.Parked && u.Owner == Me) v.Add((u.Pos.X, u.Pos.Z, u.Fow));
            foreach (var b in World.Buildings) if (b.Alive && !b.Parked && b.Built && b.Owner == Me) v.Add((b.Pos.X, b.Pos.Z, b.Fow));
            FogGrid.Update(v);
        }
        public string? InputMode { get; set; }
        public Sim.Rules.ActionDef? ModeAction;
        public void SetInputMode(string? mode, Sim.Rules.ActionDef? a = null) { InputMode = mode; ModeAction = a; }
        public readonly Dictionary<int, List<Entity>> Groups = new();
        public IReadOnlyList<Entity>? Group(int k) => Groups.TryGetValue(k, out var g) ? g : null;
        public string? SelectedGroup { get; set; }
        public void GroupButton(int k, string how) { if (how == "assign") Groups[k] = Sel.ToList(); else if (Groups.TryGetValue(k, out var g)) { Sel = g.ToList(); SelectedGroup = k.ToString(); } }
        public void OrderOn(Entity target, bool shift, bool ctrl) { }
        public MissionPresenter? Mission { get; }
        public (double X, double Z) MapToWorld(double mx, double my) => _s.Campaign.ToGame(mx, my);
        public readonly List<string> Sounds = new();
        public void UiSound(params string[] names) => Sounds.Add(names.FirstOrDefault() ?? "");
        public void ErrorSound() => Sounds.Add("error");
        public void Ordered(string type, IReadOnlyList<Entity> units) => Sounds.Add("ordered:" + type);
        public int MenuOpened, ChatOpened;
        public void OpenMenu() => MenuOpened++;
        public void OpenChat(bool history) => ChatOpened++;
    }

    void Check(bool ok, string what)
    {
        _checks++;
        if (ok) L.Info("ok    " + what);
        else { _fails++; L.Error("FAIL  " + what); }
    }

    public override async void _Ready()
    {
        if (Log.Sink<ConsoleLogSink>() == null) Log.AddSink(new ConsoleLogSink());
        Log.DefaultLevel = LogLevel.Info;
        var args = GameApp.UserArgs();
        _shots = args.GetValueOrDefault("shots", "");
        _prefix = args.GetValueOrDefault("prefix", "hud_");
        if (args.TryGetValue("size", out var size))
        {
            var p = size.Split('x');
            GetWindow().Size = new Vector2I(int.Parse(p[0]), int.Parse(p[1]));
            await Frames(3);
        }
        SetAnchorsPreset(LayoutPreset.FullRect);
        try
        {
            var content = System.Environment.GetEnvironmentVariable("PW_CONTENT") ?? "/home/claude/testcontent";
            var cfg = GameConfiguration.Load(args.GetValueOrDefault("config", System.IO.Path.Combine(content, "configs", "base")))
                ?? throw new InvalidOperationException("no test configuration (PW_CONTENT)");
            var assets = new AssetSource(cfg);
            PwTheme.Init(assets);
            HudArt.Init(assets);
            Background();
            LogicChecks();
            int mission = int.Parse(args.GetValueOrDefault("mission", "1"));
            var start = new GameStart { Configuration = cfg.Id, Mission = mission, Difficulty = 1, Seed = 7 };
            _s = MissionSession.Create(start, cfg, new SessionOptions());
            L.Info($"mission {mission}: {_s.World.Units.Count} units, {_s.World.Buildings.Count} buildings, human {_s.Human}");
            // the mission UI for the quest button and the markers
            MissionPresenter? mp = null;
            try
            {
                var data = MissionUiData.FromJson(_s.Lua.World, _s.Lua.Mission, null);
                foreach (var (k, v) in _s.Lua.Script.Texts) data.Texts[k] = v;
                mp = new MissionPresenter(data, assets);
                AddChild(mp);
            }
            catch (Exception e) { L.Warn("no mission UI: " + e.Message); }
            _host = new FakeHost(_s, mp);
            _hud = new Hud(_host);
            if (args.TryGetValue("scale", out var sc)) _hud.UiScale = double.Parse(sc, System.Globalization.CultureInfo.InvariantCulture);
            AddChild(_hud);
            var table = MinimapModel.LoadTable(assets.Resolve($"assets/terrain/{_s.Map.Setting}/setting.json"));
            Check(table != null, $"minimap colour table of {_s.Map.Setting}");
            _host.UpdateFog();
            _hud.SetMap(MinimapSource.From(_s.Map), table);
            await Run();
        }
        catch (Exception e) { L.Error("test failed", e); _fails++; }
        L.Info($"{_checks - _fails} of {_checks} checks passed");
        GetTree().Quit(_fails == 0 ? 0 : 1);
    }

    void Background()
    {
        // a plain ground-coloured background with a faint pattern (no world view here)
        var bg = new ColorRect { Color = new Color(0.27f, 0.30f, 0.17f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);
    }

    async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    async Task Shot(string name)
    {
        if (_shots.Length == 0) return;
        await Frames(3);
        System.IO.Directory.CreateDirectory(_shots);
        var path = System.IO.Path.Combine(_shots, _prefix + name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        L.Info($"shot {path}");
    }

    /// <summary>steps the world (game seconds) and keeps the HUD fed: fog, events</summary>
    void Step(double seconds)
    {
        int n = (int)Math.Round(seconds / World.STEP);
        for (int i = 0; i < n; i++) _s.Step();
        _host.UpdateFog();
        _hud.OnEvents(_s.World.DrainEvents());
    }

    void Select(params Entity[] list) { _host.SetSelection(list); _hud.SelectionChanged(); _hud.RefreshAll(); }

    // ------------------------------------------------------------------ the logic against the JS
    void LogicChecks()
    {
        // the pyramid's places (hud.js slotRects; node on remake/src/ui/hud.js's code)
        string R(List<PyrRect> l) => string.Join(",", l.Select(r => $"[{r.X},{r.Y},{r.W},{r.H}]"));
        var a = HudModel.SlotRects(2, 12, out bool c2);
        Check(c2 && R(a) == "[8,159,27,27],[41,159,27,27],[74,159,27,27],[107,159,27,27],[140,159,27,27],[8,195,27,27],[41,195,27,27],[74,195,27,27],[107,195,27,27],[140,195,27,27],[184,159,27,27],[184,195,27,27]", "slotRects(2, 12) as the JS");
        var b = HudModel.SlotRects(3, 8, out bool c3);
        Check(c3 && R(b) == "[8,98,27,27],[41,98,27,27],[74,98,27,27],[107,98,27,27],[140,98,27,27],[184,98,27,27],[216,98,27,27],[248,98,27,27]", "slotRects(3, 8) as the JS");
        var d = HudModel.SlotRects(0, 30, out bool c0);
        Check(!c0 && d.Count == 30 && d[25] == new PyrRect(184, 362, 27, 27) && d[29] == new PyrRect(184, 507, 27, 27), "slotRects(0, 30) as the JS");
        var e = HudModel.SlotRects(4, 14, out _);
        Check(e[5] == new PyrRect(8, 44, 27, 27) && e[13] == new PyrRect(216, 44, 27, 27), "slotRects(4, 14) as the JS");
        // the minimap (minimap.js reliefCells, snapDiamond, paintCells)
        var col = MinimapModel.ReliefCells(6, 5, (i, j) => Math.Sin(i * 1.3) * 4 + j * j * 0.7 - 2, 0.5);
        Check(string.Join(",", col) == "11,1,10,11,11,11,11,1,3,11,11,11,0,0,3,11,11,0,0,0,1,1,0,0,0,0,3,3,0,0", "reliefCells as the JS");
        Check(Math.Abs(MinimapModel.SnapDiamond(2.2) - 2.356194490192345) < 1e-12 && Math.Abs(MinimapModel.SnapDiamond(-0.9) + 0.7853981633974483) < 1e-12
            && Math.Abs(MinimapModel.SnapDiamond(1) - 0.7853981633974483) < 1e-12, "snapDiamond as the JS");
        var table = Enumerable.Range(0, 6).Select(r => Enumerable.Range(0, 21).Select(cc => new[] { (r * 40 + cc * 11) % 256, (cc * 23) % 256, (r * 7 + cc * 5) % 256, cc % 3 == 0 ? 136 : 255 }).ToArray()).ToArray();
        var data = new byte[col.Length * 4]; var forest = new byte[col.Length]; forest[3] = 1; forest[7] = 1;
        MinimapModel.PaintCells(data, col, forest, table, k => k % 3);
        Check(string.Join(",", data.Take(48)) == "0,0,0,0,131,23,26,255,110,230,50,255,0,0,0,0,241,253,76,255,121,253,55,255,0,0,0,0,196,21,55,255,33,69,15,136,0,0,0,0,241,253,76,255,121,253,55,255", "paintCells as the JS");
    }

    // ------------------------------------------------------------------ the HUD on the mission
    async Task Run()
    {
        var W = _s.World; var me = _host.Me;
        Step(10);
        // a richer army for the screenshots: resources and skulls, a few units of each level
        me.Res["food"] = 850; me.Res["wood"] = 420; me.Res["stone"] = 190; me.Res["skulls"] = 120;
        var tribe = me.Tribe.ToLowerInvariant();
        var home = W.Buildings.FirstOrDefault(x => x.Alive && x.Owner == me) as Entity ?? W.Units.First(u => u.Alive && u.Owner == me);
        // (mission 1 starts without a building: a main building for the produce menu)
        if (!W.Buildings.Any(x => x.Alive && x.Owner == me)) W.PlaceBuilding($"{tribe}_fireplace", me, home.Pos.X - 14, home.Pos.Z - 6);
        foreach (var (cls, lv) in new[] { ($"{tribe}_warrior", 1), ($"{tribe}_warrior", 2), ($"{tribe}_warrior", 3), ($"{tribe}_worker", 1), ($"{tribe}_worker", 1) })
            W.SpawnUnit(cls, me, home.Pos.X + 12 + W.Rng.Next() * 8, home.Pos.Z + 8 + W.Rng.Next() * 8, lv);
        Step(3);
        // (the units of the test are more than the mission's start allows; a site under construction for the info window)
        me.MaxUnits = 30;
        // the surroundings explored once (a sight of 260 m around the start), then the player's own sight
        _host.FogGrid.Update(new[] { (home.Pos.X, home.Pos.Z, 260.0) });
        _host.UpdateFog();
        _hud.RefreshAll();
        await Frames(3);
        var mine = W.Units.Where(u => u.Alive && u.Owner == me).ToList();
        L.Info("own units: " + string.Join(", ", mine.Select(u => $"{u.Name} L{u.Level}")));
        var worker = mine.FirstOrDefault(u => u.IsWorker && u.CanBuild);
        Check(worker != null, "the mission has a worker");

        // ---- 1: a worker: the build menu opens by itself (the original's Economy / Military / Defence rows)
        if (worker != null)
        {
            Select(worker);
            Check(_hud.Model.Menu == "build", "a worker opens the build menu");
            var rows = _hud.Model.MenuRows();
            L.Info("build rows: " + string.Join(" | ", rows.Select(r => r.Title + ": " + string.Join(" ", r.Cards.Select(c => c.Id)))));
            Check(rows.Count >= 2 && rows[0].Title == "Economy", "build rows (Economy first)");
            // the tooltip of the first card (the original's window above the minimap)
            var card = rows[0].Cards[0];
            var tip = Tooltips.ActionTip(W, me, card.A!, _hud.Model.G.Why(card, _hud.Model.G.OwnSelection()), _hud.Model.G.OwnSelection());
            L.Info("tooltip:\n" + tip);
            Check(!string.IsNullOrEmpty(tip.Title) && tip.Cost != null, "a build card's tooltip has a title and costs");
            _hud.TipNear = false;
            _hud.ShowTip(tip, null);
            _hud.Message("Build up your settlement", "");
            _hud.Message("Epoch II reached!", "good");
            await Shot("worker_build");
            _hud.HideTip();
            _hud.TipNear = true;
        }

        // ---- 2: the main building: the produce menu, a unit queued from it through a command
        var main = W.Buildings.FirstOrDefault(x => x.Alive && x.Owner == me && x.Built && _hud.Model.G.ProducerActions(x).Any(a => a.Kind == "Build"));
        Check(main != null, "a building that produces");
        if (main != null)
        {
            Select(main);
            Check(_hud.Model.Menu == "produce", "a producer opens the produce menu");
            var rows = _hud.Model.MenuRows();
            L.Info("produce rows: " + string.Join(" | ", rows.Select(r => r.Title + ": " + string.Join(" ", r.Cards.Select(c => c.Id)))));
            var unitCard = rows.SelectMany(r => r.Cards).FirstOrDefault(c => c.A!.Kind == "Build" && _hud.Model.G.Why(c, new List<Entity> { main }) == null);
            Check(unitCard != null, "a unit that can be trained now (" + string.Join(", ", rows.SelectMany(r => r.Cards).Select(c => c.Id + ":" + (_hud.Model.G.Why(c, new List<Entity> { main }) ?? "ok"))) + ")");
            if (unitCard != null)
            {
                int q0 = main.Queue.Count;
                _hud.Model.G.UseAction(unitCard, new List<Entity> { main }, false);
                Step(0.1);
                Check(main.Queue.Count == q0 + 1, $"the produce card queued {unitCard.Id} through a command ({main.Queue.Count})");
                _hud.Model.G.UseAction(unitCard, new List<Entity> { main }, false);
                Step(2);
                _hud.RefreshAll();
                await Frames(2);
                // a card's tooltip by the mouse: pushed into the viewport over the flyout's first card
                var flyCard = FindHit<HudFlyout>(h => h.Id.Contains(unitCard.Id));
                if (flyCard != null) { Move(flyCard.Value); await Frames(2); }
                Check(_hud.CurrentTip != null && !string.IsNullOrEmpty(_hud.CurrentTip.Title), "the mouse over a card shows its tooltip");
                // the keyboard: the produce menu's bottom row armed (its digits on the cards)
                Check(_hud.ArmRow("produce", 0), "a row of the produce menu armed by key");
                await Frames(2);
                await Shot("building_produce");
            }
        }

        // ---- 3: a group of soldiers: the group in the info window, the stances menu; a stance by command
        var soldiers = mine.Where(u => !u.IsWorker && u.Cls == "CHTR").Take(6).ToList();
        if (soldiers.Count >= 2)
        {
            Select(soldiers.ToArray());
            _hud.Model.ToggleMenu("stances");
            _hud.RefreshCommands();
            _hud.Model.G.SetStance(soldiers, 2);
            Step(0.1);
            Check(soldiers.All(u => u.Stance == 2), "the stance card reached the units (SetStanceCommand)");
            _hud.RefreshAll();
            // the minimap: a ping and an attack marker
            _hud.Minimap?.AddMarker("Attack", soldiers[0].Pos.X + 30, soldiers[0].Pos.Z - 20);
            _hud.MapPing(home.Pos.X - 40, home.Pos.Z + 30);
            await Frames(2);
            await Shot("group_stances");
        }

        // ---- 4: the pyramid: a card clicked with the mouse selects the unit; a level up by dragging (ChangeLevel)
        var lv1 = mine.FirstOrDefault(u => u.Level == 1 && !u.IsWorker && u.Cls == "CHTR") ?? mine.FirstOrDefault(u => u.Level == 1);
        if (lv1 != null)
        {
            _host.SetSelection(Array.Empty<Entity>());
            _hud.RefreshAll();
            await Frames(2);
            var card = FindHit<HudPyramid>(h => h.Id == "card" + lv1.Id);
            Check(card != null, "the unit's card is in the pyramid");
            if (card != null)
            {
                await Click(card.Value);
                Check(_host.Sel.Count == 1 && _host.Sel[0] == lv1, "a click on a pyramid card selects the unit");
            }
            int before = lv1.Level;
            _hud.Model.G.ChangeLevel(lv1, 2);
            Step(0.1);
            Check(lv1.Level == 2 || W.CommandResults.Any(r => r.Command is ChangeLevelCommand), $"the level up by the pyramid reached the world (level {before} -> {lv1.Level})");
            _hud.RefreshAll();
            await Frames(2);
            // the minimap: a click moves the camera there
            var mm = FindPanel<HudMinimap>();
            var cam0 = _host.Camera;
            var center = mm.GetGlobalTransformWithCanvas() * new Vector2(145, 147 - 60);
            await ClickAt(center);
            Check(Math.Abs(_host.Camera.X - cam0.X) + Math.Abs(_host.Camera.Z - cam0.Z) > 1, $"a minimap click moved the camera ({cam0.X:0},{cam0.Z:0} -> {_host.Camera.X:0},{_host.Camera.Z:0})");
            Check(_hud.IsOverHud(FindPanel<HudMinimap>().GetGlobalTransformWithCanvas() * new Vector2(140, 140)) && !_hud.IsOverHud(GetViewport().GetVisibleRect().Size / 2), "IsOverHud: the minimap yes, the middle of the screen no");
            await Shot("pyramid_selected");
        }

        // ---- 5: a building site (construction progress), a resource
        var site = W.PlaceBuilding($"{tribe}_stone_cottage", me, home.Pos.X + 20, home.Pos.Z - 25, 0, false);
        if (site != null)
        {
            site.Progress = 0.37;
            Select(site);
            var info = _hud.Model.Info();
            Check(info.Progress != null && Math.Abs(info.Progress.Value - 0.37) < 1e-9, "a building site's progress in the info window");
            await Shot("construction");
        }
        var res = W.Resources.FirstOrDefault(r => r.Alive && r.Type != "tree" && r.Amount > 0) ?? W.Resources.FirstOrDefault(r => r.Alive);
        if (res != null)
        {
            Select(res);
            var info = _hud.Model.Info();
            Check(info.Kind == "res" && info.Name.Length > 0, $"a resource in the info window ({info.Name} {info.Amount})");
            await Shot("resource");
        }
        // ---- 6: the minimap zoomed, the pyramid closed, the epoch bar
        if (_hud.Minimap != null) { _hud.Minimap.PressButton("zoomin"); _hud.Minimap.PressButton("zoomin"); }
        _hud.SetPyramidOpen(false);
        if (worker != null) Select(worker);
        await Frames(2);
        await Shot("minimap_zoom");
        _hud.SetPyramidOpen(true);
        // ---- 7: the statistics screen
        Step(15);
        _hud.ShowStats(null, _s.Engine?.Quests);
        await Frames(4);
        await Shot("statistics");
        // ---- the messages of the world's events
        Check(_hud.Model.G != null, "done");
    }

    // ------------------------------------------------------------------ mouse input through the viewport
    T FindPanel<T>() where T : Control
    {
        static IEnumerable<Node> All(Node n) { foreach (var c in n.GetChildren()) { yield return c; foreach (var d in All(c)) yield return d; } }
        return All(_hud).OfType<T>().First();
    }
    Vector2? FindHit<T>(Func<Hit, bool> f) where T : HudPanel
    {
        var p = FindPanel<T>();
        var hits = (List<Hit>)typeof(HudPanel).GetField("Hits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(p)!;
        var h = hits.FirstOrDefault(f);
        return h == null ? null : p.GetGlobalTransformWithCanvas() * h.R.GetCenter();
    }
    void Move(Vector2 at)
    {
        var e = new InputEventMouseMotion { Position = at, GlobalPosition = at };
        GetViewport().PushInput(e);
    }
    async Task Click(Vector2 at)
    {
        Move(at);
        await Frames(1);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        await Frames(1);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false });
        await Frames(2);
    }
    Task ClickAt(Vector2 at) => Click(at);
}
