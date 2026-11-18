// The end of a mission (menu.js end for a mission): the original's game-over art (menue__decoration__gameover_victory /
// _defeat, 250 x 150), the game's result line (_GAOV_Accomplished / _GAOV_Failed), the mission title, the reason of
// a defeat, the visible quests with their result and the bonus points, the statistics when the game gives them, and
// the buttons: Continue (won) or Retry, Load Game, View Statistics, Back to Main menu. A skirmish (menu.js end without
// a mission): caption "Skirmish", "You were victorious!" / "You have been defeated!", the epoch reached, and "Keep
// playing". The buttons only report (OnEndAction: next, retry, load, stats, watch, menu); the app decides what they do.
using System;
using Godot;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Mission;

public partial class EndScreenWindow : Control
{
    public EndScreenWindow(EndScreenInfo e, (string Time, int Kills, int Lost)? stats, Action<string> onAction)
    {
        Name = "EndScreen";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Theme = PwTheme.Theme;
        var dark = PwTheme.Tex("windows__darken_overlay");
        Control shade = dark != null
            ? new TextureRect { Texture = dark, StretchMode = TextureRect.StretchModeEnum.Tile, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize }
            : new ColorRect { Color = new Color(0, 0, 0, 0.55f) };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var win = new PwWindow(e.Skirmish ? "Skirmish" : "Campaign", 480, 560);
        center.AddChild(win);
        var B = win.Body;
        var art = PwTheme.Tex(e.Won ? "decoration__gameover_victory" : "decoration__gameover_defeat");
        if (art != null) B.AddChild(new TextureRect { Texture = art, CustomMinimumSize = new Vector2(250, 150), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize });
        B.AddChild(PwTheme.Heading(e.Result, 22, e.Won ? PwTheme.Won : PwTheme.Lost));
        B.AddChild(Centered(e.Title, 16, PwTheme.Dim));
        if (e.Text.Length > 0) B.AddChild(Centered(e.Text, 17, e.Won ? PwTheme.Hi : new Color(1, 0xb0 / 255f, 0x90 / 255f)));
        if (e.Quests.Count > 0)
        {
            var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(400, 0) };
            list.AddThemeConstantOverride("separation", 2);
            var sc = new ScrollContainer { CustomMinimumSize = new Vector2(420, Math.Min(150, 24 * e.Quests.Count + 4)), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
            sc.AddChild(list);
            foreach (var (q, st, pts) in e.Quests)
            {
                var h = new HBoxContainer();
                h.AddThemeConstantOverride("separation", 6);
                var color = st == "done" ? Col.Done : st == "failed" ? Col.Failed : Col.Open;
                h.AddChild(new TextureRect { Texture = QuestLogWindow.Check(st == "done" ? 1 : st == "failed" ? 2 : 0), CustomMinimumSize = new Vector2(16, 16), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
                h.AddChild(new Label { Text = q.Headline.Length > 0 ? q.Headline : q.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill, LabelSettings = HudArt.Text(14, color) });
                h.AddChild(new Label { Text = q.Accomplished && pts != 0 ? "+" + pts : "", LabelSettings = HudArt.Text(13, color) });
                list.AddChild(h);
            }
            B.AddChild(sc);
        }
        if (e.PointsLine.Length > 0) B.AddChild(Centered(e.PointsLine, 16, PwTheme.Hi));
        if (stats is { } s)
        {
            var g = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
            g.AddThemeConstantOverride("h_separation", 20);
            var rows = new System.Collections.Generic.List<(string, string)> { ("Time", s.Time), ("Enemies killed", s.Kills.ToString()), ("Units lost", s.Lost.ToString()) };
            if (e.Skirmish && e.Epoch.Length > 0) rows.Add(("Epoch reached", e.Epoch));
            foreach (var (k, v) in rows)
            {
                g.AddChild(new Label { Text = k, LabelSettings = HudArt.Text(14, PwTheme.Hi) });
                g.AddChild(new Label { Text = v, LabelSettings = HudArt.Text(14, PwTheme.Text) });
            }
            B.AddChild(g);
        }
        var btns = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        btns.AddThemeConstantOverride("separation", 8);
        B.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        B.AddChild(btns);
        if (e.Won && !e.Skirmish) btns.AddChild(PwTheme.Btn("Continue", () => onAction("next")));
        else btns.AddChild(PwTheme.Btn("Retry", () => onAction("retry")));
        btns.AddChild(PwTheme.Btn("Load Game", () => onAction("load")));
        if (e.HasStats) btns.AddChild(PwTheme.Btn("View Statistics", () => onAction("stats")));
        if (e.Skirmish) btns.AddChild(PwTheme.Btn("Keep playing", () => onAction("watch")));
        btns.AddChild(PwTheme.Btn("Back to Main menu", () => onAction("menu")));
    }

    static Label Centered(string text, int size, Color color) => new()
    {
        Text = text, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        LabelSettings = new LabelSettings { Font = HudArt.SansBold, FontSize = size, FontColor = color, ShadowColor = new Color(0, 0, 0, 0.9f), ShadowOffset = new Vector2(1, 1) },
    };
}
