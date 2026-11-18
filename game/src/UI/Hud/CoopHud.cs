// The co-op campaign's panel in the game (docs/COOP.md "In the game"), at the right edge below the quest notices:
//   * the seats: name, faction (the tribe's original icon), whose heroes;
//   * every AI commander: what it does now, its commands - Attack / Defend (then a click on the world or the minimap:
//     the input's callback mode), Follow (the selected units), Economy, Retreat, Clear - and its aggression;
//   * with two AI commanders, "Both AI commanders": the same buttons once more, each press one CoopCommand to both
//     seats (CoopCommand.Seats) - the same picking (world or minimap click), the same answers;
//   * "Give selected to": the selected units to another seat (heroes only to humans; buildings stay).
// Resources go to a seat through the diplomacy window's tribute (the top bar's Diplomacy button lists the seats).
// It reads the simulation (CoopRuntime) and sends commands (CoopCommand, CoopGiveCommand) through the session.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Coop;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud;

public partial class CoopHud : CanvasLayer
{
    readonly GameSession G;
    readonly CoopRuntime R;
    public PanelContainer Panel { get; private set; } = null!;
    VBoxContainer _rows = null!;
    string _key = "";
    double _t;

    public CoopHud(GameSession g, CoopRuntime r) { G = g; R = r; Layer = 11; }

    static readonly Color Gold = new(0.63f, 0.48f, 0.25f);
    static StyleBoxFlat Box() => new()
    {
        BgColor = new Color(0.10f, 0.07f, 0.04f, 0.82f), BorderColor = Gold, BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
        CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6,
    };

    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = PwTheme.Theme };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        Panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, Name = "CoopPanel" };
        Panel.AddThemeStyleboxOverride("panel", Box());
        Panel.Position = new Vector2(8, 110);
        root.AddChild(Panel);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        Panel.AddChild(col);
        var head = new Label { Text = "Co-op" };
        head.AddThemeColorOverride("font_color", PwTheme.Hi);
        head.AddThemeFontOverride("font", HudSprites.SerifBold);
        head.AddThemeFontSizeOverride("font_size", 15);
        col.AddChild(head);
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 3);
        col.AddChild(_rows);
    }

    Player Me => G.Me;
    CoopSeatRun? MySeat => R.SeatOf(Me);

    /// <summary>what the panel shows: rebuilt when it changes (twice a second)</summary>
    string Key()
    {
        var sel = G.Selection.Items.Where(e => e.Alive && e.Owner == Me).ToList();
        return string.Join("|", R.Seats.Select(s => $"{s.Seat}:{s.Player.Name}:{s.Commander?.ToString() ?? ""}:{G.World.Units.Count(u => u.Alive && u.Owner == s.Player && u.Def?.Unique == true)}"))
               + $"|sel {sel.Count} {sel.Any(e => e is Unit { Def.Unique: true })}";
    }

    public override void _Process(double delta)
    {
        // at the right edge below the quest notices (the HUD's left column and the minimap stay free)
        var vp = GetViewport().GetVisibleRect().Size;
        Panel.Position = new Vector2(vp.X - Panel.Size.X - 8, 110);
        _t -= delta;
        if (_t > 0) return;
        _t = 0.5;
        var k = Key();
        if (k == _key) return;
        _key = k;
        Rebuild();
    }

    static Label Small(string text, Color? c = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 12);
        if (c != null) l.AddThemeColorOverride("font_color", c.Value);
        return l;
    }
    static Button SmallBtn(string text, string tip, Action a)
    {
        var b = new Button { Text = text, TooltipText = tip, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 22) };
        b.AddThemeFontSizeOverride("font_size", 12);
        b.Pressed += a;
        return b;
    }

    void Rebuild()
    {
        foreach (var c in _rows.GetChildren()) { _rows.RemoveChild(c); c.QueueFree(); }
        var me = MySeat;
        var sel = G.Selection.Items.OfType<Unit>().Where(u => u.Alive && u.Owner == Me).ToList();
        bool heroSel = sel.Any(u => u.Def?.Unique == true);
        foreach (var s in R.Seats)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            if (TribeInfo.Icon(s.Player.Tribe) is { } icon) row.AddChild(new TextureRect { Texture = icon, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(20, 20) });
            var heroes = G.World.Units.Where(u => u.Alive && u.Owner == s.Player && u.Def?.Unique == true).Select(u => CoopLobby.HeroName(u.Name)).ToList();
            row.AddChild(Small($"{s.Seat}. {s.Player.Name}{(s == me ? " (you)" : "")}{(heroes.Count > 0 ? " - " + string.Join(", ", heroes) : "")}", HudSprites.Rgb(s.Player.Color)));
            _rows.AddChild(row);
            if (s.Commander is { } cmd) _rows.AddChild(CommanderRow(s, cmd));
        }
        // every AI commander at once (two of them: seats 2 and 3)
        var ais = R.Seats.Where(x => x.Commander != null).ToList();
        if (ais.Count >= 2 && me != null && !me.Ai) _rows.AddChild(AllRow(ais));
        // give the selected units to a seat
        if (me != null && !me.Ai && sel.Count > 0)
        {
            var give = new HBoxContainer();
            give.AddThemeConstantOverride("separation", 4);
            give.AddChild(Small($"Give {sel.Count} to"));
            foreach (var s in R.Seats.Where(x => x != me))
            {
                var to = s.Seat;
                var b = SmallBtn(s.Seat.ToString(), $"Give the selected units to {s.Player.Name}{(s.Ai && heroSel ? " (heroes stay: an AI commander gets none)" : "")}", () =>
                    G.Submit(new CoopGiveCommand { Units = G.Selection.Items.OfType<Unit>().Where(u => u.Alive && u.Owner == Me).Select(u => u.Id).ToArray(), ToSeat = to },
                        why => { if (why != null) G.Message(why == "hero" ? "Heroes go to human players only" : why == "building" ? "Buildings stay with their owner" : G.Reason(why), "bad"); }));
                give.AddChild(b);
            }
            _rows.AddChild(give);
        }
    }

    Control CommanderRow(CoopSeatRun s, CoopCommander cmd)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        box.AddChild(Small($"   AI commander: {What(cmd)}", PwTheme.Dim));
        var me = MySeat;
        if (me == null || me.Ai) return box;
        Orders(box, new[] { s }, cmd.Aggression);
        return box;
    }

    static string What(CoopCommander cmd) => cmd.Order switch
    {
        "attack" => cmd.Reached ? "holds the attacked point" : "attacks",
        "defend" => "defends a point", "follow" => "escorts", "economy" => "builds up its economy", "retreat" => "retreats",
        _ => cmd.Joined != null ? "joins your attack" : "defends its base",
    };

    /// <summary>What, said of them all ("both defend their bases")</summary>
    static string WhatAll(CoopCommander cmd) => cmd.Order switch
    {
        "attack" => cmd.Reached ? "hold the attacked point" : "attack",
        "defend" => "defend a point", "follow" => "escort", "economy" => "build up their economies", "retreat" => "retreat",
        _ => cmd.Joined != null ? "join your attack" : "defend their bases",
    };

    /// <summary>"Both AI commanders": the commands once for all of them (one CoopCommand with Seats)</summary>
    Control AllRow(List<CoopSeatRun> ais)
    {
        var box = new VBoxContainer { Name = "CoopAllCommanders" };
        box.AddThemeConstantOverride("separation", 2);
        box.AddChild(new HSeparator());
        var cmds = ais.Select(a => a.Commander!).ToList();
        var head = Small(ais.Count == 2 ? "Both AI commanders" : "All AI commanders", PwTheme.Hi);
        head.AddThemeFontOverride("font", HudSprites.SerifBold);
        box.AddChild(head);
        var whats = cmds.Select(WhatAll).Distinct().ToList();
        if (whats.Count == 1) box.AddChild(Small($"   {(ais.Count == 2 ? "both" : "all")} {whats[0]}", PwTheme.Dim));
        int ag = cmds.Select(c => c.Aggression).Distinct().Count() == 1 ? cmds[0].Aggression : -1;
        Orders(box, ais, ag);
        return box;
    }

    /// <summary>the command buttons for one AI commander or several (one command to all: CoopCommand.Seats); aggression
    /// -1 = theirs differ</summary>
    void Orders(VBoxContainer box, IReadOnlyList<CoopSeatRun> to, int aggression)
    {
        var seats = to.Select(x => x.Seat).ToArray();
        bool all = seats.Length > 1;
        string them = all ? (seats.Length == 2 ? "both AI commanders" : "every AI commander") : to[0].Player.Name;
        void Send(string kind, double? x = null, double? z = null, int target = 0, int[]? units = null, int value = 0) =>
            G.Submit(new CoopCommand
            {
                Seat = seats[0], Seats = all ? seats : Array.Empty<int>(), Kind = kind, HasPoint = x != null, X = x ?? 0, Z = z ?? 0, Target = target,
                Units = units ?? Array.Empty<int>(), Value = value,
            }, why => { if (why != null) G.Message(why == "target" ? "Nothing to do that with" : G.Reason(why), "bad"); });
        void Pick(string kind, string prompt)
        {
            G.Message(prompt);
            G.Input.SetMode("callback", new Action<double, double, Entity?>((x, z, e) =>
            {
                // an enemy of (any of) them clicked: attack that object; else the point (the world or the minimap)
                if (kind == "attack" && e != null && e.Alive && e.Owner != null && to.Any(s => s.Player.IsEnemy(e.Owner))) Send(kind, null, null, e.Id);
                else Send(kind, x, z);
                G.PlayUi("UI_click");
            }));
        }
        string pre = all ? (seats.Length == 2 ? "Both: " : "All: ") : "";
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 3);
        var row2 = new HBoxContainer();
        row2.AddThemeConstantOverride("separation", 3);
        row.AddChild(SmallBtn("Attack", $"Attack a point or an enemy with {them}: click it in the world or on the minimap", () => Pick("attack", pre + "Attack: click the target")));
        row.AddChild(SmallBtn("Defend", $"Defend a point with {them}: click it in the world or on the minimap", () => Pick("defend", pre + "Defend: click the point")));
        row.AddChild(SmallBtn("Follow", $"Escort the selected units with {them}", () =>
        {
            var ids = G.Selection.Items.Where(e => e.Alive && e is Unit).Select(e => e.Id).ToArray();
            if (ids.Length == 0) { G.Message("Select the units to escort first", "bad"); return; }
            Send("follow", units: ids);
        }));
        row2.AddChild(SmallBtn("Economy", "Gather and expand; the army stays home", () => Send("economy")));
        row2.AddChild(SmallBtn("Retreat", all ? "Back to their bases" : "Back to its base", () => Send("retreat")));
        row2.AddChild(SmallBtn("Clear", "No command: it defends, expands and joins your attacks", () => Send("none")));
        var ag = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = all ? $"Aggression of {them}" : "Aggression" };
        ag.AddThemeFontSizeOverride("font_size", 12);
        foreach (var a in new[] { "Defensive", "Normal", "Aggressive" }) ag.AddItem(a);
        ag.Select(aggression);
        if (aggression < 0) ag.Text = "Mixed";
        ag.ItemSelected += i => Send("aggression", value: (int)i);
        row2.AddChild(ag);
        box.AddChild(row);
        box.AddChild(row2);
    }
}
