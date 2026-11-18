// A full-screen menu: a background picture of the original's menus (cover-scaled) or the darkened game, and one
// PwWindow in the middle. Title, campaign and the other menus derive from it and fill Window.Body.
using Godot;

namespace PwRemake.UI;

public abstract partial class MenuScreen : Control
{
    protected PwWindow Window { get; }
    readonly string? _background;

    protected MenuScreen(string caption, string? background = "decoration__loadbg_mp_jungle.jpg", float minWidth = 420)
    {
        _background = background;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = PwTheme.Theme;
        MouseFilter = MouseFilterEnum.Stop;
        Window = new PwWindow(caption, minWidth);
    }

    public override void _Ready()
    {
        if (_background != null)
        {
            var tex = PwTheme.Tex(_background);
            if (tex != null)
                AddChild(new TextureRect
                {
                    Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, AnchorRight = 1, AnchorBottom = 1,
                    MouseFilter = MouseFilterEnum.Ignore,
                });
            else AddChild(new ColorRect { Color = new Color(0.05f, 0.04f, 0.03f), AnchorRight = 1, AnchorBottom = 1 });
        }
        else
        {
            var dark = PwTheme.Tex("windows__darken_overlay");
            if (dark != null) AddChild(new TextureRect { Texture = dark, StretchMode = TextureRect.StretchModeEnum.Tile, AnchorRight = 1, AnchorBottom = 1 });
            else AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.55f), AnchorRight = 1, AnchorBottom = 1 });
        }
        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1 };
        AddChild(center);
        center.AddChild(Window);
        Build();
    }

    /// <summary>Fill Window.Body.</summary>
    protected abstract void Build();

    protected static HBoxContainer Row(params Control[] items)
    {
        var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        h.AddThemeConstantOverride("separation", 10);
        foreach (var c in items) h.AddChild(c);
        return h;
    }

    /// <summary>A labelled group (the remake's .pwgrp: a bold caption over its content).</summary>
    protected static VBoxContainer Group(string caption)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        var l = new Label { Text = caption };
        l.AddThemeColorOverride("font_color", PwTheme.Hi);
        l.AddThemeFontSizeOverride("font_size", 15);
        v.AddChild(l);
        return v;
    }
}
