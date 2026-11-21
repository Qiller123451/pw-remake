// What the HUD shows, worked out from the game - the browser remake's hud.js without its DOM: the resource bar's
// numbers, the group bar's buttons, the timers, the epoch bar, the info window's content, the command bar's menus and
// buttons and the flyout's rows (with the keyboard's armed row), the army pyramid's places and cards (with the task
// icons and the debug options' extra places). The views (Hud*.cs) draw it and turn clicks into the calls below.
//
// The layout numbers are the original's (IngameScreen.usl, Info.usl, NewPyramid.usl, CommandBar.usl), as hud.js
// and its stylesheet (remake/index.src.html .hud ...) have them.
//
// No Godot types. Ported from remake/src/ui/hud.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Hud.Logic;

/// <summary>A place of the army pyramid: x, y, w, h in the pyramid picture's pixels (177 x 545).</summary>
public readonly record struct PyrRect(int X, int Y, int W, int H)
{
    public bool Contains(double x, double y, double m = 0) => x >= X - m && x <= X + W + m && y >= Y - m && y <= Y + H + m;
}

/// <summary>A button of the command bar: a menu (left, 53 px) or an action (45 px).</summary>
public sealed class CmdButton
{
    public string Id = "";
    /// <summary>the symbol on it (menubtn_sym_* / actbtn_sym_*)</summary>
    public string Sym = "";
    public bool Menu;
    /// <summary>the menu is open (menubtn_back_down)</summary>
    public bool On;
    public Func<Tip> Tip = () => new Tip();
    public Action Press = () => { };
}

/// <summary>A row of the flyout: its title and its cards.</summary>
/// <summary>a row of the flyout: its name, its cards, its category (Flyout.usl: the picture at its left; null: none)</summary>
public sealed record FlyoutRow(string Title, List<HudAction> Cards, string? Cat = null);

/// <summary>A card of the army pyramid (a unit, a building that counts, or a queued unit) at its place.</summary>
public sealed class PyrCard
{
    public int Level;               // 0..4 (the row)
    public int Index;               // its place in the row
    public PyrRect R;
    public Entity? E;               // null: a queued unit (Queued)
    public QueueItem? Queued;
    public string Name = "";
    public string? Icon;
    public double Hp = 1;           // 0..1
    public int HpSlice = 100;       // the bar's "hp%" (HpBarRule.Slice)
    public string? Task;            // card_task_<task> (or "power")
    public bool CanLevelUp, Selected;
}

/// <summary>Everything the info window shows of the selection (hud.js refreshInfo).</summary>
public sealed class InfoModel
{
    /// <summary>none | group | item | well | res | object</summary>
    public string Kind = "none";
    public string Name = "", Line = "", Hint = "";
    public string? Portrait;          // atlas name (level 4, 64 x 61)
    public string? ObjName;           // the object's class (its tooltip)
    public Player? Owner;
    public double Hp, MaxHp;          // the bar (well: fill / max)
    public bool ShowHp;
    /// <summary>the class icon left of the name (Info.usl: tt_class_chtr / anml / bldg / vhcl / other, 16 x 19 at 33, 7)</summary>
    public string? TypeIcon;
    /// <summary>what a unit with the six values carries (icon, amount): shown under them</summary>
    public (string Icon, string Amount)? Carry;
    /// <summary>the stats rows: icon, value, help key (Info.usl: six rows)</summary>
    public readonly List<(string Icon, string Value, string? Key)> Stats = new();
    /// <summary>extra tooltip lines of a stats row (its index): the armour's bonus per attacker type</summary>
    public readonly Dictionary<int, List<string>> StatLines = new();
    public readonly List<BuffDef> Buffs = new();
    public string? Item;              // the item it carries (atlas name)
    public double? Progress;          // a building site 0..1
    public readonly List<(string Icon, double Done, int Index)> Queue = new();
    public Entity? Entity;
    /// <summary>a group: the members (at most 24) with their hit points</summary>
    public readonly List<(Entity E, string? Icon, double Hp)> Group = new();
    public int GroupCount;
    /// <summary>a resource: its amount and icon</summary>
    public string? ResIcon;
    public double Amount;
    /// <summary>a resource that is never used up (the remake's map attribute infinite: the HUD shows ∞)</summary>
    public bool Infinite;
}

public sealed class HudModel
{
    // ------------------------------------------------------------------ the army pyramid's places (NewPyramid.usl)
    public static readonly PyrRect[][] PYR = BuildPyr();
    static PyrRect[][] BuildPyr()
    {
        var L5 = new List<PyrRect> { new(58, 14, 60, 57) };
        var L4 = new List<PyrRect> { new(8, 89, 46, 45), new(65, 89, 46, 45), new(122, 89, 46, 45) };
        var L3 = new List<PyrRect>(); var L2 = new List<PyrRect>(); var L1 = new List<PyrRect>();
        foreach (var y in new[] { 152, 195 }) foreach (var x in new[] { 8, 50, 92, 134 }) L3.Add(new(x, y, 34, 34));
        foreach (var y in new[] { 246, 282, 318 }) foreach (var x in new[] { 8, 41, 74, 107, 140 }) L2.Add(new(x, y, 27, 27));
        foreach (var y in new[] { 362, 398, 435, 471, 507 }) foreach (var x in new[] { 8, 41, 74, 107, 140 }) L1.Add(new(x, y, 27, 27));
        return new[] { L1.ToArray(), L2.ToArray(), L3.ToArray(), L4.ToArray(), L5.ToArray() };
    }
    /// <summary>atlas level index per pyramid row (bigger cards higher up)</summary>
    public static readonly int[] ICON_LVL = { 2, 2, 2, 3, 4 };
    public const int PYR_W = 177, PYR_H = 545;
    public static readonly Dictionary<string, string> RES_ICON = new()
    {
        ["food"] = "resicon_food", ["wood"] = "resicon_wood", ["stone"] = "resicon_stone", ["skulls"] = "resicon_scalps", ["units"] = "resicon_unit",
    };
    public const string REASON_DOWN = "A unit keeps the level it has reached";

    readonly IHudContext H;
    public readonly HudGame G;
    public HudModel(IHudContext host) { H = host; G = new HudGame(host); }

    World W => H.World;
    Player Me => H.Me;
    Rules D => H.World.Data;

    // ------------------------------------------------------------------ options (Options menu of the JS)
    /// <summary>open the build / produce menu on a new selection (settings.autoMenu)</summary>
    public bool AutoMenu = true;
    /// <summary>show income per minute under the resources (settings.showIncome; AppSettings.IncomePerMinute)</summary>
    public bool ShowIncome;

    // ------------------------------------------------------------------ resource bar (hud.js update / resTip)
    public static string Fmt(double n) => Math.Floor(n).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>the five counters of the bar in its order: units, skulls, food, wood, stone - (value, extra line, full)</summary>
    public List<(string Key, string Value, string Extra, bool Full)> Resources()
    {
        var p = Me;
        var inc = ShowIncome ? p.IncomePerMin(W.Time) : null;
        var res = new List<(string, string, string, bool)>
        {
            ("units", $"{p.Units}/{Fmt(p.MaxUnits)}", "", false),
            ("skulls", Fmt(p["skulls"]), "", false),
        };
        foreach (var r in new[] { "food", "wood", "stone" })
            res.Add((r, Fmt(p[r]), inc != null ? "+" + Fmt(inc.GetValueOrDefault(r)) : "", p[r] >= p.Caps.GetValueOrDefault(r)));
        return res;
    }

    /// <summary>the resource bar's tooltips: the help items of the top bar (H_ui "Topmenu": _tb_unitlimit, _tb_skulls,
    /// _tb_food ...) and the numbers (the remake's bar shows no maximum)</summary>
    public Tip ResTip(string k)
    {
        var p = Me;
        var key = k == "units" ? "_tb_unitlimit" : k == "skulls" ? "_tb_skulls" : "_tb_" + k;
        var n = k == "units" ? $"{p.Units} / {Fmt(p.MaxUnits)}" : k == "skulls" ? Fmt(p["skulls"]) : $"{Fmt(p[k])} / {Fmt(p.Caps.GetValueOrDefault(k))}";
        double inc = k != "units" && k != "skulls" ? p.IncomePerMin(W.Time).GetValueOrDefault(k) : 0;
        var t = Tooltips.HelpTip(D, key);
        t.Lines.Add((n + (inc != 0 ? $"  (+{Fmt(inc)} / min)" : ""), false));
        return t;
    }

    /// <summary>the line under the bar: the epoch and the debug state</summary>
    public string EpochLine()
    {
        int ep = Me.Epoch();
        string[] R = { "", "I", "II", "III", "IV", "V" };
        return $"Epoch {R[Math.Clamp(ep, 0, 5)]}" + (W.Cheats != null ? "  ·  Debug options" : Me.Debug ? "  ·  Debug mode (free, instant)" : "");
    }

    // ------------------------------------------------------------------ the group bar (GroupBar.usl)
    bool Used(int k) => H.Group(k) is { } g && g.Any(x => x.Alive);
    /// <summary>a button per assigned group (its number), the selected one pressed; of the unassigned ones only the first
    /// after the last assigned one shows (MODE_FREAKY, the default). In the bar's order 1 .. 9, 0: (k, shown, label, pressed)</summary>
    public List<(int K, bool Shown, string Label, bool Pressed)> Groups()
    {
        // (UpdateVisibility: from group 9 down to the first assigned one below an unassigned one; then the first
        // unassigned one above it - or button 0 when group 9 is assigned)
        int i = 9; bool empty = false;
        for (; i >= 0; i--) { if (Used(i) && empty) break; if (!Used(i)) empty = true; }
        int? next = null;
        if (empty)
        {
            if (Used(9)) next = 0;
            else { if (i == -1) i = 0; for (int j = i + 1; j < 10; j++) if (!Used(j)) { next = j; break; } }
        }
        var res = new List<(int, bool, string, bool)>();
        foreach (var k in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0 })
        {
            bool on = Used(k);
            res.Add((k, on || k == next, on ? k.ToString() : "", on && H.SelectedGroup == k.ToString()));
        }
        return res;
    }

    // ------------------------------------------------------------------ timers (gameover.js goTimers)
    public static string Mmss(double t) { var s = (int)Math.Max(0, Math.Ceiling(t)); return $"{s / 60}:{s % 60:00}"; }
    /// <summary>the game's timers top right: (atlas name of the pill, text, owner colour or null, paused, tooltip)</summary>
    public List<(string Icon, string Text, int? Color, bool Paused, string Tip)> Timers()
    {
        var res = new List<(string, string, int?, bool, string)>();
        foreach (var t in W.GoTimers())
        {
            var tip = D.Ui(t.Tip, "%1").Replace("%1", t.Owner?.Name ?? "");
            res.Add(("timer_" + t.Icon, Mmss(t.Left), t.Owner?.Color, t.Paused, tip));
        }
        return res;
    }

    // ------------------------------------------------------------------ the epoch bar (IngameScreen.usl UpdatePyramidButton)
    static readonly int[] BLEND = { 0, 65, 44, 26, 15, 0 };
    /// <summary>The bar's rows are the pyramid's levels; from the top down to the current epoch's line (BLEND: % of the
    /// height, by epoch) it is dark, below it lit. The next epoch, when the player can start it, shows orange between
    /// the two lines; while it is being researched the lit part grows up into the orange. Returns the heights in % of
    /// the layers pyramid_bg_nextage (noAvail) and pyramid_bg_noavail (blend) over pyramid_bg_avail.
    /// (The original asks only the resources: its requirement path "/Actions/&lt;tribe&gt;/Upgrades/BLDG/..." does not
    /// exist, so the requirements always pass, and the SEAS have no path at all. Here the age action's own check decides.)</summary>
    public (int Blend, int NoAvail) EpochBar()
    {
        var p = Me;
        int age = Math.Clamp(p.Epoch(), 0, 5);
        bool next = false, res = false; double prog = 0;
        if (age < 5)
        {
            var id = "age_" + (age + 1);
            foreach (var b in W.Buildings)
                if (b.Alive && b.Owner == p) foreach (var q in b.Queue)
                        if (q.Action.Id == id) { next = true; res = true; prog = Math.Max(prog, Math.Min(1, q.T / q.Total)); }
            if (!next)
            {
                var a = D.Actions(p).FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == id);
                if (a != null && D.Check(p, a) == null) { next = true; res = p.CanAfford(a.Cost); }
            }
        }
        int blend = BLEND[age], noAvail = BLEND[age];
        if (next && res && age < 5) blend = BLEND[age + 1];
        if (prog > 0 && age < 5) noAvail -= (int)Math.Round((BLEND[age] - BLEND[age + 1]) * prog, MidpointRounding.AwayFromZero);
        return (blend, noAvail);
    }

    // ------------------------------------------------------------------ the info window (Info.usl)
    public InfoModel Info()
    {
        var sel = H.Selection.Where(e => e.Alive).ToList();
        var m = new InfoModel();
        // the buffs of the object the info window speaks for: the only one, or the first of the highest level of a group
        // (Info.usl:1338-1360)
        Entity? lead = sel.Count == 1 ? sel[0] : null;
        if (sel.Count > 1) foreach (var e in sel) if (e.Kind == "unit" && (lead == null || e.Level > lead.Level)) lead = e;
        if (lead != null && (lead.Kind == "unit" || lead.Kind == "building")) m.Buffs.AddRange(Buffs.VisibleBuffs(W, lead, Me));
        if (sel.Count == 0) return m;
        if (sel.Count > 1)
        {
            m.Kind = "group";
            foreach (var e in sel.Take(24)) m.Group.Add((e, UiAtlas.IconFor(e.Name), e.MaxHp > 0 ? e.Hp / e.MaxHp : 1));
            m.GroupCount = sel.Count;
            return m;
        }
        var x = sel[0];
        m.Entity = x;
        if (x.Kind == "item")
        {
            // an item (Info.usl ITEM): its help item - the name and what it does
            var t = D.Text(x.Name);
            m.Kind = "item"; m.Portrait = x.Name; m.Name = t.Name; m.Hint = t.Medium ?? ""; m.ObjName = x.Name;
            return m;
        }
        if (x is Well wl)
        {
            // a healing well (Info.usl FNTN): its help title (_laerad_well, _healing_well_01) and what it holds as hit points
            var t = D.TextKey(Regex.IsMatch(x.Name ?? "", "laerad") || Regex.IsMatch(x.Gfx ?? "", "laerad") ? "_laerad_well" : "_healing_well_01");
            m.Kind = "well"; m.Name = t?.Name ?? "Healing well"; m.ShowHp = true; m.Hp = wl.Fill; m.MaxHp = wl.Max;
            return m;
        }
        if (x is ResNode rn)
        {
            // Info.usl:1404-1436: the resources' help titles (_wood, _stone, _fruit, _food, _fish), the amount _iw_resourceamount
            var key = rn.Type == "bush" ? "_fruit" : rn.Type == "fish" ? "_fish" : "_" + rn.Res;
            m.Kind = "res"; m.Name = D.TextKey(key)?.Name ?? rn.Res;
            m.ResIcon = RES_ICON.GetValueOrDefault(rn.Res); m.Amount = Math.Ceiling(rn.Amount); m.Infinite = rn.Infinite;
            int n = rn.Workers.Count;
            m.Hint = $"{n} worker{(n == 1 ? "" : "s")} gathering";
            return m;
        }
        var tx = D.Text(x.Name, x.Owner);
        m.Kind = "object"; m.Name = tx.Name; m.ObjName = x.Name; m.Owner = x.Owner;
        m.Portrait = UiAtlas.IconFor(x.Name);
        m.ShowHp = true; m.Hp = Math.Max(0, x.Hp); m.MaxHp = x.MaxHp;
        // Info.usl CInfo.Update: the class icon, the name beside it, the owner's (player's) name under them
        m.TypeIcon = x.Kind == "building" ? "tt_class_bldg" : x.Cls switch { "CHTR" => "tt_class_chtr", "ANML" => "tt_class_anml", "VHCL" => "tt_class_vhcl", _ => "tt_class_other" };
        m.Line = x.Owner == null ? "" : x.Owner.Name.StartsWith("_") ? D.Ui(x.Owner.Name, x.Owner.Name) : x.Owner.Name;
        // the six values in the frame's rows (CAttribIcon: attack, rangeattack, shield, armor, range, skulls - "-" for 0,
        // "min - max" for a range with a minimum), shown when any of them is not 0
        var mw = x.Weapons.Short != null && string.IsNullOrEmpty(x.Weapons.Short.Projectile) ? x.Weapons.Short
               : x.Weapons.Long != null && string.IsNullOrEmpty(x.Weapons.Long.Projectile) ? x.Weapons.Long : null;
        var rw = x.Weapons.Long != null && !string.IsNullOrEmpty(x.Weapons.Long.Projectile) ? x.Weapons.Long : null;
        var w = x.Weapons.Long ?? x.Weapons.Short;
        if (w != null || (x.Stats != null && x.Stats.Scalps != 0))
        {
            string Kind(Weapon wp, string b) => b + (wp.Ap != 0 ? "_pierce" : "") + (wp.Poison != 0 ? "_poison" : "") + (wp.Splash != 0 ? "_area" : "");
            string Icon(string k, string b) => UiAtlas.Entry("info_" + k, 9) != null ? "info_" + k : "info_" + b;
            string V(double v) => v == 0 || double.IsNaN(v) ? "-" : R(v);
            var mcs = mw != null ? x.Cs(mw) : null; var rcs = rw != null ? x.Cs(rw) : null; var wcs = w != null ? x.Cs(w) : null;
            var mk = mw != null ? Kind(mw, "attack") : "attack";
            var rk = rw != null ? Kind(rw, "rangeattack") : "rangeattack";
            m.Stats.Add((Icon(mk, "attack"), mw != null ? V(mcs?.Dmg ?? mw.Dmg) : "-", "_iw_" + mk));
            m.Stats.Add((Icon(rk, "rangeattack"), rw != null ? V(rcs?.Dmg ?? rw.Dmg) : "-", "_iw_" + rk));
            m.Stats.Add(("info_shield", w != null ? V(wcs?.RProt ?? w.RDef) : "-", "_iw_defense_arrow"));
            m.Stats.Add(("info_armor", w != null ? V(wcs?.Prot ?? w.Def) : "-", "_iw_defense_melee"));
            // the armour's bonus per attacker type (MIRAGE's mirage_item_stat_display, Info.usl CAttribIcon): the weapon's
            // DefenseBonus with the tech tree's Defence_<type> modifiers - the bony amulet's +10 against animals
            var vs = DefenceLines(x);
            if (vs.Count > 0) m.StatLines[3] = vs;
            var lw = rw ?? w;
            double range = lw != null ? (x.Cs(lw)?.Range ?? lw.Range) : 0;
            m.Stats.Add(("info_range", lw != null && lw.MinRange != 0 && range != 0 ? $"{TT.JsString(lw.MinRange)} - {R(range)}" : V(range), "_iw_range"));
            m.Stats.Add(("info_skulls", x.Stats != null && x.Stats.Scalps != 0 ? ScalpsText(x) : "-", "_iw_skulls"));
            if (m.Stats.All(st => st.Value == "-")) { m.Stats.Clear(); m.StatLines.Clear(); }
        }
        bool attribs = m.Stats.Count > 0;
        var pax = W.PassengersOf(x);
        if (!attribs && pax != null && pax.Count > 0) m.Stats.Add(("resicon_unit", $"{pax.Count} inside", null));
        if (!attribs && x is Building gb && gb.Def?.Gate == true && gb.Built)
            m.Stats.Add(("info_armor", new[] { "open", "closed", "automatic" }[Math.Clamp(gb.GateState, 0, 2)] + (gb.GateOpen ? " (open)" : ""), null));
        // MIRAGE's SEAS well: its energy (Info.usl "energystate" / "maxenergyamount")
        if (!attribs && x is Building hw && hw.SeasWell is { } sw && sw.Max > 0) m.Stats.Add((UiAtlas.Entry("bufficon_heal", 9) != null ? "bufficon_heal" : "info_armor", $"{Math.Floor(sw.Energy)} / {R(sw.Max)}", null));
        if (!attribs && x is Building wb && wb.WarpT != null) m.Stats.Add(("info_skulls", $"victory in {Math.Max(0, Math.Ceiling(wb.WarpT.Value))} s", null));
        if (!attribs && x.Stats != null && x.Stats.Scalps != 0 && x.Owner != Me) m.Stats.Add(("info_skulls", ScalpsText(x), "_iw_skulls"));
        // what it carries: a row too, or (beside the six) under them
        if (x is Unit cu && cu.Carry != null && cu.Carry.Amount > 0)
        {
            var ci = RES_ICON.GetValueOrDefault(cu.Carry.Res == "food" ? "food" : cu.Carry.Res) ?? "resicon_food";
            if (!attribs) m.Stats.Add((ci, TT.JsString(cu.Carry.Amount), null));
            else m.Carry = (ci, TT.JsString(cu.Carry.Amount));
        }
        // Info.usl CInfo: one value per row of the window's frame, ATTR_BASE 117, 34, ATTR_OFFSET 17 (six rows)
        if (m.Stats.Count > 6) m.Stats.RemoveRange(6, m.Stats.Count - 6);
        // the item it carries (Info.usl: the item slot, with the item's help as its tooltip)
        if (x is Unit iu && iu.Item != null) m.Item = iu.Item.Name;
        if (x is Building sb && !sb.Built) m.Progress = sb.Progress;
        if (x.Queue.Count > 0 && x.Owner == Me)
        {
            for (int i = 0; i < x.Queue.Count; i++)
            {
                var q = x.Queue[i];
                var qi = q.Action.Loc?.Icon ?? (q.BuildDown ? "actbtn_sym_recycle" : q.Action.Id);
                m.Queue.Add((qi, q.Total > 0 ? Math.Min(1, q.T / q.Total) : 0, i));
            }
        }
        return m;
    }
    static string R(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture);

    static readonly (string Type, string Name)[] ATTACKER_TYPES = { ("CHTR", "characters"), ("ANML", "animals"), ("VHCL", "vehicles"), ("BLDG", "buildings"), ("SHIP", "ships") };
    /// <summary>the armour tooltip's lines: "Against animals: +10" per attacker type with a bonus (World.DefenceBonusVs)</summary>
    public List<string> DefenceLines(Entity x)
    {
        var l = new List<string>();
        if (x.Kind != "unit") return l;
        foreach (var (t, n) in ATTACKER_TYPES)
        {
            double v = W.DefenceBonusVs(x, t);
            if (v != 0 && !double.IsNaN(v)) l.Add($"Against {n}: {(v > 0 ? "+" : "")}{R(v)}");
        }
        return l;
    }

    /// <summary>
    /// The skulls row (MIRAGE's mirage_item_stat_display): the unit's skulls and, for one the player may kill (not an
    /// ally's), the difference to what killing it pays (World.ScalpsFor: the flowerpot of insanity's × 1.5 - "10 +5" -,
    /// Schliemann's aura, the skull protector, a young animal's share).
    /// </summary>
    public string ScalpsText(Entity x)
    {
        double s = x.Stats!.Scalps;
        var txt = TT.JsString(s);
        if (x.Owner != null && (x.Owner == Me || Me.IsFriend(x.Owner))) return txt;
        double d = W.ScalpsFor(x, Me) - s;
        return d == 0 || double.IsNaN(d) ? txt : $"{txt} {(d > 0 ? "+" : "−")}{TT.JsString(Math.Abs(d))}";
    }

    /// <summary>a key that changes whenever the info window must be redrawn (hud.js infoKey)</summary>
    public string InfoKey()
    {
        var sel = H.Selection.Where(e => e.Alive).ToList();
        var sb = new System.Text.StringBuilder();
        foreach (var e in sel)
        {
            sb.Append(e.Id).Append(':').Append(Math.Round(e is ResNode r ? r.Amount : e is Well w ? w.Fill : e is Building { SeasWell: { } sw } ? e.Hp * 1e4 + sw.Energy : e.Hp)).Append(':');
            if (e is ResNode rr) sb.Append(rr.Workers.Count);
            sb.Append(':').Append(e.Level).Append(':');
            foreach (var q in e.Queue) sb.Append(q.Action.Id).Append(Math.Floor(q.T)).Append(',');
            if (e is Building b) sb.Append(':').Append(b.Progress.ToString("0.00"));
            if (e is Unit u && u.Carry != null) sb.Append(u.Carry.Amount);
            if (e is Unit iu && iu.Item != null) sb.Append('i');
            sb.Append('|');
        }
        Entity? lead = sel.Count == 1 ? sel[0] : null;
        if (sel.Count > 1) foreach (var e in sel) if (e.Kind == "unit" && (lead == null || e.Level > lead.Level)) lead = e;
        if (lead != null && (lead.Kind == "unit" || lead.Kind == "building")) foreach (var b in Buffs.VisibleBuffs(W, lead, Me)) sb.Append(b.Attrib).Append(',');
        // (the flowerpot and the amulet change the skulls and the armour's tooltip of every unit shown)
        sb.Append(Me.ScalpMod);
        if (lead != null && lead.Kind == "unit") sb.Append(':').Append(W.DefenceBonusVs(lead, "ANML"));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the command bar (CommandBar.usl)
    /// <summary>the open menu: build | produce | special | stances | slots | null</summary>
    public string? Menu;
    string? _selSig;
    readonly HashSet<int> _selIds = new();
    /// <summary>a selection made by the player whose main menu is still to open (it opens as soon as the selection has
    /// one: at once, or when its building is finished)</summary>
    bool _menuNew;
    /// <summary>the keyboard's armed row (Keys.srf CmdBar_Accel_*: rows count from the bottom)</summary>
    public (string Menu, int Row)? Armed;
    /// <summary>the rows of the open menu the last time they were asked (top to bottom)</summary>
    public List<FlyoutRow> Rows = new();

    /// <summary>the buttons of the command bar for the selection; opens the main menu of a new selection</summary>
    public List<CmdButton> CommandBar()
    {
        var sel = G.OwnSelection();
        var res = new List<CmdButton>();
        if (sel.Count == 0) { Menu = null; _selSig = ""; _selIds.Clear(); _menuNew = false; return res; }
        var workers = sel.Where(e => e is Unit u && u.IsWorker && u.CanBuild).ToList();     // (harvesting vehicles build nothing)
        var units = sel.OfType<Unit>().ToList();
        var producer = G.Producer(sel);       // (one object, or several buildings: the first that produces)
        // a selection the player makes - by click, box, double click, control group, the pyramid, the info window, again
        // the same one - opens its main menu (OnSelectionChanged); so does one with something new in it that came another
        // way (a game loaded). What only shrinks it by itself (deaths, a unit lost) leaves the menu as it is.
        var sig = string.Join(",", sel.Select(e => e.Id));
        if (sig != _selSig)
        {
            if (sel.Any(e => !_selIds.Contains(e.Id))) _menuNew = true;
            _selSig = sig;
            _selIds.Clear(); foreach (var e in sel) _selIds.Add(e.Id);
        }
        if (_menuNew && AutoMenu) Menu = null;
        // (the tooltips: CommandBar.usl's cb_* help items)
        // Build for anything that can build (CommandBar.usl AvailableMenuButtons: can_build - the workers, Taslow, the
        // Dustriders' fighters); Produce only without it
        var menus = new List<(string Id, string Sym, string Tt)>();
        bool builds = units.Any(e => e.CanBuild);
        if (builds) menus.Add(("build", "menubtn_sym_build", "cb_build"));
        if (producer != null && !builds) menus.Add(("produce", "menubtn_sym_produce", "cb_produce"));
        if (G.MoveActions(sel).Count > 0) menus.Add(("special", "menubtn_sym_special", "cb_specials"));
        if (units.Count > 0) menus.Add(("stances", "menubtn_sym_stances", "cb_set_stance"));
        // transporters and bunkers: who is inside (CommandBar.usl FLAG_HOLD_UNITS -> the "slots" menu)
        if (sel.Any(e => e.Owner == Me && W.Capacity(e) > 0)) menus.Add(("slots", "menubtn_sym_slots", "cb_unload"));
        // the main menu: the first of Build, Produce, Special the selection has - for anything of the player's that builds,
        // produces or has moves; one with none yet (a site) opens it when it has (the building finished). Closing it by
        // hand keeps it closed until the selection changes. Options: "Open the menu on selection" (AutoMenu).
        if (_menuNew)
        {
            if (!AutoMenu) _menuNew = false;
            else if (DefaultMenu(menus.Select(m => m.Id)) is { } first) { Menu = first; _menuNew = false; }
        }
        foreach (var (id, sym, tt) in menus)
            res.Add(new CmdButton { Id = id, Sym = sym, Menu = true, On = Menu == id, Tip = () => Tooltips.ButtonTip(D, tt), Press = () => ToggleMenu(id) });
        CmdButton Act(string sym, string tt, Action f) => new() { Id = sym, Sym = sym, Tip = () => Tooltips.ButtonTip(D, tt), Press = f };
        if (units.Count > 0)
        {
            res.Add(Act("actbtn_sym_walkto", "cb_walk", () => H.SetInputMode("move")));
            res.Add(Act("actbtn_sym_attack", "cb_attack", () => H.SetInputMode("attack")));
            res.Add(Act("actbtn_sym_agrwalk", "cb_agressive_walk", () => H.SetInputMode("amove")));
            res.Add(Act("actbtn_sym_stop", "cb_stop", () => G.Order(units, "stop")));
            if (units.Any(u => u.Passengers is { Count: > 0 })) res.Add(Act("actbtn_sym_disemb", "cb_dismount_all", () => G.Order(units, "unload")));
            if (units.Any(u => u.CanBuild)) res.Add(Act("actbtn_sym_repair", "cb_repair", () => H.SetInputMode("repair")));
            if (units.Any(u => u.Inside)) res.Add(Act("actbtn_sym_disemb", "cb_dismount_all", () => G.Order(units, "unload")));
            if (units.Count == 1 && units[0].Level < 5)
            {
                // CommandBar.usl:1031-1057: "No Level-Up possible" when it isn't, and the skulls it costs
                var u = units[0];
                double cost = u.Level - 1 < D.LevelupSkulls.Length ? D.LevelupSkulls[u.Level - 1] : 0;
                res.Add(new CmdButton
                {
                    Id = "actbtn_sym_levelup", Sym = "actbtn_sym_levelup", Press = () => G.LevelUp(u),
                    Tip = () => Tooltips.ButtonTip(D, "cb_level_up", t =>
                    {
                        if (!W.CanHaveLevel(u, u.Level + 1)) t.Lines.Add((D.Ui("_ObjCommandBar_NoLevelUp"), false));
                        t.Cost = new Cost { Skulls = cost };
                        if (Me["skulls"] < cost) t.Short.Add("skulls");
                    }),
                });
            }
        }
        if (units.Count == 0 && sel.Any(e => W.IsMovingHarbour(e) && e is Building { Built: true })) res.Add(Act("actbtn_sym_walkto", "cb_walk", () => H.SetInputMode("move")));
        if (units.Count == 0 && sel.Any(e => W.CanAimTower(e))) res.Add(Act("actbtn_sym_attack", "cb_attack", () => H.SetInputMode("attack")));
        if (sel.Any(e => G.CanRally(e))) res.Add(Act("actbtn_sym_rallye", "cb_set_rallypoint", () => H.SetInputMode("rally")));
        var bunker = sel.FirstOrDefault(e => e is Building && W.PassengersOf(e) is { Count: > 0 });
        if (bunker != null) res.Add(Act("actbtn_sym_disemb", "cb_dismount_all", () => G.Send(new UnloadCommand { Transport = bunker.Id })));
        res.Add(Act("actbtn_sym_kill", "cb_kill", () => G.KillSelected()));
        if (Menu != null && !menus.Any(m => m.Id == Menu)) Menu = null;
        return res;
    }

    public void ToggleMenu(string id) { Menu = Menu == id ? null : id; Armed = null; _menuNew = false; }

    /// <summary>the menu a new selection opens by itself: the first of Build, Produce, Special it has (Stances and the
    /// passengers' slots stay closed)</summary>
    public static string? DefaultMenu(IEnumerable<string> menus) => menus.FirstOrDefault(m => m is "build" or "produce" or "special");

    /// <summary>the selection changed (Hud.SelectionChanged); not silent: the player selected - again the same, too - so
    /// its main menu opens</summary>
    public void SelectionChanged(bool silent)
    {
        if (!silent) _menuNew = true;
        if (Menu != null && !H.Selection.Any(e => e.Owner == Me)) Menu = null;
    }

    /// <summary>the rows of the open menu, top to bottom (CommandBar.usl SetCategory; Flyout.usl sorts a row by the actions'
    /// uiposition menupos - a fixed place; -1: the first free places, -2: at the end). Rows without cards are left out.</summary>
    public List<FlyoutRow> MenuRows()
    {
        var sel = G.OwnSelection();
        var rows = new List<FlyoutRow>();
        static double Slot(HudAction a, int i)
        {
            var mp = a.MenuPos;
            int m = int.TryParse(mp, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : int.MinValue;
            return m >= 0 ? m : m == -2 ? 1e6 + i : 1e3 + i;
        }
        // (the sort is stable: equal places keep their order)
        static List<HudAction> ByPos(IEnumerable<HudAction> acts) => acts.Select((a, i) => (a, i)).OrderBy(t => Slot(t.a, t.i)).ThenBy(t => t.i).Select(t => t.a).ToList();
        if (Menu == "build")
        {
            var acts = G.BuildActions();
            // MIRAGE puts more in the build menu: subcat UNIT (the SEAS carrier, the launch pads, the floating harbour, the
            // final boss) - a row of its own, the top one (MIRAGE CommandBar.usl: SetCategory 0 UNIT, 1 ECON, 2 COMB,
            // 3 DEFE; the base game's ECON, COMB, DEFE), like any other subcat the remake doesn't know
            var BROWS = new[] { ("UNIT", "Special"), ("ECON", "Economy"), ("COMB", "Military"), ("DEFE", "Defence") };
            foreach (var (k, t) in BROWS) rows.Add(new FlyoutRow(t, ByPos(acts.Where(a => a.Subcat == k)), k));
            var brest = acts.Where(a => !BROWS.Any(b => b.Item1 == a.Subcat)).ToList();
            if (brest.Count > 0) rows.Add(new FlyoutRow("More", ByPos(brest)));
        }
        else if (Menu == "produce")
        {
            // Dustriders: UMGR (the farms' modes), UPGR, UNIT; the others: UPGR, UNIT
            var acts = G.ProducerActions(G.Producer(sel) ?? sel.FirstOrDefault()).Select(a => new HudAction { A = a }).ToList();
            string Sub(HudAction a) => a.Subcat ?? (a.A!.Kind == "Build" ? "UNIT" : "UPGR");
            // MIRAGE adds rows: ENTY (taverns: the heroes are split over UNIT and ENTY; the SEAS garage's dinosaurs, the Norse
            // animal farm's), EXTE / RESE (laboratory, weapons builder), CONV (the brachiosaurus' and catapult rex's
            // conversions); a subcat the remake doesn't know yet still gets a row rather than disappearing
            var ROWS = new[] { ("UMGR", "Modes"), ("UPGR", "Upgrades"), ("EXTE", "Extensions"), ("RESE", "Research"), ("CONV", "Conversions"), ("UNIT", "Units"), ("ENTY", "Units") };
            foreach (var (k, t) in ROWS) rows.Add(new FlyoutRow(t, ByPos(acts.Where(a => Sub(a) == k)), k));
            var rest = acts.Where(a => !ROWS.Any(r => r.Item1 == Sub(a))).ToList();
            if (rest.Count > 0) rows.Add(new FlyoutRow("More", ByPos(rest)));
        }
        else if (Menu == "special")
        {
            // SPEC rows by subcat: "UNIT" (the heroes' moves), none (the units' moves), then any other (the Dustriders'
            // BuildDown ...); the building commands (gates, markets) above them
            var acts = G.MoveActions(sel).Select(a => new HudAction { A = a }).ToList();
            var spec = acts.Where(a => a.Cat == "SPEC" && Moves.MOVES.ContainsKey(a.Id)).ToList();
            rows.Add(new FlyoutRow("Commands", ByPos(acts.Where(a => !spec.Contains(a)))));
            var subs = new List<string> { "UNIT", "" };
            foreach (var a in spec) { var k = a.Subcat ?? ""; if (!subs.Contains(k)) subs.Add(k); }
            foreach (var k in subs) rows.Add(new FlyoutRow(k == "UNIT" ? "Hero moves" : "Special moves", ByPos(spec.Where(a => (a.Subcat ?? "") == k))));
        }
        else if (Menu == "stances")
        {
            // AggroState_0..2 (FightingObj.m_iAggressionState), in their menu places 0 / 1 / 2
            rows.Add(new FlyoutRow("Stance", new[] { 0, 1, 2 }.Select(n => new HudAction { Stance = n, IconOverride = "menubtn_sym_stance_" + n }).ToList()));
        }
        return Rows = rows.Where(r => r.Cards.Count > 0).ToList();
    }

    /// <summary>the icon of a card (hud.js refreshFlyout: the location's icon, the result's card, the tribe's epoch card;
    /// fallbacks for the generic commands) and the atlas level to draw it at (3: 44 x 43; null: the symbol's own)</summary>
    public (string? Icon, int? Level) CardIcon(HudAction c)
    {
        if (c.Stance != null) return (c.IconOverride, null);
        var a = c.A!;
        var icon = c.Icon ?? a.Id;
        if (UiAtlas.Entry(icon, 3) != null) return (icon, 3);
        var r = a.Results.Count > 0 ? UiAtlas.IconFor(a.Results[0].Obj) : null;
        if (r != null) return (r, 3);
        // an epoch: the tribe's epoch card (hu_age_2 ...; the SEAS have none: their headquarters of that epoch)
        var age = Regex.Match(a.Id, @"^age_(\d)$");
        if (age.Success)
        {
            var tr = Me.Tribe.ToLowerInvariant();
            var card = UiAtlas.Entry($"{tr}_age_{age.Groups[1].Value}") != null ? $"{tr}_age_{age.Groups[1].Value}" : $"{tr}_headquarters_{age.Groups[1].Value}";
            if (UiAtlas.Entry(card) != null) return (card, 3);
        }
        var FALLBACK = new Dictionary<string, string>
        {
            ["Open"] = "actbtn_sym_gtopen", ["Close"] = "actbtn_sym_gtclose", ["Auto"] = "actbtn_sym_gtauto", ["Kill"] = "actbtn_sym_kill",
            ["BuildDown"] = "actbtn_sym_recycle", ["buy_food"] = "resicon_food", ["buy_wood"] = "resicon_wood", ["buy_stone"] = "resicon_stone",
        };
        return FALLBACK.TryGetValue(a.Id, out var f) ? (f, null) : (null, null);
    }

    /// <summary>a unit's card: the level (tier) it comes out at, the original's card level badge (menucmd_lvl&lt;n&gt;_ovl)</summary>
    public int? CardLevel(HudAction c)
    {
        var a = c.A;
        var made = a != null && a.Kind == "Build" && a.Results.Count > 0 ? a.Results[0].Obj : null;
        var ix = made != null ? D.Info(made) : null;
        if (ix == null || ix.Type == "BLDG") return null;
        try { return Math.Clamp(W.UnitStartLevel(a!, Me), 1, 5); } catch (Exception) { return null; }
    }

    /// <summary>a card's look: locked (grey: requirements, done; a move's level), dim (can't now: costs, cooldown ...)</summary>
    public static (bool Locked, bool Dim) CardState(HudAction c, string? why) =>
        (why == "req" || why == "done" || (why == "level" && c.A?.Kind == "Moves"), why != null && !(why == "req" || why == "done" || (why == "level" && c.A?.Kind == "Moves")));

    /// <summary>keyboard (Keys.srf CmdBar_Accel_*): a letter opens a menu and arms one of its rows - counted from the bottom
    /// (Flyout.usl: category = rows - row - 1); then 1 .. 9, 0 press the row's buttons. false if there is no such row.</summary>
    public bool ArmRow(string menu, int row)
    {
        if (H.Selection.Count == 0) return false;
        CommandBar();                                    // (a new selection's main menu first: the key's menu wins)
        if (Menu != menu) { Menu = menu; _menuNew = false; CommandBar(); }
        if (Menu != menu) return false;                  // (the selection has no such menu)
        var rows = MenuRows();
        if (row >= rows.Count) { Armed = null; return false; }
        Armed = (menu, row);
        return true;
    }
    public void Disarm() => Armed = null;
    /// <summary>presses the n-th button (0-based) of the armed row</summary>
    public bool PressArmed(int n, bool shift = false)
    {
        var A = Armed;
        Armed = null;
        var rows = MenuRows();
        if (A == null) return false;
        var r = rows.Count - 1 - A.Value.Row >= 0 && rows.Count - 1 - A.Value.Row < rows.Count ? rows[rows.Count - 1 - A.Value.Row] : null;
        var a = r != null && n < r.Cards.Count ? r.Cards[n] : null;
        if (a == null) return false;
        G.UseAction(a, G.OwnSelection(), shift);
        return true;
    }

    /// <summary>the passengers' menu (CommandBar.usl InitMenuCommands "SLOT"): a row per transporter (at most 4), its
    /// passengers and its capacity</summary>
    public List<(Entity T, List<Unit> Pax, int Cap)> SlotRows() =>
        G.OwnSelection().Where(e => W.Capacity(e) > 0).Take(4)
            .Select(t => (t, W.PassengersOf(t)?.Where(p => p.Alive).ToList() ?? new List<Unit>(), (int)W.Capacity(t))).ToList();

    // ------------------------------------------------------------------ pyramid cards
    readonly Dictionary<int, double> _idleSince = new();
    /// <summary>what a unit is doing, shown as a layer on its card like the original (FightingObj.SetTaskDescription ->
    /// PyramidCard.usl "card_task_&lt;desc&gt;"): food / wood / stone while gathering, buildup, repair, fight, transport
    /// (inside a transporter), and after 2 s without a task "idle" (zzz) for workers, carts and fishing boats, else "wait"</summary>
    public string? TaskIcon(Entity e)
    {
        if (e is not Unit u) return null;
        if (u.Inside) return "transport";
        var t = u.Task;
        bool idle = t == null || t.Type == "idle" || t.Type == "hold";
        if (!idle) _idleSince.Remove(u.Id);
        if (idle)
        {
            if (!_idleSince.TryGetValue(u.Id, out var since)) { _idleSince[u.Id] = since = W.Time; }
            if (W.Time - since < 2) return null;
            return Regex.IsMatch(u.Name, "_worker|_cart|_fishing_boat|^aje_trade_dino$") ? "idle" : "wait";
        }
        // (the sequel's workers at a power plant: their own icon, the power symbol - the original has none for it)
        if (t!.Type == "gather" && (t.Res == "power" || t.FarmKind == "power")) return "power";
        if (t.Type == "gather" || t.Type == "fish" || t.Type == "trade") return t.Res == "wood" || t.Res == "stone" ? t.Res : "food";
        if (t.Type == "build") return "buildup";
        if (t.Type == "repair") return "repair";
        if (t.Type == "attack" || (t.Type == "attackmove" && t.Target != null)) return "fight";
        return null;
    }

    /// <summary>the places per row the last time the cards were asked (hud.js rowN)</summary>
    public int[] RowN = PYR.Select(r => r.Length).ToArray();

    /// <summary>the cards of the army pyramid: the player's units (and the buildings that count, the floating harbour) by
    /// level, then the units being trained at their level; the free places beside the pyramid as Free</summary>
    public List<PyrCard> PyramidCards(out List<(int Level, PyrRect R)> free, out List<(int Level, PyrRect R)> compactRows)
    {
        var p = Me;
        var byLevel = new List<Entity>[5];
        var queued = new List<QueueItem>[5];
        for (int i = 0; i < 5; i++) { byLevel[i] = new List<Entity>(); queued[i] = new List<QueueItem>(); }
        foreach (var u in W.Units) if (u.Alive && u.Owner == p && u.CountsInPop) byLevel[Math.Clamp(u.Level, 1, 5) - 1].Add(u);
        foreach (var b in W.Buildings) if (b.Alive && b.Owner == p && b.CountsInPop) byLevel[Math.Clamp(b.Level, 1, 5) - 1].Add(b);     // the floating harbour
        foreach (var e in W.Buildings.Cast<Entity>().Concat(W.Units))
            if (e.Alive && e.Owner == p) foreach (var q in e.Queue) if (q.Level > 0) queued[Math.Clamp(q.Level, 1, 5) - 1].Add(q);
        // the places of each row: the pyramid's own, more beside it when a debug option allows more (game/cheats.js)
        var lim = W.PyramidFor(p);
        RowN = Enumerable.Range(0, 5).Select(lv => Math.Max(PYR[lv].Length, Math.Max(lv < lim.Length ? (int)lim[lv] : 0, byLevel[lv].Count + queued[lv].Count))).ToArray();
        var res = new List<PyrCard>();
        free = new List<(int, PyrRect)>();
        compactRows = new List<(int, PyrRect)>();
        var sel = new HashSet<Entity>(H.Selection);
        for (int lv = 0; lv < 5; lv++)
        {
            var slots = SlotRects(lv, RowN[lv], out bool compact);
            if (compact)
            {
                // the compact row covers the pyramid picture's own (big) frames of the row
                var own = PYR[lv]; int top = own.Min(r => r.Y), bottom = own.Max(r => r.Y + r.H);
                compactRows.Add((lv, new PyrRect(4, top - 3, 167, bottom - top + 6)));
            }
            var list = byLevel[lv].OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            for (int i = 0; i < slots.Count; i++)
            {
                var r = slots[i];
                var u = i < list.Count ? list[i] : null;
                var q = u == null && i - list.Count < queued[lv].Count ? queued[lv][i - list.Count] : null;
                if (u == null && q == null)
                {
                    // a free place beside the pyramid (it has no picture of its own)
                    if (i >= PYR[lv].Length || compact) free.Add((lv, r));
                    continue;
                }
                var name = u != null ? u.Name : q!.Action.Results[0].Obj;
                var c = new PyrCard { Level = lv, Index = i, R = r, E = u, Queued = q, Name = name, Icon = UiAtlas.IconFor(name) };
                if (u != null)
                {
                    c.Hp = Math.Clamp(u.MaxHp > 0 ? u.Hp / u.MaxHp : 1, 0, 1);
                    c.HpSlice = u.MaxHp > 0 ? HpBarRule.Slice(u.Hp, u.MaxHp) : 100;
                    c.Task = TaskIcon(u);
                    c.Selected = sel.Contains(u);
                    c.CanLevelUp = u is Unit uu && lv < 4 && p["skulls"] >= D.LevelupSkulls[lv] && W.CanHaveLevel(uu, lv + 2);
                }
                res.Add(c);
            }
        }
        return res;
    }
    /// <summary>the pyramid's head line: units / unit limit</summary>
    public string PyramidHead() => $"{W.Units.Count(u => u.Alive && u.Owner == Me && u.CountsInPop) + W.Buildings.Count(b => b.Alive && b.Owner == Me && b.CountsInPop)} / {Fmt(Me.MaxUnits)}";

    /// <summary>The places of row lv for n units: the pyramid's own, then more to the right of the pyramid on the row's
    /// lines (a debug option's bigger pyramid, game/cheats.js). Rows 3-5 (big cards): first more places inside the row
    /// itself, as cards of the first row's size (27 px, the pyramid's five columns, as many lines as the row's height takes:
    /// row 3 2 x 5, row 4 1 x 5, row 5 2 x 5); only what does not fit there goes beside the pyramid. The compact row has
    /// no picture of its own: every free place of it gets a frame (compact).</summary>
    public static List<PyrRect> SlotRects(int lv, int n, out bool compact)
    {
        var own = PYR[lv];
        compact = false;
        if (!(n > own.Length)) return own.ToList();
        var bas = own.ToList();
        if (lv >= 2)
        {
            int S = PYR[0][0].W; int[] COLS = { 8, 41, 74, 107, 140 };
            int top = own.Min(r => r.Y), bottom = own.Max(r => r.Y + r.H), Hh = bottom - top;
            int lines = Math.Max(1, (int)Math.Floor((double)(Hh - S) / (S + 3)) + 1);
            double pitch = lines > 1 ? Math.Min(36, (double)(Hh - S) / (lines - 1)) : 0, y0 = top + (Hh - S - (lines - 1) * pitch) / 2;
            if (lines * COLS.Length > own.Length)
            {
                bas = new List<PyrRect>();
                for (int l = 0; l < lines; l++) foreach (var x in COLS) bas.Add(new PyrRect(x, (int)Math.Round(y0 + l * pitch, MidpointRounding.AwayFromZero), S, S));
                compact = true;
            }
        }
        var outl = bas.Take(Math.Min(n, bas.Count)).ToList();
        if (n <= bas.Count) return outl;
        var ys = bas.Select(r => r.Y).Distinct().ToList();
        int w = bas[0].W, h = bas[0].H;
        for (int j = 0; j < n - bas.Count; j++) outl.Add(new PyrRect(184 + j / ys.Count * (w + 5), ys[j % ys.Count], w, h));
        return outl;
    }

    /// <summary>the place under a point of the pyramid picture (its pixels), or null</summary>
    public (int Lv, int I)? PyramidSlotAt(double x, double y)
    {
        for (int lv = 0; lv < 5; lv++)
        {
            var rs = SlotRects(lv, RowN[lv], out _);
            int i = rs.FindIndex(s => s.Contains(x, y, 3));
            if (i >= 0) return (lv, i);
        }
        return null;
    }

    /// <summary>what dropping a card of unit u on place (lv, i) would do (NewPyramid.usl SwapSlotsAndCards: up only, onto a
    /// free slot): (ok, tooltip)</summary>
    public (bool Ok, string Tip) DropCheck(Unit u, (int Lv, int I) target, Entity? other)
    {
        if (target.Lv + 1 == u.Level) return (true, "");
        double cost = target.Lv + 1 > u.Level ? W.SkullCost(u.Level, target.Lv + 1) : 0;
        bool ok = target.Lv + 1 > u.Level && other == null && Me["skulls"] >= cost;
        var tip = target.Lv + 1 < u.Level ? REASON_DOWN : other != null ? "That slot is taken" : $"Level up to {target.Lv + 1}{(cost != 0 ? $" · {Fmt(cost)} skulls" : "")}";
        return (ok, tip);
    }
}
