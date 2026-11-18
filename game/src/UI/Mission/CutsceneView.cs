// The cutscene on screen (MissionUiState.Cine): the letterbox (black bars with the soft edges of the menu art,
// menue__decoration__cinema_topsoft / _bottomsoft), sliding in; the sequence's title in the upper bar; the subtitle
// line with the speaker (and the portrait in a line scene) in the lower bar; the fade to and from black of the
// timeline; the hint "Click or Space: continue · Esc: skip". It takes every click (the player has no input,
// Game.usl:1812-1858).
//
// Full screen (the option "Cutscene black bars" off, the default - MissionUiState.CinemaBars): no letterbox; the title,
// the subtitle line and the hint over the picture, the line at the bottom on a dark backing with an outline. The
// camera is not changed: the original's bars are a filter drawn over its full picture (ItemWideScreen,
// docs/SEQUENCE_FORMAT.md §5) with the camera's vertical field of view over the whole screen, so without them the shot
// keeps its width and framing and shows what the bars covered above and below.
//
// Films (the original's .bik): Godot plays Ogg Theora only. A film converted to assets/video/<name>.ogv plays full
// screen with the sequence's lines as subtitles; the toolkit's WebM films (VP9, for the browser) cannot - such a
// sequence shows its lines without pictures, or (no lines) a card with the film's name for a moment.
using System;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.Mission;

public partial class CutsceneView : Control
{
    readonly MissionUiState S;
    readonly AssetSource? _assets;
    readonly ColorRect _top, _bot, _fade, _filmBg, _subBg;
    readonly TextureRect _topSoft, _botSoft, _por;
    readonly Label _title, _hint, _card, _cardNote;
    readonly RichTextLabel _line;
    readonly HBoxContainer _sub;
    readonly VideoStreamPlayer _video;
    readonly FilmFit _fit = new();
    double _slide;          // 0 = bars out, 1 = in (0.5 s, .cine .cbar transition)
    CinePlay? _shown;
    int _film = -1;

    public CutsceneView(MissionUiState s, AssetSource? assets)
    {
        S = s;
        _assets = assets;
        Name = "Cutscene";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        _filmBg = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _filmBg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_filmBg);
        _video = new VideoStreamPlayer { Expand = true, Visible = false, MouseFilter = MouseFilterEnum.Ignore, Bus = "Master" };
        _video.Finished += () => S.FilmEnded();
        AddChild(_video);
        _fade = new ColorRect { Color = new Color(0, 0, 0, 0), MouseFilter = MouseFilterEnum.Ignore };
        _fade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_fade);
        _top = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        _bot = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_top); AddChild(_bot);
        _topSoft = new TextureRect { Texture = HudArt.Tex("menu/menue__decoration__cinema_topsoft.png"), StretchMode = TextureRect.StretchModeEnum.Tile, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore };
        _botSoft = new TextureRect { Texture = HudArt.Tex("menu/menue__decoration__cinema_bottomsoft.png"), StretchMode = TextureRect.StretchModeEnum.Tile, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_topSoft); AddChild(_botSoft);
        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center, LabelSettings = new LabelSettings { Font = HudArt.Sans, FontSize = 13, FontColor = new Color(1, 250 / 255f, 180 / 255f, 0.55f) } };
        _top.AddChild(_title);
        _subBg = new ColorRect { Color = new Color(0, 0, 0, 0.42f), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _bot.AddChild(_subBg);
        _sub = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        _sub.AddThemeConstantOverride("separation", 14);
        _bot.AddChild(_sub);
        _por = new TextureRect { CustomMinimumSize = new Vector2(51, 49), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, SizeFlagsVertical = SizeFlags.ShrinkBegin, MouseFilter = MouseFilterEnum.Ignore };
        _sub.AddChild(_por);
        _line = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore };
        _line.AddThemeFontOverride("normal_font", HudArt.Sans);
        _line.AddThemeFontOverride("bold_font", HudArt.SansBold);
        _line.AddThemeColorOverride("default_color", Col.Speech);
        _line.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
        _line.AddThemeConstantOverride("shadow_offset_x", 0);
        _line.AddThemeConstantOverride("shadow_offset_y", 1);
        _line.AddThemeConstantOverride("line_separation", 4);
        _sub.AddChild(_line);
        _hint = new Label { HorizontalAlignment = HorizontalAlignment.Right, LabelSettings = new LabelSettings { Font = HudArt.Sans, FontSize = 11, FontColor = new Color(1, 250 / 255f, 180 / 255f, 0.42f) } };
        _bot.AddChild(_hint);
        _card = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visible = false, LabelSettings = new LabelSettings { Font = HudArt.SansBold, FontSize = 30, FontColor = new Color(1, 1, 200 / 255f), ShadowColor = Colors.Black, ShadowSize = 6 } };
        _cardNote = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false, LabelSettings = new LabelSettings { Font = HudArt.Sans, FontSize = 14, FontColor = new Color(0.75f, 0.7f, 0.6f) } };
        AddChild(_card); AddChild(_cardNote);
        GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) S.Next();
            AcceptEvent();
        };
    }

    public void Sync(double dt)
    {
        var c = S.Cine;
        if (c == null)
        {
            if (Visible) { Visible = false; _video.Stop(); _video.Stream = null; _shown = null; _film = -1; _slide = 0; }
            return;
        }
        if (c != _shown) { _shown = c; _film = -1; _slide = 0; }
        Visible = true;
        var vp = GetViewportRect().Size;
        bool film = c.Kind == CineKind.Film, card = c.Kind == CineKind.FilmCard;
        // (full screen: the bars' places stay, see-through - the title, line and hint on the picture)
        bool full = !S.CinemaBars && !film && !card;
        _slide = (c.Bars && !full) || card ? Math.Min(1, _slide + dt / 0.5) : Math.Max(0, _slide - dt / 0.5);
        double e = full ? 1 : 1 - Math.Pow(1 - _slide, 3);            // ease-out
        _top.Color = _bot.Color = full ? new Color(0, 0, 0, 0) : Colors.Black;
        _topSoft.Visible = _botSoft.Visible = !film && !full;
        _title.LabelSettings.OutlineSize = full ? 4 : 0;
        _title.LabelSettings.OutlineColor = new Color(0, 0, 0, 0.8f);
        _line.AddThemeConstantOverride("outline_size", full ? 5 : 0);
        _line.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        _hint.LabelSettings.OutlineSize = full ? 3 : 0;
        _hint.LabelSettings.OutlineColor = new Color(0, 0, 0, 0.7f);
        float th = Math.Max(46, vp.Y * 0.12f), bh = Math.Max(140, vp.Y * 0.21f);
        _top.Visible = _bot.Visible = !film;
        _top.Position = new Vector2(0, (float)(-th * (1 - e))); _top.Size = new Vector2(vp.X, th);
        _bot.Position = new Vector2(0, (float)(vp.Y - bh * e)); _bot.Size = new Vector2(vp.X, bh);
        _topSoft.Position = new Vector2(0, _top.Position.Y + th); _topSoft.Size = new Vector2(vp.X, 6);
        _botSoft.Position = new Vector2(0, _bot.Position.Y - 6); _botSoft.Size = new Vector2(vp.X, 6);
        _title.Text = c.Title.ToUpperInvariant();
        _title.Position = new Vector2(0, th - 9 - 16); _title.Size = new Vector2(vp.X, 16);
        _fade.Color = new Color(0, 0, 0, (float)c.Fade);
        // the subtitle line
        int fs = (int)Math.Clamp(vp.Y * 0.016 + 8, 15, 21);
        _line.AddThemeFontSizeOverride("normal_font_size", fs);
        _line.AddThemeFontSizeOverride("bold_font_size", fs);
        float sw = Math.Min(920, vp.X * 0.92f);
        var por = c.Portrait.Length > 0 ? HudArt.Atlas(c.Portrait, 3) : null;
        _por.Texture = por;
        _por.Visible = por != null;
        // (.csub centres the line; a short one is as wide as its text)
        float lw = sw - (por != null ? 65 : 0);
        float tw = HudArt.SansBold.GetStringSize(c.Speaker + "  ", HorizontalAlignment.Left, -1, fs).X + HudArt.Sans.GetStringSize(c.Text, HorizontalAlignment.Left, -1, fs).X + 8;
        _line.CustomMinimumSize = new Vector2(Math.Min(lw, tw), 0);
        _line.Size = new Vector2(Math.Min(lw, tw), _line.Size.Y);
        var txt = (c.Speaker.Length > 0 ? $"[b][color={Col.Hex(Col.Gold)}]{NewsTickerView.Esc(c.Speaker)}[/color][/b]  " : "") + NewsTickerView.Esc(c.Text);
        if (_line.Text != txt) _line.Text = txt;
        var ss = _sub.GetCombinedMinimumSize();
        _sub.Size = ss;
        // (full screen: at the bottom, above the hint, on its backing)
        _sub.Position = new Vector2((vp.X - ss.X) / 2, full ? bh - 30 - ss.Y : 14);
        _subBg.Visible = full && c.Text.Length > 0;
        if (_subBg.Visible) { _subBg.Position = _sub.Position - new Vector2(16, 6); _subBg.Size = ss + new Vector2(32, 12); }
        _hint.Text = film || card ? "Esc: skip" : c.CanNext ? "Click or Space: continue  ·  Esc: skip" : "Esc: skip";
        _hint.Position = new Vector2(0, bh - 22); _hint.Size = new Vector2(vp.X - 14, 16);
        // films
        _filmBg.Visible = film || card;
        _video.Visible = film;
        if (film)
        {
            if (c.FilmIndex != _film)
            {
                _film = c.FilmIndex;
                var path = c.FilmIndex >= 0 && c.FilmIndex < c.Films.Count ? (S.Out as GodotMissionOutput)?.FilmPath(c.Films[c.FilmIndex]) : null;
                var stream = path != null ? new VideoStreamTheora { File = path } : null;
                _video.Stream = stream;
                _fit.Reset();
                if (stream != null) _video.Play(); else S.FilmEnded();
            }
            // as large as the screen allows, shape kept; the bars painted into the film go off the screen (FilmFit)
            _fit.Apply(_video, Vector2.Zero, vp, GetProcessDeltaTime());
            double len = _video.GetStreamLength();
            S.FilmSubtitle(_video.StreamPosition, len);
            // (the subtitle over the film: the lower bar is not shown - the line sits at the bottom)
            if (_sub.GetParent() != this) _sub.Reparent(this, false);
            _sub.Position = new Vector2((vp.X - ss.X) / 2, vp.Y * 0.94f - ss.Y);
        }
        else if (_sub.GetParent() != _bot) _sub.Reparent(_bot, false);
        _card.Visible = _cardNote.Visible = card;
        if (card)
        {
            _card.Text = S.Data.Title.Length > 0 ? S.Data.Title : c.Seq?.Id ?? "";
            _card.Position = new Vector2(0, vp.Y * 0.38f); _card.Size = new Vector2(vp.X, 40);
            _cardNote.Text = $"(film {c.MissingFilm} - not playable here: Godot plays Ogg Theora films, assets/video/<name>.ogv)";
            _cardNote.Position = new Vector2(vp.X * 0.2f, vp.Y * 0.38f + 56); _cardNote.Size = new Vector2(vp.X * 0.6f, 40);
            // (the card fades with its time)
            float a = (float)Math.Clamp(Math.Min(c.T / 0.4, (c.Len - c.T) / 0.6), 0, 1);
            _card.Modulate = _cardNote.Modulate = new Color(1, 1, 1, a);
        }
    }
}
