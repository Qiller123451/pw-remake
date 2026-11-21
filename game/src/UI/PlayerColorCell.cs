// A player's colour in the skirmish and multiplayer lobbies (SkirmishMenu; docs/SKIRMISH.md "Colours"): the remake's
// choice of any colour of the RGB spectrum (Kacper: "a color picker from the whole RGB spectrum ... still leave the
// predefined ones as an option").
//
//   [swatch] [Blue        v]
//   the swatch: a ColorPickerButton - its popup has the HSV square, hue bar, RGB / HSV sliders, the hex field and the
//     palette's sixteen colours below; a colour picked there is "#rrggbb" (or the palette id when it is one)
//   the list: the palette (the original's eight, the remake's eight) by name with their colours, and "Custom" while the
//     slot has a colour of its own
//
// While the popup is open every change is reported (Picking: the lobby checks the colours at once, without rebuilding
// itself); when it closes the choice is final (Chosen: saved, sent to the other machines).
//
// The campaign menus (CampaignMenu "Your colour", the co-op lobby's seats: docs/CAMPAIGN.md "Your colour") use two more
// things: a first entry for "the mission's colour" (defaultLabel; the id "" then) and the colours another player has
// (BlockedBy: the player's name or null) - greyed out in the list and in the picker's palette with the tooltip
// "used by <name>"; a colour picked in the square that is too close to one of them is not taken (the cell goes back to
// the choice before it).
using System;
using Godot;
using SimColors = PwRemake.Sim.Rules.Colors;

namespace PwRemake.UI;

public partial class PlayerColorCell : HBoxContainer
{
    /// <summary>the colour id (palette id or "#rrggbb") while the picker is open</summary>
    public event Action<string>? Picking;
    /// <summary>the colour id chosen (list, or the picker closed)</summary>
    public event Action<string>? Chosen;
    /// <summary>is the picker's popup open (the lobby waits with rebuilding its rows)</summary>
    public bool Open { get; private set; }

    readonly ColorPickerButton _btn;
    readonly OptionButton _list;
    string _id;
    /// <summary>the last colour that was allowed (a blocked pick goes back to it)</summary>
    string _lastOk;
    /// <summary>1 with the "mission's colour" entry before the palette, else 0</summary>
    readonly int _off;
    readonly string? _defaultLabel;
    readonly System.Collections.Generic.List<(Button Btn, string Id, Color Col)> _swatches = new();
    /// <summary>the swatch's colour for the default entry (the mission's colour of the player)</summary>
    Color _defaultColor = new(0.6f, 0.6f, 0.6f);
    /// <summary>the name of the player who has this colour already (null: free) - those are greyed out (UpdateBlocked)</summary>
    public Func<string, string?>? BlockedBy { get; set; }

    /// <summary>id: palette id, "#rrggbb", or "" (with defaultLabel: the default, "As in the mission")</summary>
    public PlayerColorCell(string? id, float width = 200, string? defaultLabel = null)
    {
        _defaultLabel = defaultLabel;
        _off = defaultLabel != null ? 1 : 0;
        _id = id ?? (defaultLabel != null ? "" : SimColors.PLAYER_COLORS[0].Id);
        _lastOk = _id;
        CustomMinimumSize = new Vector2(width, 0);
        AddThemeConstantOverride("separation", 4);
        _btn = new ColorPickerButton
        {
            CustomMinimumSize = new Vector2(38, 24), EditAlpha = false, EditIntensity = false, FocusMode = FocusModeEnum.None,
            Color = ColorOf(_id), TooltipText = "Any colour: the colour square, hue, RGB values or a hex code",
        };
        _btn.PickerCreated += ConfigurePicker;          // (the popup is made when first opened)
        _btn.Pressed += () => Open = true;
        _btn.ColorChanged += c => { _id = IdOf(c); ShowName(); Picking?.Invoke(_id); };
        _btn.PopupClosed += () =>
        {
            Open = false;
            if (Blocker(_id) is { } who)
            {
                // (too close to another player's colour: the choice before stays)
                _id = _lastOk; _btn.Color = ColorOf(_id); ShowName();
                _list.TooltipText = $"That colour is too close to {who}'s: {_list.TooltipText} stays";
                return;
            }
            _lastOk = _id;
            Chosen?.Invoke(_id);
        };
        AddChild(_btn);
        _list = new OptionButton { CustomMinimumSize = new Vector2(width - 42, 26), FocusMode = FocusModeEnum.None, FitToLongestItem = false };
        if (defaultLabel != null) { _list.AddIconItem(Swatch(_defaultColor), defaultLabel); _list.SetItemMetadata(0, ""); }
        foreach (var c in SimColors.PLAYER_COLORS) { _list.AddIconItem(Swatch(Rgb(c.Light)), c.Name); _list.SetItemMetadata(_list.ItemCount - 1, c.Id); }
        _list.AddIconItem(Swatch(ColorOf(_id)), "Custom");
        _list.SetItemMetadata(_list.ItemCount - 1, "custom");
        _list.ItemSelected += i =>
        {
            var v = _list.GetItemMetadata((int)i).AsString();
            if (v == "custom") { _btn.Color = ColorOf(_id); return; }
            if (Blocker(v) != null) { ShowName(); return; }
            _id = v; _lastOk = v; _btn.Color = ColorOf(v);
            ShowName();
            Chosen?.Invoke(_id);
        };
        AddChild(_list);
        ShowName();
    }

    /// <summary>the colour id shown ("" = the default entry)</summary>
    public string Id
    {
        get => _id;
        set { _id = _lastOk = value ?? ""; _btn.Color = ColorOf(_id); ShowName(); }
    }

    /// <summary>the default entry's swatch: the mission's colour of the player (null: grey, none)</summary>
    public void SetDefaultColor(Color? c)
    {
        _defaultColor = c ?? new Color(0.6f, 0.6f, 0.6f);
        if (_off == 1) _list.SetItemIcon(0, Swatch(_defaultColor));
        if (_id == "") _btn.Color = _defaultColor;
    }

    string? Blocker(string id) => id.Length == 0 ? null : BlockedBy?.Invoke(id);

    /// <summary>greys out the colours BlockedBy names (list and palette), with "used by &lt;name&gt;"</summary>
    public void UpdateBlocked()
    {
        for (int i = 0; i < SimColors.PLAYER_COLORS.Count; i++)
        {
            var c = SimColors.PLAYER_COLORS[i];
            var who = Blocker(c.Id);
            _list.SetItemDisabled(i + _off, who != null);
            _list.SetItemTooltip(i + _off, who != null ? $"used by {who}" : c.Name);
        }
        foreach (var (b, id, _) in _swatches)
        {
            var who = Blocker(id);
            b.Disabled = who != null;
            b.Text = who != null ? "x" : "";
            b.TooltipText = who != null ? $"{SimColors.ColorById(id)?.Name} - used by {who}" : SimColors.ColorById(id)?.Name ?? id;
        }
        ShowName();
    }

    /// <summary>opens the picker's popup below the swatch (tests, screenshots: --pick-color)</summary>
    public void OpenPicker()
    {
        _btn.GetPicker();
        var pop = _btn.GetPopup();
        Open = true;
        pop.Popup(new Rect2I((Vector2I)(_btn.GetScreenPosition() + new Vector2(0, _btn.Size.Y + 2)), Vector2I.Zero));
    }

    /// <summary>the cell can't be changed (another player's slot in a network lobby)</summary>
    public bool Disabled { set { _btn.Disabled = value; _list.Disabled = value; } }

    void ShowName()
    {
        int k = SimColors.IndexOf(_id);
        int custom = _list.ItemCount - 1;
        bool dflt = _off == 1 && _id.Length == 0;
        _list.SetItemIcon(custom, Swatch(ColorOf(_id)));
        _list.SetItemDisabled(custom, k >= 0 || dflt);
        _list.Select(dflt ? 0 : k >= 0 ? k + _off : custom);
        var who = Blocker(_id);
        _list.TooltipText = (dflt ? _defaultLabel! : k >= 0 ? SimColors.PLAYER_COLORS[k].Name : $"Custom colour {_id}") + (who != null ? $" - used by {who}" : "");
    }

    /// <summary>the swatch colour of an id (the default entry: its colour)</summary>
    Color ColorOf(string id) => id.Length == 0 && _off == 1 ? _defaultColor : ToColor(id);

    void ConfigurePicker()
    {
        var p = _btn.GetPicker();
        p.PickerShape = ColorPicker.PickerShapeType.HsvRectangle;
        p.ColorMode = ColorPicker.ColorModeType.Rgb;
        p.EditIntensity = false;
        p.SamplerVisible = false;
        p.ColorModesVisible = true;
        p.SlidersVisible = true;
        p.HexVisible = true;
        p.PresetsVisible = false;          // (Godot's swatches fold away: the palette below instead)
        p.CanAddSwatches = false;
        // an opaque panel in the game's colours (the theme's popup panel is see-through)
        var box = new StyleBoxFlat { BgColor = new Color(24 / 255f, 19 / 255f, 13 / 255f, 0.97f), BorderColor = PwTheme.Dim, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 8, ContentMarginBottom = 8 };
        box.SetBorderWidthAll(1);
        _btn.GetPopup().AddThemeStyleboxOverride("panel", box);
        // the palette: the predefined colours one click away (two rows: the original's eight, the remake's eight)
        p.AddChild(new Label { Text = "Predefined colours", Modulate = PwTheme.Hi });
        var grid = new GridContainer { Columns = 8 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var c in SimColors.PLAYER_COLORS)
        {
            var col = Rgb(c.Light);
            var sw = new Button { CustomMinimumSize = new Vector2(30, 22), FocusMode = FocusModeEnum.None, TooltipText = c.Name };
            var st = new StyleBoxFlat { BgColor = col, BorderColor = new Color(0, 0, 0, 0.7f) };
            st.SetBorderWidthAll(1);
            var hov = new StyleBoxFlat { BgColor = col, BorderColor = PwTheme.Hi };
            hov.SetBorderWidthAll(2);
            // (another player's colour, the campaign menus: dark, with an x - BlockedBy)
            var off = new StyleBoxFlat { BgColor = col.Darkened(0.65f), BorderColor = new Color(0, 0, 0, 0.7f) };
            off.SetBorderWidthAll(1);
            sw.AddThemeStyleboxOverride("normal", st); sw.AddThemeStyleboxOverride("hover", hov); sw.AddThemeStyleboxOverride("pressed", hov);
            sw.AddThemeStyleboxOverride("disabled", off);
            var id = c.Id;
            sw.Pressed += () => { _id = id; p.Color = col; _btn.Color = col; ShowName(); Picking?.Invoke(_id); };
            grid.AddChild(sw);
            _swatches.Add((sw, id, col));
        }
        p.AddChild(grid);
        if (BlockedBy != null) UpdateBlocked();
    }

    // ---------------------------------------------------------------- colours <-> ids
    static Color Rgb(int[] c) => Color.Color8((byte)c[0], (byte)c[1], (byte)c[2]);
    /// <summary>the light colour of an id (palette or "#rrggbb"); grey for none</summary>
    public static Color ToColor(string? id) => SimColors.ColorById(id) is { } c ? Rgb(c.Light) : new Color(0.6f, 0.6f, 0.6f);
    /// <summary>a picked colour -> the palette id when it is one of the palette's, else "#rrggbb"</summary>
    public static string IdOf(Color c)
    {
        int rgb = (int)(c.R8 << 16 | c.G8 << 8 | c.B8);
        foreach (var p in SimColors.PLAYER_COLORS) if (SimColors.Hex(p.Light) == rgb) return p.Id;
        return SimColors.CustomId(rgb);
    }
    static ImageTexture Swatch(Color c)
    {
        var img = Image.CreateEmpty(14, 14, false, Image.Format.Rgb8);
        img.Fill(new Color(0, 0, 0));
        img.FillRect(new Rect2I(1, 1, 12, 12), c);
        return ImageTexture.CreateFromImage(img);
    }
}
