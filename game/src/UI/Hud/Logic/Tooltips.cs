// The original's tooltips: what they say (the view, TipWindow.cs, draws them as the original does).
//
// Reference: Game/UI/ToolTipWindow.usl (GenerateMsgScript: the line order and the icons), CommandBarEx/CmdButton.usl
// (Validate: the command bar's buttons), CommandBarEx/CommandBar.usl (the menu and action buttons, the requirement
// lines), Info.usl and PyramidCard.usl (objects). The texts are the encyclopedia's help items (Texts/Help/*.seml,
// gamedata.json "texts"), keyed by the tech tree's description of the action's location or object; the command bar's
// own lines come from the string table (main_uk.ltf, gamedata.json "ui").
//
// A tooltip, top to bottom: the title row with the class icon ("other" for upgrades, moves and buttons), the heroes'
// level rows (5 down to 1), the help text, the requirement lines, the row of strengths against infantry, animals,
// vehicles and buildings, and the cost row (skulls, food, wood, stone; light red what the player can't afford). No
// durations, cooldowns or keys: the original shows none. A key without a help item shows as it is, under an empty
// title (CmdButton.usl:154).
//
// No Godot types. Ported from remake/src/ui/tooltip.js (pw-exporter 98c7703) - ttHtml became the Tip record.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Hud.Logic;

/// <summary>A tooltip's content (tooltip.js ttHtml's argument o): title, cls, lvl {1..5}, body, lines [[text, bad]],
/// vs {chtr, anml, vhcl, bldg}, cost, short {res: true}.</summary>
public sealed class Tip
{
    public string? Title;
    /// <summary>the class icon: chtr | anml | vhcl | bldg | other (null: other)</summary>
    public string? Cls;
    /// <summary>the heroes' level rows ("1" .. "5")</summary>
    public Dictionary<string, string>? Lvl;
    public string? Body;
    public readonly List<(string Text, bool Bad)> Lines = new();
    public Dictionary<string, string>? Vs;
    public Cost? Cost;
    /// <summary>the resources the player is short of (drawn light red)</summary>
    public readonly HashSet<string> Short = new(StringComparer.Ordinal);
    /// <summary>what F1 opens the help book at while the tip is shown (the original: the hovered command button's
    /// help item, HelpMenu.StartHelpNav): a help item key or a class name</summary>
    public string? HelpKey;

    public Tip Clone()
    {
        var t = new Tip { Title = Title, Cls = Cls, Lvl = Lvl, Body = Body, Vs = Vs, Cost = Cost, HelpKey = HelpKey };
        t.Lines.AddRange(Lines);
        foreach (var s in Short) t.Short.Add(s);
        return t;
    }

    /// <summary>the qualities' icon names (tt_quality_*)</summary>
    /// <summary>(the MIRAGE mod's ToolTipWindow.usl also takes 2 / 1 / -1 / -2)</summary>
    public static readonly Dictionary<string, string> Q = new() { ["++"] = "plusplus", ["+"] = "plus", ["-"] = "minus", ["--"] = "minusminus", ["2"] = "plusplus", ["1"] = "plus", ["-1"] = "minus", ["-2"] = "minusminus" };
    /// <summary>the strength row's columns and the cost row's resources, in the order of the row pictures</summary>
    public static readonly string[] VS_COLS4 = { "chtr", "anml", "vhcl", "bldg" }, VS_COLS5 = { "chtr", "anml", "vhcl", "ship", "bldg" };
    public static readonly string[] COST_COLS = { "skulls", "food", "wood", "stone" };
    /// <summary>x of the qualities on tt_bgimage_value (ToolTipWindow.usl: tabs 23, 72, 121, 169) and of the numbers on
    /// tt_bgimage_stats (194 px rows)</summary>
    public static readonly int[] VS_X4 = { 23, 72, 121, 169 }, VS_X5 = { 19, 59, 97, 137, 175 }, COST_X = { 19, 68, 117, 165 };
    /// <summary>The MIRAGE mod's strength row has five icons (its hud_tooltip_bgimages: characters, animals, vehicles,
    /// ships, buildings; its atlas has tt_class_ship) and its ToolTipWindow.usl puts the qualities at tabs 19, 59, 97,
    /// 137, 175; the original's has four at 23, 72, 121, 169.</summary>
    public static bool FiveCols => UiAtlas.Entry("tt_class_ship", -1) != null;
    public static string[] VS_COLS => FiveCols ? VS_COLS5 : VS_COLS4;
    public static int[] VS_X => FiveCols ? VS_X5 : VS_X4;

    /// <summary>is there a body text worth a line (the help items' "%%" placeholder is none)</summary>
    public bool HasBody => Body != null && Body.Trim().Length > 0 && Body.Trim() != "%%";
    public bool HasVs => Vs != null && VS_COLS.Any(c => Vs.TryGetValue(c, out var v) && Q.ContainsKey(v));

    /// <summary>everything as plain text (logs, tests)</summary>
    public override string ToString()
    {
        var s = new List<string>();
        if (!string.IsNullOrEmpty(Title)) s.Add($"[{Cls ?? "other"}] {Title}");
        if (Lvl != null) for (int n = 5; n >= 1; n--) if (Lvl.TryGetValue(n.ToString(), out var l) && !string.IsNullOrEmpty(l)) s.Add($"L{n}: {l}");
        if (HasBody) s.Add(Body!.Trim());
        foreach (var (t, bad) in Lines) s.Add((bad ? "! " : "") + t.Trim());
        if (HasVs) s.Add("vs " + string.Join(" ", VS_COLS.Select(c => c + (Vs!.TryGetValue(c, out var v) ? v : ""))));
        if (Cost != null) s.Add("cost " + string.Join(" ", COST_COLS.Select(r => r + " " + Math.Round(Cost[r]) + (Short.Contains(r) ? "!" : ""))) + (Cost.Power != 0 ? " power " + Math.Round(Cost.Power) : ""));
        return string.Join("\n", s);
    }
}

public static class Tooltips
{
    // the tech tree's generic moves have no help item: the original's buttons for them use the command bar's keys
    static readonly Dictionary<string, string> GENERIC = new()
    {
        ["_attack"] = "cb_attack", ["_walk"] = "cb_walk", ["_stop"] = "cb_stop", ["_kill"] = "cb_kill", ["_open"] = "cb_gate_open",
        ["_close"] = "cb_gate_close", ["_auto_gate"] = "cb_gate_auto", ["_aggressivetarget"] = "cb_agressive_walk",
    };

    static Tip FromText(TextEntry t) => new()
    {
        Title = t.Name, Cls = t.Cls, Lvl = t.Lvl.Count > 0 ? t.Lvl : null, Body = t.Medium, Vs = t.Vs.Count > 0 ? t.Vs : null,
    };

    /// <summary>a help item by key: { title, cls, lvl, body, vs } (a missing key: the key itself as the text)</summary>
    public static Tip HelpTip(Rules D, string key)
    {
        var k = GENERIC.TryGetValue(key, out var g) ? g : key;
        var t = D.TextKey(k);
        var tip = t != null ? FromText(t) : new Tip { Title = "", Body = key ?? "" };
        tip.HelpKey = k;
        return tip;
    }

    /// <summary>an object (a unit, building, animal): its help item (HelpMenu.GetObjHelpitemKey: by its description key)</summary>
    public static Tip ObjectTip(Rules D, string name, Player? owner = null)
    {
        var t = FromText(D.Text(name, owner));
        t.HelpKey = name;
        return t;
    }

    /// <summary>a menu or action button of the command bar (CommandBar.usl: title + help text of a "cb_*" key)</summary>
    public static Tip ButtonTip(Rules D, string key, Action<Tip>? extra = null)
    {
        var t = HelpTip(D, key);
        extra?.Invoke(t);
        return t;
    }

    /// <summary>a single text (SetToolTipText(s): GenerateMsgScript takes its first line as the title)</summary>
    public static Tip TextTip(string? s)
    {
        var parts = (s ?? "").Split('\n');
        return new Tip { Title = parts[0], Body = string.Join("\n", parts.Skip(1)) };
    }

    /// <summary>a button of the command bar's flyout: build, produce, upgrade, hire, special move (CmdButton.Validate).
    /// why: the reason it can't be used now (production.js canQueue, main.js moveState); sel: the selection's own
    /// alive entities (the first is the caster / producer)</summary>
    public static Tip ActionTip(World W, Player p, ActionDef a, string? why, IReadOnlyList<Entity> sel)
    {
        var D = W.Data;
        // the help item of the location's description key (Rules.actionText: the sequel's copies their own); a key the game
        // has no help item for shows as it is
        var key = a.Loc?.Desc ?? (a.Locs.Count > 0 ? a.Locs[0].Desc : null) ?? "";
        var r = a.Kind == "Build" && a.Results.Count > 0 ? a.Results[0].Obj : null;
        bool own = r != null && D.SameAs.ContainsKey(r) && D.Texts.ContainsKey(r);
        var o = key.Length > 0 && !own ? HelpTip(D, key) : FromText(D.ActionText(a));
        o.HelpKey = key.Length > 0 && !own ? key : r ?? a.Id;
        // no free place in the army pyramid for the unit's level, or the unit limit reached (CommandBar.CheckCharacterConditions)
        if ((why == "level" || why == "housing") && a.Kind != "Moves") { o.Body = D.Ui("_ObjCommandBar_UnitLevelReached"); o.Lvl = null; }
        // MIRAGE's building limits (CmdButton.usl Validate): "maximum reached" when it is, and the count whenever the
        // building has a limit - "Current count: n/max" (CheckBuildConditions: placed + queued upgrades; World.LimitOf)
        if (why == "limit") o.Lines.Add((D.Ui("_MI_BuildingMaxReached", HudGame.Reason("limit")), true));
        if (W.LimitOf(p, a) is var (cur, max)) o.Lines.Add((D.Ui("_MI_BuildingQuantity", "Current count: ") + cur + "/" + max, cur >= max));
        var caster = sel.Count > 0 ? sel[0] : null;
        IRulesOwner owner = caster != null ? caster.RulesOwner() ?? p : p;
        bool queuedElsewhere = a.Kind == "Upgrades" && !a.Results.Any(x => x.Cls == "local") && W.AnyQueued(p, a.Id) &&
            !(caster != null && caster.Queue.Any(q => q.Action.Id == a.Id));
        if (queuedElsewhere) o.Lines.Add((D.Ui("_ObjCommandBar_AlreadyInventing"), true));
        else
        {
            var T = D.Tree(owner);
            var tribe = a.Tribe == "Special" ? T.Tribe : a.Tribe;
            var inv = a.Req.Where(x => !D.Invented(owner, x, tribe, caster)).ToList();
            if (inv.Count > 0) o.Lines.Add((D.Ui("_ObjCommandBar_InventionsNeeded") + "\n" + string.Join(" ", inv), true));    // (the ids: the original doesn't translate them)
            var bld = owner.Debug ? new List<string>() : a.BReq.Where(x => !T.Built(x, tribe)).ToList();
            if (bld.Count > 0) o.Lines.Add((D.Ui("_ObjCommandBar_BuildingsNeeded") + "\n" + string.Join(" ", bld.Select(b => D.Exists(b) ? D.Text(b).Name : $"unknown ({b})")), true));
        }
        // the cost row: every resource of the tribe, 0 included; not for moves without costs
        var c = a.Cost;
        bool paid = Rules.COSTS.Any(x => c[x] != 0);
        foreach (var x in Rules.COSTS) if (c[x] > p[x]) o.Short.Add(x);
        // a move that needs a higher level than any of the selected has
        if (a.LevelReq > 1 && !sel.Any(u => Math.Max(1, u.Level) >= a.LevelReq)) o.Lines.Add((D.Ui("_ObjCommandBar_RequiresLevel") + Sim.Rules.TT.JsString(a.LevelReq), true));
        o.Cost = queuedElsewhere || (a.Kind == "Moves" && !paid) ? null : c;
        return o;
    }
}
