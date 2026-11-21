// The map editor's object panels (docs/MAP_EDITOR.md "Objects"): the palette in the Tools window (a search field, the
// categories - All, MIRAGE and the palette's - and the list of classes with the HUD's portrait icons, as the game's
// card sheets show them; a click starts placing that class) and the properties of the object tool: the options of
// new objects (owner, level, random heading, snap) and the selected objects' name for the triggers, owner, level, hit
// points %, amount, a nest's species, heading - each change one undo step.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Maps;
using PwRemake.UI;
using PwRemake.UI.Mission;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.Editor;

public partial class MapEditor
{
    static bool s_icons;

    /// <summary>the HUD's portrait icons (the card sheets' atlas) and the names of the cards the tech tree gives</summary>
    static void InitIcons()
    {
        if (s_icons) return;
        s_icons = true;
        try
        {
            if (UiAtlas.Count == 0) HudArt.Init(GameApp.Assets);
            var D = EditorSession.Rules;
            UiAtlas.CardName = name => { var d = D.Exists(name) ? D.Def(name, null) : null; return d != null && !string.IsNullOrEmpty(d.Desc) ? (d.Desc[0] == '_' ? d.Desc[1..] : d.Desc) : null; };
        }
        catch (Exception e) { L.Warn($"editor icons: {e.Message}"); }
    }

    static Texture2D? IconOf(PaletteEntry e)
    {
        var name = UiAtlas.IconFor(e.Cls);
        return name != null ? HudArt.Atlas(name, 2) : null;
    }

    static string OwnerText(int? o) => o is int i ? $"Player {i + 1}" : "Nobody";

    // ================================================================================ the palette (Tools window)

    /// <summary>the Objects group's palette: search, category, the classes (a click: place it)</summary>
    void BuildObjectPalette()
    {
        InitIcons();
        var pal = ObjectToolState.PaletteOf();
        var search = new LineEdit { PlaceholderText = "Search (name, class, tribe)", Text = ObjectToolState.Query, ClearButtonEnabled = true, CustomMinimumSize = new Vector2(210, 0) };
        _palette.AddChild(search);
        var cat = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(210, 0) };
        var cats = new List<string> { ObjectPalette.ALL };
        if (pal.Any(e => e.Mirage)) cats.Add(ObjectPalette.MIRAGE);
        cats.AddRange(ObjectPalette.CATEGORIES);
        foreach (var c in cats) cat.AddItem(c == ObjectPalette.ALL ? $"All ({pal.Count})" : c == ObjectPalette.MIRAGE ? $"MIRAGE ({pal.Count(e => e.Mirage)})" : $"{c} ({pal.Count(e => e.Category == c)})");
        cat.Select(Math.Max(0, cats.IndexOf(ObjectToolState.Category)));
        _palette.AddChild(cat);
        var list = new ItemList
        {
            CustomMinimumSize = new Vector2(210, 380), FixedIconSize = new Vector2I(30, 30), SameColumnWidth = true, AllowReselect = true,
            FocusMode = Control.FocusModeEnum.None, TooltipText = "Click: place it (Shift + click on the map: more of it; wheel or Q / E: turn; Esc: done)",
        };
        list.AddThemeFontSizeOverride("font_size", 13);
        _palette.AddChild(list);
        var count = PwTheme.Para("", 12);
        _palette.AddChild(count);
        List<PaletteEntry> shown = new();
        void Fill()
        {
            list.Clear();
            shown = ObjectPalette.Filter(pal, ObjectToolState.Category, ObjectToolState.Query);
            string? group = null;
            foreach (var e in shown)
            {
                string g = $"{e.Category} · {e.Group}";
                if (g != group && ObjectToolState.Category != ObjectPalette.ALL && ObjectToolState.Category != ObjectPalette.MIRAGE)
                {
                    int hi = list.AddItem("— " + (e.Group.Length > 0 ? e.Group : e.Category) + " —", null, false);
                    list.SetItemDisabled(hi, true);
                    list.SetItemCustomFgColor(hi, PwTheme.Hi);
                }
                group = g;
                int i = list.AddItem(e.Name + (e.Mirage ? "  ·M" : ""), IconOf(e));
                list.SetItemMetadata(i, e.Cls);
                list.SetItemTooltip(i, $"{e.Name}\n{e.Cls} ({e.Type})\n{e.Category} · {e.Group}{(e.Mirage ? " · MIRAGE" : "")}{(e.Model == null ? "\n(no model: shown as a marker)" : "")}");
                if (ObjectToolState.Placing?.Cls == e.Cls) list.Select(i);
            }
            count.Text = $"{shown.Count} classes" + (ObjectToolState.Placing != null ? $" · placing {ObjectToolState.Placing.Name}" : "");
        }
        search.TextChanged += q => { ObjectToolState.Query = q; Fill(); };
        cat.ItemSelected += i => { ObjectToolState.Category = cats[(int)i]; Fill(); };
        list.ItemSelected += i =>
        {
            if (list.GetItemMetadata((int)i).AsString() is { Length: > 0 } cls && ObjectToolState.EntryOf(cls) is { } e) { StartPlacing(e); count.Text = $"{shown.Count} classes · placing {e.Name}"; }
        };
        Fill();
    }

    // ================================================================================ the properties (Properties window)

    HBoxContainer Row(string label, Control c, float lw = 92)
    {
        var r = new HBoxContainer();
        r.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(lw, 0) });
        c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        r.AddChild(c);
        return r;
    }

    static OptionButton Options(IEnumerable<string> items, int selected, Action<int> set)
    {
        var o = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var t in items) o.AddItem(t);
        o.Select(Math.Clamp(selected, -1, o.ItemCount - 1));
        o.ItemSelected += i => set((int)i);
        return o;
    }

    static SpinBox Spin(double min, double max, double step, double value, Action<double> set, string suffix = "")
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, SelectAllOnFocus = true };
        s.ValueChanged += v => set(v);
        return s;
    }

    static readonly string[] OWNERS = new[] { "Nobody" }.Concat(Enumerable.Range(1, MapPlayerSettings.SLOTS).Select(i => $"Player {i}")).ToArray();

    /// <summary>the Objects group's properties: placing options, the selection</summary>
    internal void BuildObjectProps()
    {
        var placing = ObjectToolState.Placing;
        _props.AddChild(Caption(placing != null ? $"Placing: {placing.Name}" : "Objects"));
        // ---- new objects
        _props.AddChild(Row("Owner", Options(OWNERS, ObjectToolState.NewOwner is int no ? no + 1 : 0, i => ObjectToolState.NewOwner = i == 0 ? null : i - 1)));
        _props.AddChild(Row("Level", Spin(1, 5, 1, ObjectToolState.NewLevel, v => ObjectToolState.NewLevel = (int)v)));
        var opts = new HBoxContainer();
        var rnd = new CheckBox { Text = "Random heading", ButtonPressed = ObjectToolState.RandomRot, FocusMode = Control.FocusModeEnum.None };
        rnd.Toggled += on => { ObjectToolState.RandomRot = on; if (on) ObjectToolState.GhostRot = ObjectToolState.RandomHeading(); };
        opts.AddChild(rnd);
        _props.AddChild(opts);
        double[] snaps = { 0, 1, 2, 4, 8, 16 };
        _props.AddChild(Row("Snap", Options(snaps.Select(s => s == 0 ? "Off" : $"{s} m grid"), Array.IndexOf(snaps, ObjectToolState.Snap), i => ObjectToolState.Snap = snaps[i])));
        if (placing != null)
        {
            _props.AddChild(PwTheme.Para("Click: put it down · Shift + click: more · wheel, Q / E: turn (Shift: 45°) · Esc, right click: done", 12));
            return;
        }
        // ---- the selection
        _props.AddChild(new HSeparator());
        if (Sel.Count == 0)
        {
            _props.AddChild(PwTheme.Para("Pick a class in the palette to place it. Click an object to select it (Ctrl: more), drag a box over several. Drag the selection to move it; Q / E turn it; Delete; Ctrl+C, Ctrl+V, Ctrl+D.", 12));
            return;
        }
        var o = Sel[0];
        var pe = ObjectToolState.EntryOf(o.Cls);
        var head = new HBoxContainer();
        if (pe != null && IconOf(pe) is Texture2D ic) head.AddChild(new TextureRect { Texture = ic, CustomMinimumSize = new Vector2(37, 37), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
        // (a wrapping label in a row needs its share of the width: without it the row gave it none - one letter per line)
        var headText = PwTheme.Para(Sel.Count == 1 ? $"{pe?.Name ?? o.Cls}\n{o.Cls} · {o.Type}" : $"{Sel.Count} objects selected\n{string.Join(", ", Sel.Select(x => x.Cls).Distinct().Take(3))}{(Sel.Select(x => x.Cls).Distinct().Count() > 3 ? " …" : "")}", 13, PwTheme.Text);
        headText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(headText);
        _props.AddChild(head);
        if (Sel.Count == 1)
        {
            var name = new LineEdit { Text = o.Name, TooltipText = "The object's name for the triggers (unique on the map; the class's name: none)" };
            void Rename() { var n = name.Text.Trim(); if (n.Length == 0) n = o.Cls; if (n != o.Name) EditSelection("Rename object", x => x.Name = ObjectOps.UniqueName(Doc, n, x.Cls, x)); }
            name.TextSubmitted += _ => Rename();
            name.FocusExited += Rename;
            _props.AddChild(Row("Name", name));
        }
        bool owned = Sel.Any(x => ObjectToolState.EntryOf(x.Cls)?.Owned ?? x.Owner != null) || Sel.Any(x => x.Type == "SLOC");
        if (owned)
        {
            int cur = Sel.Select(x => x.Owner).Distinct().Count() == 1 ? (o.Owner is int ow ? ow + 1 : 0) : -1;
            _props.AddChild(Row(o.Type == "SLOC" && Sel.Count == 1 ? "Player" : "Owner", Options(o.Type == "SLOC" ? OWNERS.Skip(1) : OWNERS, o.Type == "SLOC" ? cur - 1 : cur,
                i => EditSelection("Change owner", x => x.Owner = x.Type == "SLOC" ? i : i == 0 ? null : i - 1))));
        }
        var levels = Sel.Select(x => ObjectToolState.EntryOf(x.Cls)).Where(e => e != null && e.MaxLevel > 0).ToList();
        if (levels.Count > 0)
        {
            int lo = levels.Max(e => e!.MinLevel), hi = levels.Min(e => e!.MaxLevel);
            if (hi >= lo)
                _props.AddChild(Row("Level", Spin(lo, hi, 1, Math.Clamp(o.Level + 1, lo, hi), v => EditSelection("Change level", x => x.Level = (int)v - 1))));
        }
        if (Sel.All(x => ObjectToolState.EntryOf(x.Cls)?.Living ?? false) && Sel.All(x => x.Type != "FNTN"))
        {
            var hp = Spin(1, 100, 1, ObjectOps.HpPercent(o), _ => { }, " %");
            hp.ValueChanged += v => EditSelection("Change hit points", x => ObjectOps.SetHpPercent(x, v));
            _props.AddChild(Row("Hit points", hp));
        }
        if (Sel.All(x => x.Type is "STON" or "FRUI"))
            _props.AddChild(Row("Amount", Spin(1, 100000, 10, ObjectOps.Amount(o, ObjectToolState.EntryOf(o.Cls)?.Amount ?? 0), v => EditSelection("Change amount", x => x.Attr["hitpoints"] = v.ToString(CultureInfo.InvariantCulture)))));
        if (Sel.Count == 1 && o.Type == "NEST")
        {
            var R = EditorSession.Rules;
            var species = (ObjectPalette.NEST_SPECIES.TryGetValue(o.Cls, out var l) ? l.Where(R.Exists) : Enumerable.Empty<string>()).Concat(ObjectPalette.WildSpecies(R)).Distinct().ToList();
            string cur = o.AttrOr("spawn_type");
            if (cur.Length > 0 && !species.Contains(cur)) species.Insert(0, cur);
            _props.AddChild(Row("Species", Options(species.Select(s => PwRemake.Sim.Rules.Rules.Prettify(s)), species.IndexOf(cur), i => EditSelection("Change nest", x => x.Attr["spawn_type"] = species[i]))));
            double Num(string k, double d) => double.TryParse(o.AttrOr(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
            _props.AddChild(Row("At most", Spin(0, 50, 1, Num("spawn_max", 3), v => EditSelection("Change nest", x => x.Attr["spawn_max"] = v.ToString(CultureInfo.InvariantCulture)), " animals")));
            _props.AddChild(Row("One every", Spin(5, 3600, 5, Num("spawn_rate", 60), v => EditSelection("Change nest", x => x.Attr["spawn_rate"] = v.ToString(CultureInfo.InvariantCulture)), " s")));
            _props.AddChild(Row("Born before", Spin(0, 36000, 30, Num("advance_time", 0), v => EditSelection("Change nest", x => x.Attr["advance_time"] = v.ToString(CultureInfo.InvariantCulture)), " s")));
        }
        if (Sel.Count == 1 && o.Type == "ITSP")
        {
            var items = new LineEdit { Text = o.AttrOr("spawn_items"), PlaceholderText = "item classes, | between" };
            void SetItems() { if (items.Text != o.AttrOr("spawn_items")) EditSelection("Change item spawn", x => x.Attr["spawn_items"] = items.Text.Trim()); }
            items.TextSubmitted += _ => SetItems(); items.FocusExited += SetItems;
            _props.AddChild(Row("Items", items));
        }
        if (Sel.Count == 1)
        {
            double deg = Math.Round(ObjectOps.Norm(o.Rot) * 180 / Math.PI);
            _props.AddChild(Row("Heading", Spin(-180, 180, 1, deg, v => EditSelection("Turn objects", x => ObjectOps.SetRot(x, v * Math.PI / 180)), "°")));
            _props.AddChild(PwTheme.Para($"x {o.X:0.0}  y {o.Y:0.0}  height {o.Z:0.0} m", 12));
        }
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 4);
        bar.AddChild(PwTheme.Btn("Duplicate", DuplicateSelection, 92));
        bar.AddChild(PwTheme.Btn("Copy", CopySelection, 70));
        bar.AddChild(PwTheme.Btn("Delete", DeleteSelection, 80));
        _props.AddChild(bar);
    }
}
