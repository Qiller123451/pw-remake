// A tab control in the original's look (CTabCtrl; art controls/tabctrl_*): a row of tabs - the active one open to its
// page, the others darker behind the frame's top line -, the frame (corner, top line, side lines, bottom) around the
// page. AddTab(caption, tooltip, page) adds a tab whose page is built when it is first shown; Select(i) shows a page.
// Without the art the tabs are plain toggle buttons and the frame thin lines.
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.UI;

public partial class PwTabs : VBoxContainer
{
    const int ROW = 27, EDGE = 6;
    readonly HBoxContainer _row = new() { CustomMinimumSize = new Vector2(0, ROW) };
    readonly MarginContainer _page = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
    readonly ButtonGroup _group = new();
    readonly List<(Button Tab, Func<Control> Make, Control? Page)> _tabs = new();
    public int Current { get; private set; } = -1;
    /// <summary>a tab was chosen (its index)</summary>
    public Action<int>? Changed;

    public PwTabs()
    {
        AddThemeConstantOverride("separation", 0);
        _row.AddThemeConstantOverride("separation", 0);
        _row.AddChild(new Control { CustomMinimumSize = new Vector2(EDGE, 0) });
        AddChild(_row);
        foreach (var (side, v) in new[] { ("left", 14), ("right", 14), ("top", 10), ("bottom", 12) }) _page.AddThemeConstantOverride("margin_" + side, v);
        AddChild(_page);
    }

    public Button AddTab(string caption, string tooltip, Func<Control> page)
    {
        var b = new Button { Text = caption, ToggleMode = true, ButtonGroup = _group, ThemeTypeVariation = "PwTab", TooltipText = tooltip, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, ROW) };
        int i = _tabs.Count;
        b.Pressed += () => Select(i);
        _row.AddChild(b);
        _tabs.Add((b, page, null));
        return b;
    }

    public void Select(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        var (tab, make, page) = _tabs[i];
        if (page == null) { page = make(); _tabs[i] = (tab, make, page); _page.AddChild(page); }
        for (int k = 0; k < _tabs.Count; k++) if (_tabs[k].Page is { } p) p.Visible = k == i;
        tab.SetPressedNoSignal(true);
        Current = i;
        QueueRedraw();
        Changed?.Invoke(i);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized || what == NotificationSortChildren) QueueRedraw();
    }

    public override void _Draw()
    {
        float w = Size.X, h = Size.Y;
        var tl = PwTheme.Tex("controls__tabctrl_topleft"); var tc = PwTheme.Tex("controls__tabctrl_topcenter"); var tr = PwTheme.Tex("controls__tabctrl_topright");
        var cl = PwTheme.Tex("controls__tabctrl_centerleft"); var cr = PwTheme.Tex("controls__tabctrl_centerright");
        var bl = PwTheme.Tex("controls__tabctrl_bottomleft"); var bc = PwTheme.Tex("controls__tabctrl_bottomcenter"); var br = PwTheme.Tex("controls__tabctrl_bottomright");
        // the active tab's place in the row: the top line is open there
        float ax = -1, aw = 0;
        if (Current >= 0) { var t = _tabs[Current].Tab; ax = t.Position.X; aw = t.Size.X; }
        if (tl == null || tc == null || tr == null || cl == null || cr == null || bl == null || bc == null || br == null)
        {
            var c = new Color(PwTheme.Dim, 0.8f);
            DrawLine(new Vector2(0, ROW - 1), new Vector2(ax < 0 ? w : ax, ROW - 1), c);
            if (ax >= 0) DrawLine(new Vector2(ax + aw, ROW - 1), new Vector2(w, ROW - 1), c);
            DrawPolyline(new[] { new Vector2(0, ROW - 1), new Vector2(0, h - 1), new Vector2(w - 1, h - 1), new Vector2(w - 1, ROW - 1) }, c);
            return;
        }
        DrawTexture(tl, Vector2.Zero);
        void Line(float x0, float x1) { if (x1 > x0) DrawTextureRect(tc, new Rect2(x0, 0, x1 - x0, ROW), true); }
        if (ax < 0) Line(EDGE, w - EDGE);
        else { Line(EDGE, ax); Line(ax + aw, w - EDGE); }
        DrawTexture(tr, new Vector2(w - tr.GetWidth(), 0));
        // the sides and the bottom of the page
        DrawTextureRect(cl, new Rect2(0, ROW, cl.GetWidth(), h - ROW - bl.GetHeight()), true);
        DrawTextureRect(cr, new Rect2(w - cr.GetWidth(), ROW, cr.GetWidth(), h - ROW - br.GetHeight()), true);
        DrawTexture(bl, new Vector2(0, h - bl.GetHeight()));
        DrawTextureRect(bc, new Rect2(bl.GetWidth(), h - bc.GetHeight(), w - bl.GetWidth() - br.GetWidth(), bc.GetHeight()), true);
        DrawTexture(br, new Vector2(w - br.GetWidth(), h - br.GetHeight()));
    }
}
