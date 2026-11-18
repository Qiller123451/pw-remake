// The loading bar of the original's loading screen: the "loading display" of the menu art (UI/menue/decoration/
// loading_display_off and _on, 609 x 66 - converted to assets/ui/menu/menue__decoration__loading_display_*.png):
// the unlit display, and the lit one shown from the left up to the progress (the browser remake's #loading .ld /
// #loadbar). Only the game's own art is drawn - no stand-in (without it the screen shows its progress line alone and
// the log says why). The bar keeps its art's proportions inside its rectangle (anchor it; it scales with the window)
// and glides to a new value instead of jumping.
using System;
using Godot;

namespace PwRemake.UI.Loading;

public partial class LoadingBar : Control
{
    /// <summary>the original's display (both or neither)</summary>
    public Texture2D? Off, On;
    double _value, _shown;

    /// <summary>0..1</summary>
    public double Value
    {
        get => _value;
        set => _value = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
    }

    /// <summary>what is drawn now (eases towards Value)</summary>
    public double Shown => _shown;

    public LoadingBar() { MouseFilter = MouseFilterEnum.Ignore; }

    public override void _Process(double delta)
    {
        double d = _value - _shown;
        if (Math.Abs(d) < 0.0005) { if (_shown != _value) { _shown = _value; QueueRedraw(); } return; }
        // quick but not instant (about 0.15 s); never backwards by more than a jump
        _shown = d < 0 ? _value : _shown + d * Math.Min(1, delta * 7);
        QueueRedraw();
    }

    /// <summary>the drawing rectangle: the art's proportions, centred in the control</summary>
    Rect2 Fit()
    {
        var ar = Off != null && On != null ? Off.GetSize() : new Vector2(609, 66);
        var s = Size;
        float k = Math.Min(s.X / ar.X, s.Y / ar.Y);
        var sz = ar * k;
        return new Rect2((s - sz) / 2, sz);
    }

    public override void _Draw()
    {
        var r = Fit();
        float k = (float)_shown;
        if (Off != null && On != null)
        {
            DrawTextureRect(Off, r, false);
            if (k > 0)
            {
                var src = new Rect2(0, 0, On.GetWidth() * k, On.GetHeight());
                DrawTextureRectRegion(On, new Rect2(r.Position, new Vector2(r.Size.X * k, r.Size.Y)), src);
            }
            return;
        }

    }
}
