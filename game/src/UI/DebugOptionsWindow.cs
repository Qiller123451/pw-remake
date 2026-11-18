// The "Debug options" window of the campaign menu: what the next game starts with (DebugOptions, docs/DEBUG.md). The
// choices are kept in settings.json ("debug") and apply while "Use the debug options" is ticked; any change ticks it.
// Ported from remake/src/ui/debugopts.js (pw-exporter 98c7703).
using System;
using System.Globalization;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Game;

namespace PwRemake.UI;

public partial class DebugOptionsWindow : MenuScreen
{
    static readonly int[] StdPyramid = { 25, 15, 8, 3, 1 };
    readonly Func<Node> _back;
    DebugOptions _c = null!;
    Label _sum = null!;
    bool _filling;
    readonly System.Collections.Generic.List<Action> _fill = new();

    public DebugOptionsWindow(Func<Node> back) : base("Debug options", minWidth: 940) { _back = back; }

    protected override void Build()
    {
        _c = GameApp.Settings.Debug.Normalized();
        var b = Window.Body;
        b.AddChild(PwTheme.Heading("Debug options", 20));
        b.AddChild(Check("Use the debug options in the next game", () => _c.On, v => _c.On = v, "Off: the game is played as normal; the choices below are kept", isOn: true));
        _sum = PwTheme.Para("", 12);
        b.AddChild(_sum);

        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 28);
        b.AddChild(cols);
        var left = new VBoxContainer(); var right = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 8); right.AddThemeConstantOverride("separation", 8);
        cols.AddChild(left); cols.AddChild(right);

        var game = Group("Game");
        left.AddChild(game);
        game.AddChild(Check("Everything free and instant (you)", () => _c.Free, v => _c.Free = v,
            "Buildings stand, units are trained and upgrades researched at once, nothing costs anything, level-ups need no skulls"));
        game.AddChild(Check("Whole map visible (no fog of war)", () => _c.Reveal, v => _c.Reveal = v));
        game.AddChild(Check("Ignore the mission's bans (every unit, building and upgrade of your tribe)", () => _c.Unlock, v => _c.Unlock = v));
        game.AddChild(Line("Computer players", Select(new[] { ("attack as usual", "normal"), ("do not attack you", "passive") }, () => _c.Ai, v => _c.Ai = v)));
        game.AddChild(PwTheme.Para("Passive computer players still defend themselves; a mission's scripted attacks still come.", 11));

        var eco = Group("Your start and economy");
        left.AddChild(eco);
        eco.AddChild(Line("Food", IntField(() => _c.Res.Food, v => _c.Res.Food = v, "game")));
        eco.AddChild(Line("Wood", IntField(() => _c.Res.Wood, v => _c.Res.Wood = v, "game")));
        eco.AddChild(Line("Stone", IntField(() => _c.Res.Stone, v => _c.Res.Stone = v, "game")));
        eco.AddChild(Line("Skulls (level-ups)", IntField(() => _c.Res.Skulls, v => _c.Res.Skulls = v, "game")));
        eco.AddChild(Line("Storage limit per resource", IntField(() => _c.Storage, v => _c.Storage = v, "buildings")));
        eco.AddChild(Line("Gathering ×", NumField(() => _c.Gather, v => _c.Gather = v)));
        eco.AddChild(Line("Construction, training, research speed ×", NumField(() => _c.Speed, v => _c.Speed = v)));

        var army = Group("Your army");
        right.AddChild(army);
        army.AddChild(Line("Unit limit (population)", IntField(() => _c.PopMax, v => _c.PopMax = v, "map")));
        army.AddChild(Check("No houses needed (the unit limit is all yours from the start)", () => _c.NoHousing, v => _c.NoHousing = v));
        army.AddChild(PwTheme.Para("Places in the army pyramid per level (empty: the game's or the mission's)", 12));
        var pyr = new HBoxContainer();
        for (int lv = 5; lv >= 1; lv--)
        {
            int i = lv - 1;
            pyr.AddChild(new Label { Text = $"L{lv}" });
            var f = IntField(() => _c.Pyramid[i], v => _c.Pyramid[i] = v, StdPyramid[i].ToString());
            f.CustomMinimumSize = new Vector2(52, 26);
            pyr.AddChild(f);
        }
        army.AddChild(pyr);

        var ep = Group("Epochs and fighting");
        right.AddChild(ep);
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 12);
        ep.AddChild(grid);
        grid.AddChild(new Label()); grid.AddChild(new Label { Text = "You" }); grid.AddChild(new Label { Text = "Enemies" });
        var epochs = new[] { ("as the game", "0"), ("II", "2"), ("III", "3"), ("IV", "4"), ("V", "5") };
        var maxEp = new[] { ("no limit", "0"), ("I", "1"), ("II", "2"), ("III", "3"), ("IV", "4"), ("V", "5") };
        grid.AddChild(new Label { Text = "Start in epoch" });
        grid.AddChild(Select(epochs, () => _c.Me.StartEpoch.ToString(), v => _c.Me.StartEpoch = int.Parse(v)));
        grid.AddChild(Select(epochs, () => _c.Foe.StartEpoch.ToString(), v => _c.Foe.StartEpoch = int.Parse(v)));
        grid.AddChild(new Label { Text = "Highest epoch" });
        grid.AddChild(Select(maxEp, () => _c.Me.MaxEpoch.ToString(), v => _c.Me.MaxEpoch = int.Parse(v)));
        grid.AddChild(Select(maxEp, () => _c.Foe.MaxEpoch.ToString(), v => _c.Foe.MaxEpoch = int.Parse(v)));
        grid.AddChild(new Label { Text = "Damage dealt ×", TooltipText = "0: no damage at all" });
        grid.AddChild(NumField(() => _c.Me.Dmg, v => _c.Me.Dmg = v));
        grid.AddChild(NumField(() => _c.Foe.Dmg, v => _c.Foe.Dmg = v));
        grid.AddChild(new Label { Text = "Defence ×", TooltipText = "2: half the damage taken" });
        grid.AddChild(NumField(() => _c.Me.Def, v => _c.Me.Def = v));
        grid.AddChild(NumField(() => _c.Foe.Def, v => _c.Foe.Def = v));
        ep.AddChild(PwTheme.Para("Enemies: the players hostile to you and the wild animals. Damage 0 = no damage dealt; defence 2 = half the damage taken.", 11));
        // the original's first-shot delay (World.FirstShot): every player's ranged characters, not only yours
        ep.AddChild(Check("First-shot delay (the original's: a ranged unit's first shot after raising its weapon)", () => _c.FirstShot, v => _c.FirstShot = v,
            "The archers, marksmen, gunners, flamethrowers and the ranged heroes shoot their first shot 0.4 s later (Béla 0.6 s) when they raise the weapon - the time to the next shot too. For every player."));
        ep.AddChild(Line("First-shot delay in seconds (every unit)", OptNumField(() => _c.FirstShotDelay, v => _c.FirstShotDelay = v, "0.4 / Béla 0.6")));

        b.AddChild(Row(
            PwTheme.Btn("Defaults", () => { bool on = _c.On; _c = new DebugOptions { On = on }; Save(); Fill(); }, 140),
            PwTheme.Btn("Done", () => { Save(); Boot.Instance.Show(_back()); }, 140)));
        Fill();
    }

    void Changed(bool isOn)
    {
        if (_filling) return;
        if (!isOn) _c.On = true;           // a change turns the options on (what else would it be for)
        _c = _c.Normalized();
        Save();
        Fill();
    }

    void Save()
    {
        GameApp.Settings.Debug = _c;
        GameApp.Settings.UseDebug = _c.On;
        GameApp.Settings.Save();
    }

    void Fill()
    {
        _filling = true;
        foreach (var f in _fill) f();
        var s = _c.Summary();
        _sum.Text = _c.On ? (s.Count > 0 ? string.Join(" · ", s) : "Nothing is changed yet.") : "Off: the next game is played as normal.";
        _filling = false;
    }

    CheckBox Check(string text, Func<bool> get, Action<bool> set, string tip = "", bool isOn = false)
    {
        var cb = new CheckBox { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None };
        cb.Toggled += v => { if (_filling) return; set(v); Changed(isOn); };
        _fill.Add(() => cb.ButtonPressed = get());
        return cb;
    }

    OptionButton Select((string text, string value)[] items, Func<string> get, Action<string> set)
    {
        var ob = new OptionButton { CustomMinimumSize = new Vector2(150, 26) };
        foreach (var (t, v) in items) { ob.AddItem(t); ob.SetItemMetadata(ob.ItemCount - 1, v); }
        ob.ItemSelected += i => { if (_filling) return; set(ob.GetItemMetadata((int)i).AsString()); Changed(false); };
        _fill.Add(() =>
        {
            var v = get();
            for (int i = 0; i < ob.ItemCount; i++) if (ob.GetItemMetadata(i).AsString() == v) ob.Select(i);
        });
        return ob;
    }

    LineEdit IntField(Func<int?> get, Action<int?> set, string placeholder)
    {
        var e = new LineEdit { PlaceholderText = placeholder, CustomMinimumSize = new Vector2(90, 26) };
        void Commit(string t) { if (_filling) return; t = t.Trim(); set(t == "" ? null : int.TryParse(t, out var n) ? n : null); Changed(false); }
        e.TextSubmitted += Commit;
        e.FocusExited += () => Commit(e.Text);
        _fill.Add(() => { var v = get(); e.Text = v?.ToString() ?? ""; });
        return e;
    }

    LineEdit NumField(Func<double> get, Action<double> set)
    {
        var e = new LineEdit { PlaceholderText = "1", CustomMinimumSize = new Vector2(74, 26) };
        void Commit(string t)
        {
            if (_filling) return;
            t = t.Trim();
            set(t == "" ? 1 : double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 1);
            Changed(false);
        }
        e.TextSubmitted += Commit;
        e.FocusExited += () => Commit(e.Text);
        _fill.Add(() => { var v = get(); e.Text = v == 1 ? "" : v.ToString(CultureInfo.InvariantCulture); });
        return e;
    }

    LineEdit OptNumField(Func<double?> get, Action<double?> set, string placeholder)
    {
        var e = new LineEdit { PlaceholderText = placeholder, CustomMinimumSize = new Vector2(120, 26) };
        void Commit(string t)
        {
            if (_filling) return;
            t = t.Trim().Replace(',', '.');
            set(t == "" ? null : double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null);
            Changed(false);
        }
        e.TextSubmitted += Commit;
        e.FocusExited += () => Commit(e.Text);
        _fill.Add(() => { var v = get(); e.Text = v?.ToString(CultureInfo.InvariantCulture) ?? ""; });
        return e;
    }

    static HBoxContainer Line(string label, Control field)
    {
        var h = new HBoxContainer();
        var l = new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 0) };
        h.AddChild(l);
        h.AddChild(field);
        return h;
    }
}
