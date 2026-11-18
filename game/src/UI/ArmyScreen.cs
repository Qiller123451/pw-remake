// "My Army": buying the start army with the game's points (the original's PointBuyMenu: PyramidDialog on the left,
// ShoppingList on the right, the PresetWindow's presets; docs/spec/pointbuy.md). Opened from the skirmish setup
// (SkirmishMenu) and from the campaign menu for missions with an army budget (CampaignArmy).
//
// The original's art: the HUD's open army pyramid (hud_pyramid_open.png) with the cards of the atlas at the HUD's
// places (HudModel.PYR, ICON_LVL; card_fixed over the fixed ones), the "buy at level n" arrows
// (menue__controls__pointbuy_addarrow*.png), the resource buttons (pointbuy_resources.png: per resource a "-" and a
// "+", rows idle / down / disabled of 37 px), the point gauge (pointgauge_empty / _full.png). Left drag: a card to
// another place (moves up when the points allow), out of the pyramid: sold; right click: sold. The shop: a click picks
// a class (the arrows show its price per level), a double click buys it at the lowest level with a free place.
//
// Ported from remake/src/ui/pointbuy.js (pw-exporter 98c7703); the rules are game/pointbuy.js (src/Sim/Game/PointBuy.cs).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.UI.Hud;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.Mission;
using PwRemake.UI.Mission.Logic;
using SimRules = PwRemake.Sim.Rules.Rules;

namespace PwRemake.UI;

public partial class ArmyScreen : MenuScreen
{
    static readonly Logger L = Log.For("UI.Army");

    public sealed class Options
    {
        public string Tribe = "Hu";
        /// <summary>the game's points, and the points to spend (with the gift and the Aje collector's price)</summary>
        public double Credits, Total;
        /// <summary>the budget the gauge names (null: Total)</summary>
        public double? Shown;
        public Army Army = new();
        /// <summary>multiplayer rules: presets, Clear, 30 food</summary>
        public bool Mp;
        public bool Buildings = true;
        /// <summary>the classes the shop offers (null: all)</summary>
        public ISet<string>? Allowed;
        /// <summary>the most cards per level (null: the pyramid's places)</summary>
        public int?[]? Limits;
        /// <summary>the Default button (null: none)</summary>
        public Func<Army>? Defaults;
        public string? Title;
        /// <summary>the debug switches "Free" and "All units" (the campaign's army screen)</summary>
        public bool DebugFree;
        public Action<Army> Done = _ => { };
        public Action Back = () => { };
    }

    /// <summary>a budget no army reaches (the debug "Free": the gauge says "Free")</summary>
    const double FREE_POINTS = 1e7;
    /// <summary>pointbuy_resources.png: per resource a "-" and a "+" button (x, width)</summary>
    static readonly (int X, int W)[] RES_BTN = { (0, 35), (35, 33), (68, 33), (101, 33), (134, 33), (167, 35) };
    /// <summary>the "buy at level n" arrows beside the pyramid (PD:96-124: y 431 / 281 / 177 / 99 / 25 for the levels 1..5)</summary>
    static readonly int[] ARROW_Y = { 431, 281, 177, 99, 25 };
    static readonly string[] TABS = { "Infantry", "Cavalry", "Heroes" };

    readonly Options _o;
    readonly SimRules D;
    readonly PointBuy PB;
    Army _army;
    readonly int[] _blocked;
    bool _free, _all;
    List<ShopEntry> _shop;
    int _tab;
    string? _sel;

    PyramidView _pyr = null!;
    ResourceView _res = null!;
    GaugeView _gauge = null!;
    ItemList _list = null!;
    Label _desc = null!;
    readonly List<Button> _tabs = new();
    OptionButton? _pre;
    LineEdit? _preName;
    Button? _del, _freeBtn, _allBtn;
    Button _done = null!;

    public ArmyScreen(Options o) : base("My Army", minWidth: 900)
    {
        _o = o;
        D = SkirmishMenu.RulesOf(GameApp.Config!);
        PB = new PointBuy(D, null);
        _army = o.Army;
        _blocked = Enumerable.Range(0, 5).Select(i => o.Limits != null && i < o.Limits.Length && o.Limits[i] is int m ? Math.Max(0, PointBuy.SLOT_LEVELS[i][1] - PointBuy.SLOT_LEVELS[i][0] + 1 - m) : 0).ToArray();
        _shop = PB.Shop(o.Tribe, o.Allowed);
        _sel = _shop.FirstOrDefault()?.Cls;
    }

    double Tot => _free ? FREE_POINTS : _o.Total;
    double Left => PB.Left(_army, Tot, _o.Tribe);
    string Ui(string k, string d) => D.Ui(k, d);

    protected override void Build()
    {
        HudArt.Init(GameApp.Assets);
        UiAtlas.CardName = name => { var d = D.Exists(name) ? D.Def(name, null) : null; return d != null && !string.IsNullOrEmpty(d.Desc) ? (d.Desc[0] == '_' ? d.Desc[1..] : d.Desc) : null; };
        var b = Window.Body;
        b.AddChild(PwTheme.Heading($"{_o.Title ?? Ui("_MyArmy", "My Army")} · {TribeInfo.Name(_o.Tribe)}", 20));
        var page = new HBoxContainer();
        page.AddThemeConstantOverride("separation", 24);
        b.AddChild(page);

        // ---- left: the pyramid with its arrows, the resources, the tools
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 8);
        page.AddChild(left);
        _pyr = new PyramidView(this);
        left.AddChild(_pyr);
        _res = new ResourceView(this);
        left.AddChild(_res);
        var tools = new HBoxContainer();
        tools.AddThemeConstantOverride("separation", 6);
        left.AddChild(tools);
        if (_o.Mp) tools.AddChild(Small(Ui("_PointBuyMenu_Clear", "Clear"), () => { _army = PB.Clear(_o.Tribe, _o.Buildings); Refresh(); }, Ui("_UI_AC_PointBuy_Clear", "")));
        if (_o.DebugFree)
        {
            _freeBtn = Small("Free", () => { _free = !_free; Refresh(); }, "Debug: every unit, hero and resource costs nothing (click again to pay normally)");
            _freeBtn.ToggleMode = true;
            tools.AddChild(_freeBtn);
            if (_o.Allowed != null)
            {
                _allBtn = Small("All units", () => { _all = !_all; _shop = PB.Shop(_o.Tribe, _all ? null : _o.Allowed); if (!_shop.Any(x => x.Cls == _sel)) _sel = (_shop.FirstOrDefault(x => x.Tab == _tab) ?? _shop.FirstOrDefault())?.Cls; Refresh(); },
                    "Debug: the shop offers every unit and hero of the tribe, not only those the mission allows");
                _allBtn.ToggleMode = true;
                tools.AddChild(_allBtn);
            }
        }
        if (_o.Defaults != null) tools.AddChild(Small(Ui("_UI_PBMenu_Button_Default", "Default"), () => { _army = _o.Defaults(); Refresh(); }, Ui("_UI_AC_PointBuy_Default_TT", "")));

        // ---- right: the gauge, the shop's tabs and list, the description, the presets
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
        right.AddThemeConstantOverride("separation", 8);
        page.AddChild(right);
        _gauge = new GaugeView(this) { TooltipText = Ui("_UI_PresWind_PointGauge_TT", "") };
        right.AddChild(_gauge);
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        right.AddChild(tabs);
        for (int i = 0; i < TABS.Length; i++)
        {
            int ii = i;
            var t = new Button { Text = Ui("_UI_ShopList_Tab_" + TABS[i], TABS[i]), ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(120, 28), TooltipText = Ui($"_UI_Tab_{TABS[i]}_TT", "") };
            t.AddThemeFontSizeOverride("font_size", 14);
            t.Pressed += () => { _tab = ii; var f = _shop.FirstOrDefault(x => x.Tab == _tab); if (f != null) _sel = f.Cls; Refresh(); };
            tabs.AddChild(t);
            _tabs.Add(t);
        }
        _list = new ItemList { CustomMinimumSize = new Vector2(440, 300), FixedIconSize = new Vector2I(26, 25), TooltipText = Ui("_UI_AC_PointBuy_ShoppingList_Buy", "") };
        _list.ItemSelected += i => { _sel = _list.GetItemMetadata((int)i).AsString(); Describe(_sel); _pyr.QueueRedraw(); };
        _list.ItemActivated += i => { if (PB.BuyLowest(_army, _list.GetItemMetadata((int)i).AsString(), Tot, _o.Tribe, _blocked) != null) Refresh(); };
        right.AddChild(_list);
        _desc = PwTheme.Para("", 13, PwTheme.Text);
        _desc.CustomMinimumSize = new Vector2(440, 90);
        right.AddChild(_desc);
        if (_o.Mp)
        {
            var pr = new HBoxContainer();
            pr.AddThemeConstantOverride("separation", 6);
            right.AddChild(pr);
            pr.AddChild(new Label { Text = Ui("_UI_PresWind_Label_Preset", "Preset") });
            _pre = new OptionButton { CustomMinimumSize = new Vector2(140, 26), FocusMode = FocusModeEnum.None };
            _pre.ItemSelected += i => LoadPreset(_pre.GetItemMetadata((int)i).AsString());
            pr.AddChild(_pre);
            _preName = new LineEdit { MaxLength = 24, CustomMinimumSize = new Vector2(110, 0), TooltipText = Ui("_UI_PresWind_Edit_Name_TT", "") };
            pr.AddChild(_preName);
            pr.AddChild(Small(Ui("_UI_PresWind_Button_Save", "Save"), SavePreset, Ui("_UI_PresWind_Button_Save_TT", ""), 70));
            _del = Small(Ui("_UI_PresWind_Button_Delete", "Delete"), DeletePreset, Ui("_UI_PresWind_Button_Delete_TT", ""), 70);
            pr.AddChild(_del);
        }
        _done = PwTheme.Btn("Done", () => { if (!_free && Left < 0) return; _o.Done(_army); }, 160);
        b.AddChild(Row(PwTheme.Btn("Back", () => _o.Back(), 140), _done));
        Refresh();
    }

    static Button Small(string text, Action a, string tip = "", float w = 100)
    {
        var bt = PwTheme.Btn(text, a, w);
        bt.TooltipText = tip;
        bt.AddThemeFontSizeOverride("font_size", 14);
        return bt;
    }

    void Refresh()
    {
        _pyr.QueueRedraw();
        _res.QueueRedraw();
        _gauge.QueueRedraw();
        if (_freeBtn != null) _freeBtn.SetPressedNoSignal(_free);
        if (_allBtn != null) _allBtn.SetPressedNoSignal(_all);
        bool over = !_free && Left < 0;
        _done.Disabled = over;
        _done.TooltipText = over ? "The army costs more than the budget: sell cards or resources (or turn \"Free\" on)" : "";
        for (int i = 0; i < _tabs.Count; i++) _tabs[i].SetPressedNoSignal(i == _tab);
        FillList();
        FillPresets();
    }

    void FillList()
    {
        _list.Clear();
        foreach (var e in _shop.Where(x => x.Tab == _tab))
        {
            int lo = e.Prices == null ? -1 : Array.FindIndex(e.Prices, p => p > 0);
            bool had = PB.IsHero(e.Cls) && PB.HasHero(_army, e.Cls);
            var icon = HudArt.Atlas(UiAtlas.IconFor(e.Cls), 2);
            int i = icon != null ? _list.AddItem($"{D.Text(e.Cls).Name}   {(lo >= 0 ? e.Prices![lo].ToString() : "")}", icon) : _list.AddItem($"{D.Text(e.Cls).Name}   {(lo >= 0 ? e.Prices![lo].ToString() : "")}");
            _list.SetItemMetadata(i, e.Cls);
            if (had) _list.SetItemCustomFgColor(i, PwTheme.Dim);
            if (e.Cls == _sel) _list.Select(i);
        }
        if (_sel != null) Describe(_sel);
    }

    void Describe(string cls)
    {
        var t = D.Text(cls); var e = _shop.FirstOrDefault(x => x.Cls == cls);
        var prices = e?.Prices == null ? "" : string.Join(" · ", e.Prices.Select((p, i) => p > 0 ? $"L{i + 1}: {p}" : "").Where(s => s != ""));
        _desc.Text = $"{t.Name}\n{prices}\n{t.Medium ?? ""}";
    }

    // ------------------------------------------------------------ presets (multiplayer: PresetWindow)
    List<PbPreset> OwnPresets() => GameApp.Settings.ArmyPresets.Where(p => p.Tribe == _o.Tribe && p.Cost == _o.Credits).ToList();
    void FillPresets()
    {
        if (_pre == null) return;
        _pre.Clear();
        _pre.AddItem("—"); _pre.SetItemMetadata(0, "");
        foreach (var p in PB.Presets(_o.Tribe, _o.Credits)) { _pre.AddItem(Ui("_pb_" + p.Kind, p.Kind ?? "")); _pre.SetItemMetadata(_pre.ItemCount - 1, "d:" + p.Kind); }
        foreach (var p in OwnPresets()) { _pre.AddItem(p.Name); _pre.SetItemMetadata(_pre.ItemCount - 1, "o:" + p.Name); }
        int sel = 0;
        for (int i = 0; i < _pre.ItemCount; i++) if (_pre.GetItemMetadata(i).AsString() == _army.Preset && _army.Preset != "") sel = i;
        _pre.Select(sel);
        if (_del != null) _del.Disabled = !_army.Preset.StartsWith("o:");
    }
    void LoadPreset(string v)
    {
        if (v == "") return;
        var pre = v.StartsWith("d:") ? PB.Preset(_o.Tribe, _o.Credits, v[2..]) : OwnPresets().FirstOrDefault(p => p.Name == v[2..]);
        if (pre == null) return;
        _army = PB.Load(pre, _o.Tribe, Tot, mp: true, buildings: _o.Buildings);
        _army.Preset = v;
        Refresh();
    }
    /// <summary>an own preset: the cards (with the Aje collector) and the resources, for this tribe and budget (PW:379-461)</summary>
    void SavePreset()
    {
        var name = System.Text.RegularExpressions.Regex.Replace((_preName?.Text ?? "").Trim(), @"\s+", "_");
        if (name == "") return;
        var list = GameApp.Settings.ArmyPresets;
        list.RemoveAll(p => p.Tribe == _o.Tribe && p.Cost == _o.Credits && p.Name == name);
        list.Add(new PbPreset { Name = name, Tribe = _o.Tribe, Cost = _o.Credits, Units = _army.Cards.Select(kv => new KeyValuePair<string, string>(kv.Key.ToString(), kv.Value)).ToList(), Res = new(_army.Res) });
        GameApp.Settings.Save();
        _army.Preset = "o:" + name;
        Refresh();
    }
    void DeletePreset()
    {
        if (!_army.Preset.StartsWith("o:")) return;
        GameApp.Settings.ArmyPresets.RemoveAll(p => p.Tribe == _o.Tribe && p.Cost == _o.Credits && p.Name == _army.Preset[2..]);
        GameApp.Settings.Save();
        _army.Preset = "";
        Refresh();
    }

    // ================================================================ the pyramid and its arrows
    sealed partial class PyramidView : Control
    {
        readonly ArmyScreen A;
        int _drag = -1, _hover = -1;
        Vector2 _down, _mouse;
        bool _dragging;
        const float ARROWS = 70;

        public PyramidView(ArmyScreen a) { A = a; CustomMinimumSize = new Vector2(HudModel.PYR_W + ARROWS, HudModel.PYR_H); }

        static Rect2 Cell(int slot)
        {
            int lv = PointBuy.SlotLevel(slot);
            var r = HudModel.PYR[lv - 1][slot - PointBuy.SLOT_LEVELS[lv - 1][0]];
            return new Rect2(r.X, r.Y, r.W, r.H);
        }
        /// <summary>the slot under a point; -2: inside the pyramid but no place; -1: outside</summary>
        static int SlotAt(Vector2 p)
        {
            for (int lv = 1; lv <= 5; lv++)
                for (int i = 0; i < HudModel.PYR[lv - 1].Length; i++)
                    if (Cell(PointBuy.SLOT_LEVELS[lv - 1][0] + i).Grow(3).HasPoint(p)) return PointBuy.SLOT_LEVELS[lv - 1][0] + i;
            return p.X >= -10 && p.X <= HudModel.PYR_W + 10 && p.Y >= -10 && p.Y <= HudModel.PYR_H + 10 ? -2 : -1;
        }
        Rect2 ArrowRect(int lv) => new(HudModel.PYR_W + 6, ARROW_Y[lv - 1], 54, 35);

        public override void _Draw()
        {
            var army = A._army; var tribe = A._o.Tribe;
            if (!HudSprites.Pic(this, "hud_pyramid_open.png", new Rect2(0, 0, HudModel.PYR_W, HudModel.PYR_H)))
                DrawRect(new Rect2(0, 0, HudModel.PYR_W, HudModel.PYR_H), new Color(0.1f, 0.07f, 0.04f));
            for (int lv = 1; lv <= 5; lv++)
            {
                int a = PointBuy.SLOT_LEVELS[lv - 1][0], b = PointBuy.SLOT_LEVELS[lv - 1][1];
                for (int s = a; s <= b; s++)
                {
                    var r = Cell(s);
                    if (s > b - A._blocked[lv - 1] && !army.Cards.ContainsKey(s)) { DrawRect(r, new Color(0, 0, 0, 0.6f)); continue; }
                    if (!army.Cards.TryGetValue(s, out var name) || (_dragging && s == _drag)) continue;
                    var cls = A.PB.Resolve(name, tribe).Cls;
                    if (!HudSprites.Draw(this, UiAtlas.IconFor(cls) ?? UiAtlas.IconFor(name), HudModel.ICON_LVL[lv - 1], r))
                        DrawRect(r, new Color(0.4f, 0.3f, 0.15f));
                    if (army.IsFixed(s)) HudSprites.Draw(this, "card_fixed", HudModel.ICON_LVL[lv - 1], r);
                    if (s == _hover) DrawRect(r, PwTheme.Hi, false, 1.5f);
                }
            }
            // the arrows: the selected class's price per level
            var e = A._shop.FirstOrDefault(x => x.Cls == A._sel);
            for (int lv = 1; lv <= 5; lv++)
            {
                double price = e?.Prices != null && lv - 1 < e.Prices.Length ? e.Prices[lv - 1] : 0;
                if (price == 0) continue;
                bool can = CanBuy(lv, price);
                var r = ArrowRect(lv);
                var tex = PwTheme.Tex(can ? (r.HasPoint(_mouse) ? "controls__pointbuy_addarrow_hover" : "controls__pointbuy_addarrow") : "controls__pointbuy_addarrow_disabled");
                if (tex != null) DrawTextureRect(tex, r, false); else DrawRect(r, can ? new Color(0.5f, 0.4f, 0.2f) : new Color(0.3f, 0.3f, 0.3f));
                HudSprites.Text(this, HudSprites.Sans, new Vector2(r.Position.X + 16, r.Position.Y + 23), price.ToString(), 12, can ? PwTheme.Hi : PwTheme.Dim);
            }
            // the card being dragged
            if (_dragging && army.Cards.TryGetValue(_drag, out var dn))
            {
                var r = Cell(_drag); r.Position = _mouse - r.Size / 2;
                int to = SlotAt(_mouse);
                bool bad = to >= 0 ? !A.PB.CanMove(army, _drag, to, A.Tot, tribe) && to != _drag : to == -1 && army.IsFixed(_drag);
                HudSprites.Draw(this, UiAtlas.IconFor(A.PB.Resolve(dn, tribe).Cls), HudModel.ICON_LVL[PointBuy.SlotLevel(_drag) - 1], r, bad ? new Color(1, 0.4f, 0.4f, 0.8f) : new Color(1, 1, 1, 0.85f));
            }
        }

        bool CanBuy(int lv, double price) => price > 0 && price <= A.Left && A.PB.FreeSlot(A._army, lv, A._blocked[lv - 1]) >= 0 && !(A._sel != null && A.PB.IsHero(A._sel) && A.PB.HasHero(A._army, A._sel));

        public override void _GuiInput(InputEvent ev)
        {
            var army = A._army;
            if (ev is InputEventMouseMotion mm)
            {
                _mouse = mm.Position;
                if (_drag >= 0 && !_dragging && _mouse.DistanceTo(_down) >= 5) _dragging = true;
                int h = SlotAt(_mouse);
                if (h != _hover) { _hover = h; if (h >= 0 && army.Cards.TryGetValue(h, out var n)) { A.Describe(A.PB.Resolve(n, A._o.Tribe).Cls); TooltipText = $"{A.D.Text(A.PB.Resolve(n, A._o.Tribe).Cls).Name} · {A.PB.CardCost(n, h, A._o.Tribe)}\n{A.Ui(army.IsFixed(h) ? "_UI_AC_PointBuy_Card_Locked" : "_UI_AC_PointBuy_Card", "")}"; } else TooltipText = ""; }
                QueueRedraw();
            }
            else if (ev is InputEventMouseButton { Pressed: true } mb)
            {
                int s = SlotAt(mb.Position);
                if (mb.ButtonIndex == MouseButton.Right && s >= 0 && army.Cards.ContainsKey(s)) { if (A.PB.Sell(army, s)) A.Refresh(); return; }
                if (mb.ButtonIndex != MouseButton.Left) return;
                for (int lv = 1; lv <= 5; lv++)
                    if (ArrowRect(lv).HasPoint(mb.Position) && A._sel != null)
                    {
                        if (A.PB.Buy(army, A._sel, lv, A.Tot, A._o.Tribe, A._blocked) != null) A.Refresh();
                        return;
                    }
                if (s >= 0 && army.Cards.ContainsKey(s)) { _drag = s; _down = mb.Position; _dragging = false; }
            }
            else if (ev is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } mu && _drag >= 0)
            {
                int from = _drag; bool was = _dragging;
                _drag = -1; _dragging = false;
                if (!was) { QueueRedraw(); return; }
                int to = SlotAt(mu.Position);
                if (to >= 0 && A.PB.CanMove(army, from, to, A.Tot, A._o.Tribe)) A.PB.Move(army, from, to);
                else if (to == -1) A.PB.Sell(army, from);
                A.Refresh();
            }
        }
    }

    // ================================================================ the resources (pointbuy_resources.png)
    sealed partial class ResourceView : Control
    {
        readonly ArmyScreen A;
        int _down = -1;
        public ResourceView(ArmyScreen a) { A = a; CustomMinimumSize = new Vector2(202, 37); MouseFilter = MouseFilterEnum.Stop; }

        bool Off(int id)
        {
            var army = A._army; string r = PointBuy.RES[id / 2]; bool plus = id % 2 == 1;
            if (army.IsLocked(r)) return true;
            return plus ? A.PB.ResFor(r, A.Left + A.PB.ResCost(r, army.R(r), A._o.Tribe), A._o.Tribe) <= army.R(r) : army.R(r) <= army.MinOf(r);
        }
        int At(Vector2 p) { for (int i = 0; i < 6; i++) if (p.X >= RES_BTN[i].X && p.X < RES_BTN[i].X + RES_BTN[i].W && p.Y >= 0 && p.Y < 37) return i; return -1; }

        public override void _Draw()
        {
            var tex = PwTheme.Tex("controls__pointbuy_resources");
            for (int i = 0; i < 6; i++)
            {
                var (x, w) = RES_BTN[i];
                int row = Off(i) ? 2 : _down == i ? 1 : 0;
                if (tex != null) DrawTextureRectRegion(tex, new Rect2(x, 0, w, 37), new Rect2(x, row * 37, w, 37));
                else DrawRect(new Rect2(x, 0, w - 1, 36), new Color(0.35f, 0.28f, 0.15f));
            }
            // the amounts in the black slots
            for (int r = 0; r < 3; r++)
                HudSprites.Text(this, HudSprites.Sans, new Vector2(RES_BTN[2 * r].X + 20, 15), ((int)A._army.R(PointBuy.RES[r])).ToString(), 12, PwTheme.Hi, HorizontalAlignment.Center, 40);
        }

        public override void _GuiInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                int i = At(mb.Position);
                if (mb.Pressed) { _down = i >= 0 && !Off(i) ? i : -1; QueueRedraw(); return; }
                if (i >= 0 && i == _down && !Off(i))
                {
                    double step = (mb.ShiftPressed ? 10 : 1) * (mb.CtrlPressed ? 100 : 1);
                    A.PB.AddRes(A._army, PointBuy.RES[i / 2], i % 2 == 1 ? step : -step, A.Tot, A._o.Tribe);
                    A.Refresh();
                }
                _down = -1; QueueRedraw();
            }
            else if (ev is InputEventMouseMotion)
            {
                // the button's own tool tip (_UI_AC_PointBuy_Resource_Buy_<n>)
                int i = At(((InputEventMouseMotion)ev).Position);
                TooltipText = i >= 0 ? A.Ui("_UI_AC_PointBuy_Resource_Buy_" + i, (i % 2 == 1 ? "+" : "-") + " " + PointBuy.RES[i / 2] + " (Shift: 10, Ctrl: 100)") : "";
            }
        }
    }

    // ================================================================ the point gauge (pointgauge_empty / _full.png)
    sealed partial class GaugeView : Control
    {
        readonly ArmyScreen A;
        public GaugeView(ArmyScreen a) { A = a; CustomMinimumSize = new Vector2(280, 35); }
        public override void _Draw()
        {
            var empty = PwTheme.Tex("controls__pointgauge_empty"); var full = PwTheme.Tex("controls__pointgauge_full");
            var r = new Rect2(0, 0, 280, 35);
            double frac; string text;
            if (A._free) { frac = 1; text = $"Free (debug) · army {A.PB.Cost(A._army, A._o.Tribe)} points"; }
            else
            {
                double l = A.Left, shown = A._o.Shown ?? A.Tot;
                frac = Math.Clamp(l / Math.Max(1, A.Tot), 0, 1);
                text = A.Ui("_PointBuyMenu_PointGauge", "%1 of %2 points").Replace("%1", l.ToString()).Replace("%2", shown.ToString());
            }
            if (empty != null) DrawTextureRect(empty, r, false); else DrawRect(r, new Color(0.1f, 0.1f, 0.1f));
            float w = (float)(280 * frac);
            if (full != null) DrawTextureRectRegion(full, new Rect2(0, 0, w, 35), new Rect2(0, 0, w * full.GetWidth() / 280f, full.GetHeight()));
            else DrawRect(new Rect2(0, 0, w, 35), new Color(0.7f, 0.3f, 0.05f));
            HudSprites.Text(this, HudSprites.Sans, new Vector2(0, 22), text, 13, PwTheme.Hi, HorizontalAlignment.Center, 280);
        }
    }
}
