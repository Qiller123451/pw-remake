// The settings controls of the editor's tools (the properties panel, MapEditor.Ui.cs) in the game's look: captions,
// sliders with their value, check boxes, choices, number fields, swatches. Built in code into the panel's box.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.UI;

namespace PwRemake.Editor;

public sealed class ToolUi
{
    public readonly VBoxContainer Box;
    public ToolUi(VBoxContainer box) { Box = box; }

    public Label Caption(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", PwTheme.Hi);
        l.AddThemeFontSizeOverride("font_size", 15);
        Box.AddChild(l);
        return l;
    }

    public Label Note(string text, int size = 12)
    {
        var l = PwTheme.Para(text, size);
        l.CustomMinimumSize = new Vector2(260, 0);
        Box.AddChild(l);
        return l;
    }

    /// <summary>a label "name: value" over a slider</summary>
    public HSlider Slider(string name, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt)
    {
        var l = new Label { Text = $"{name}: {fmt(get())}" };
        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = get(), CustomMinimumSize = new Vector2(240, 22), FocusMode = Control.FocusModeEnum.None };
        s.ValueChanged += v => { set(v); l.Text = $"{name}: {fmt(get())}"; };
        Box.AddChild(l); Box.AddChild(s);
        return s;
    }

    public CheckBox Check(string text, bool value, Action<bool> set, string tip = "")
    {
        var c = new CheckBox { Text = text, ButtonPressed = value, FocusMode = Control.FocusModeEnum.None, TooltipText = tip };
        c.Toggled += on => set(on);
        Box.AddChild(c);
        return c;
    }

    /// <summary>one of several (radio buttons in rows of `perRow`)</summary>
    public void Choice(IReadOnlyList<string> names, int current, Action<int> set, int perRow = 3, IReadOnlyList<string>? tips = null)
    {
        var grp = new ButtonGroup();
        HBoxContainer? row = null;
        for (int k = 0; k < names.Count; k++)
        {
            if (k % perRow == 0) { row = new HBoxContainer(); Box.AddChild(row); }
            var c = new CheckBox { Text = names[k], ButtonGroup = grp, ButtonPressed = k == current, FocusMode = Control.FocusModeEnum.None, TooltipText = tips != null && k < tips.Count ? tips[k] : "" };
            int kk = k;
            c.Toggled += on => { if (on) set(kk); };
            row!.AddChild(c);
        }
    }

    /// <summary>a number field with a label</summary>
    public SpinBox Number(string name, double min, double max, double step, double value, Action<double> set, string suffix = "")
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = name, CustomMinimumSize = new Vector2(110, 0) });
        var sb = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AllowGreater = false };
        sb.ValueChanged += v => set(v);
        row.AddChild(sb);
        Box.AddChild(row);
        return sb;
    }

    /// <summary>a grid of picture buttons (one chosen): ground material swatches</summary>
    public void Swatches(IReadOnlyList<(Texture2D? Tex, string Name, string Tip)> items, int current, Action<int> set, int perRow = 4, int size = 52)
    {
        var grid = new GridContainer { Columns = perRow };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        var grp = new ButtonGroup();
        for (int k = 0; k < items.Count; k++)
        {
            var (tex, name, tip) = items[k];
            var b = new Button
            {
                ToggleMode = true, ButtonGroup = grp, ButtonPressed = k == current, FocusMode = Control.FocusModeEnum.None,
                CustomMinimumSize = new Vector2(size, size), TooltipText = tip, Icon = tex, ExpandIcon = true, Text = tex == null ? name : "",
                IconAlignment = HorizontalAlignment.Center,
            };
            b.AddThemeConstantOverride("icon_max_width", size - 6);
            int kk = k;
            b.Toggled += on => { if (on) set(kk); };
            grid.AddChild(b);
        }
        Box.AddChild(grid);
    }

    /// <summary>a drop-down list</summary>
    public OptionButton Options(string name, IReadOnlyList<string> items, int current, Action<int> set)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = name, CustomMinimumSize = new Vector2(90, 0) });
        var o = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var it in items) o.AddItem(it);
        if (items.Count > 0) o.Select(Math.Clamp(current, 0, items.Count - 1));
        o.ItemSelected += i => set((int)i);
        row.AddChild(o);
        Box.AddChild(row);
        return o;
    }

    public Button Button(string text, Action go, string tip = "", float w = 120)
    {
        var b = PwTheme.Btn(text, go, w);
        b.TooltipText = tip;
        Box.AddChild(b);
        return b;
    }

    public void Separator() => Box.AddChild(new HSeparator());
}
