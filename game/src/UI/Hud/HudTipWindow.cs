// The tooltip window (IngameScreen.usl:265-268, ToolTipWindow.usl): 235 px wide, the HUD's 9-slice frame
// hud/tooltip_*.tga, pale yellow text in the original's Trebuchet MS bold in a 200 px column at (13, 7); top to
// bottom the title row on tt_bgimage_title with the class icon, the heroes' level rows with their icons, the help
// text, the requirement lines (light red), the strengths row on tt_bgimage_value and the cost row on tt_bgimage_stats
// (light red what the player can't afford).
// Its place: the original's - its bottom left corner 35 px right of the minimap's left edge and 19 px below its top,
// growing upwards, the closed tab hud/tooltip_closed.tga there while no tooltip shows - or, with the option "Tooltips
// next to what the mouse is over" (the browser remake's default), beside the thing under the mouse: below the top bar's
// things, above the bottom panels', right of what is on the left half of the screen, left of what is on the right half.
//
// Ported from remake/src/ui/tooltip.js (ttHtml, TipWindow) and the .tipwin / .tip styles of remake/index.src.html
// (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.Mission;

namespace PwRemake.UI.Hud;

public partial class HudTipWindow : Control
{
    public const int W = 235, TEXT_W = 200, PAD_L = 13, PAD_T = 7, PAD_B = 15;
    // the original's tooltip font (UI/VisDef_GAM2.vis Fonts.ToolTip: 'Trebuchet MS,16,bold,autoshadow=on' - a Windows
    // cell height of 16 px, an em of about 13 px; HudSprites.Sans: Trebuchet MS bold, Segoe UI / DejaVu Sans without it)
    const int FS = 13, LINE = 16;
    static readonly Color TEXT = HudSprites.Hex("#ffffc8"), BAD = HudSprites.Hex("#ff9a8a");

    Tip? _tip;
    readonly List<Action> _ops = new();
    float _h;

    public HudTipWindow() { Name = "TipWindow"; MouseFilter = MouseFilterEnum.Ignore; Visible = false; }

    public bool Shown => _tip != null;
    public Tip? Current => _tip;

    public void Show(Tip t)
    {
        _tip = t;
        Layout();
        Visible = true;
        QueueRedraw();
    }
    public void HideTip() { _tip = null; Visible = false; }

    // lays the lines out (drawing operations and the height)
    void Layout()
    {
        _ops.Clear();
        var t = _tip!;
        float y = PAD_T;
        float x = PAD_L;
        var f = HudSprites.Sans; var fb = HudSprites.Sans;
        if (!string.IsNullOrEmpty(t.Title))
        {
            float ty = y + 1;
            var title = HudSprites.Fit(fb, t.Title!, FS, TEXT_W - 22);
            _ops.Add(() =>
            {
                HudSprites.Draw(this, "tt_bgimage_title", -1, new Rect2(x - 1, ty, 194, 16));
                HudSprites.Draw(this, "tt_class_" + (string.IsNullOrEmpty(t.Cls) ? "other" : t.Cls!.ToLowerInvariant()), -1, new Rect2(x, ty, 16, 16));
                HudSprites.Text(this, fb, new Vector2(x + 19, ty + 12), title, FS, TEXT, HorizontalAlignment.Left, -1, 1);
            });
            y += 1 + 16 + 2;
        }
        if (t.Lvl != null)
            for (int n = 5; n >= 1; n--)
            {
                if (!t.Lvl.TryGetValue(n.ToString(), out var l) || string.IsNullOrWhiteSpace(l)) continue;
                float ly = y + 2; int nn = n; var s = l.Trim();
                float h = Measure(f, s, TEXT_W - 19);
                _ops.Add(() => { HudSprites.Draw(this, "tt_level_" + (nn - 1), -1, new Rect2(x, ly, 16, 16)); Para(f, s, new Vector2(x + 19, ly), TEXT_W - 19, TEXT); });
                y += Math.Max(16, h) + 4;
            }
        if (t.HasBody) { var s = t.Body!.Trim(); float by = y + 2; _ops.Add(() => Para(f, s, new Vector2(x, by), TEXT_W, TEXT)); y += Measure(f, s, TEXT_W) + 4; }
        foreach (var (line, bad) in t.Lines)
        {
            var s = line.Trim(); float by = y + 2; bool b = bad;
            _ops.Add(() => Para(f, s, new Vector2(x, by), TEXT_W, b ? BAD : TEXT));
            y += Measure(f, s, TEXT_W) + 4;
        }
        if (t.HasVs)
        {
            // the class icons are part of the row's picture; the qualities at its tabs (Tip.VS_X: the original's four or MIRAGE's five)
            float vy = y + 3;
            _ops.Add(() =>
            {
                HudSprites.Draw(this, "tt_bgimage_value", -1, new Rect2(x - 1, vy, 194, 16));
                var cols = Tip.VS_COLS; var xs = Tip.VS_X;
                for (int i = 0; i < cols.Length; i++)
                    if (t.Vs!.TryGetValue(cols[i], out var q) && Tip.Q.TryGetValue(q, out var qn))
                        HudSprites.Draw(this, "tt_quality_" + qn, null, new Rect2(x - 1 + xs[i], vy, 16, 16));
            });
            y += 3 + 16 + 1;
        }
        if (t.Cost != null)
        {
            float cy = y + 3;
            var c = t.Cost;
            _ops.Add(() =>
            {
                HudSprites.Draw(this, "tt_bgimage_stats", -1, new Rect2(x - 1, cy, 194, 16));
                for (int i = 0; i < 4; i++)
                {
                    var r = Tip.COST_COLS[i];
                    HudSprites.Text(this, f, new Vector2(x - 1 + Tip.COST_X[i], cy + 12), Math.Round(c[r], MidpointRounding.AwayFromZero).ToString(), FS, t.Short.Contains(r) ? BAD : TEXT, HorizontalAlignment.Left, -1, 1);
                }
            });
            y += 3 + 16 + 1;
            if (c.Power != 0)
            {
                // the sequel's electricity
                float py = y + 2; var s = "⚡ " + Math.Round(c.Power);
                _ops.Add(() => Para(f, s, new Vector2(x, py), TEXT_W, t.Short.Contains("power") ? BAD : TEXT));
                y += LINE + 4;
            }
        }
        _h = y + PAD_B;
        Size = new Vector2(W, _h);
    }

    static float Measure(Font f, string s, float w) => Math.Max(LINE, f.GetMultilineStringSize(s, HorizontalAlignment.Left, w, FS).Y);

    void Para(Font f, string s, Vector2 pos, float w, Color c)
    {
        DrawMultilineString(f, pos + new Vector2(0, FS + 1), s, HorizontalAlignment.Left, w, FS, -1, new Color(0, 0, 0, 0.85f));
        DrawMultilineString(f, pos + new Vector2(0, FS), s, HorizontalAlignment.Left, w, FS, -1, c);
    }

    public override void _Draw()
    {
        if (_tip == null) return;
        DrawStyleBox(HudArt.Tooltip(), new Rect2(0, 0, W, _h));
        foreach (var op in _ops) op();
    }
}
