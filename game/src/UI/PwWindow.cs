// A menu window in the original's frame: the outer frame's corners, its title bar strip (toptile, drawn from 27 px
// at its own width), the tiled edges and the tiled centre - drawn as the browser remake's .pwwin does
// (index.src.html), with the caption in the title bar and the content inside (padding 44 / 34 / 30 / 34).
using Godot;

namespace PwRemake.UI;

public partial class PwWindow : PanelContainer
{
    readonly string _caption;
    public VBoxContainer Body { get; }

    public PwWindow(string caption, float minWidth = 420, float maxWidth = 760)
    {
        _caption = caption;
        CustomMinimumSize = new Vector2(minWidth, 0);
        AddThemeStyleboxOverride("panel", new StyleBoxEmpty
        {
            ContentMarginLeft = 34, ContentMarginRight = 34, ContentMarginTop = 44, ContentMarginBottom = 30,
        });
        Body = new VBoxContainer { CustomMinimumSize = new Vector2(minWidth - 68, 0) };
        Body.AddThemeConstantOverride("separation", 8);
        AddChild(Body);
        _maxWidth = maxWidth;
    }

    readonly float _maxWidth;

    public override void _Ready()
    {
        // keep long texts from making the window wider than maxWidth
        if (Size.X > _maxWidth) Size = new Vector2(_maxWidth, Size.Y);
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        var center = PwTheme.Tex("windows__outerframe_centertile");
        var tl = PwTheme.Tex("windows__outerframe_corner_topleft");
        var tr = PwTheme.Tex("windows__outerframe_corner_topright");
        var bl = PwTheme.Tex("windows__outerframe_corner_bottomleft");
        var br = PwTheme.Tex("windows__outerframe_corner_bottomright");
        var top = PwTheme.Tex("windows__outerframe_toptile");
        var bottom = PwTheme.Tex("windows__outerframe_bottomtile");
        var left = PwTheme.Tex("windows__outerframe_lefttile");
        var right = PwTheme.Tex("windows__outerframe_righttile");
        if (center == null || tl == null || tr == null || bl == null || br == null || top == null || bottom == null || left == null || right == null)
        {
            DrawRect(r, new Color(0.13f, 0.09f, 0.05f, 0.95f));
            DrawRect(r, PwTheme.Dim, false, 2);
            DrawRect(new Rect2(0, 0, Size.X, 25), new Color(0.6f, 0.45f, 0.25f));
            DrawCaption();
            return;
        }
        // centre (inset 25 25 25 27), tiled
        DrawTextureRect(center, new Rect2(27, 25, Size.X - 52, Size.Y - 50), true);
        // edges: left / right 27 / 25 px wide tiled down between the corners (not under their rounded, transparent
        // parts), bottom 25 px tiled across, the title strip at its own width
        DrawTextureRect(left, new Rect2(0, 0, 27, Size.Y - bl.GetHeight()), true);
        DrawTextureRect(right, new Rect2(Size.X - 25, 0, 25, Size.Y - br.GetHeight()), true);
        DrawTextureRect(bottom, new Rect2(0, Size.Y - 25, Size.X, 25), true);
        float tw = Mathf.Min(top.GetWidth() * (25f / top.GetHeight()), Size.X - 27 - tr.GetWidth());
        float k = top.GetHeight() / 25f;
        float extra = Mathf.Clamp(CaptionWidth() - TabRoom, 0, Mathf.Max(0, tw - TabEnd));
        if (extra <= 0)
            DrawTextureRectRegion(top, new Rect2(27, 0, tw, 25), new Rect2(0, 0, tw * k, top.GetHeight()));
        else
        {
            // a caption longer than the title tab (the strip's first ~190 px): the tab's middle drawn wider
            DrawTextureRectRegion(top, new Rect2(27, 0, TabStretchFrom, 25), new Rect2(0, 0, TabStretchFrom * k, top.GetHeight()));
            DrawTextureRectRegion(top, new Rect2(27 + TabStretchFrom, 0, TabStretchLen + extra, 25), new Rect2(TabStretchFrom * k, 0, TabStretchLen * k, top.GetHeight()));
            float rest = tw - TabStretchFrom - TabStretchLen - extra;
            if (rest > 0)
                DrawTextureRectRegion(top, new Rect2(27 + TabStretchFrom + TabStretchLen + extra, 0, rest, 25), new Rect2((TabStretchFrom + TabStretchLen) * k, 0, rest * k, top.GetHeight()));
        }
        DrawTexture(tl, Vector2.Zero);
        DrawTexture(tr, new Vector2(Size.X - tr.GetWidth(), 0));
        DrawTexture(bl, new Vector2(0, Size.Y - bl.GetHeight()));
        DrawTexture(br, new Vector2(Size.X - br.GetWidth(), Size.Y - br.GetHeight()));
        DrawCaption();
    }

    // the title tab of outerframe_toptile: the caption fits in TabRoom px from x 44; its plain middle (from
    // TabStretchFrom, TabStretchLen px) is drawn wider for a longer caption, up to TabEnd
    const float TabRoom = 146, TabStretchFrom = 100, TabStretchLen = 30, TabEnd = 200;
    const int CaptionSize = 15;

    float CaptionWidth() => string.IsNullOrEmpty(_caption) ? 0 : PwTheme.BodyFont.GetStringSize(_caption, HorizontalAlignment.Left, -1, CaptionSize).X;

    void DrawCaption()
    {
        if (string.IsNullOrEmpty(_caption)) return;
        DrawString(PwTheme.BodyFont, new Vector2(44, 18), _caption, HorizontalAlignment.Left, -1, CaptionSize, PwTheme.Title);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }
}
