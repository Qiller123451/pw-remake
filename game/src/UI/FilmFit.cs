// How a pre-rendered film fills the screen (the start films: IntroScreen; the story films: CutsceneView).
// The original films are 4:3 pictures with the wide picture's black bars painted in (letterbox): fitted as they are,
// a wide screen shows them small, with black on all four sides. FilmFit finds the painted bars from the frames
// (rows that stay black across the whole width while the picture around them is lit) and fits the picture inside
// them to the screen, keeping its shape; the bars go off the screen. A film without bars (a logo on black, a full
// 4:3 picture) is fitted whole, as before.
//   per frame:  _fit.Apply(video, area position, area size, delta)        a new film: _fit.Reset()
using System;
using Godot;

namespace PwRemake.UI;

public sealed class FilmFit
{
    // the picture inside the bars, in the film's pixels (null: not found yet - the whole frame)
    int _top = -1, _w, _h;
    double _next;
    int _looks;

    /// <summary>A new film: its bars are found again.</summary>
    public void Reset() { _top = -1; _w = _h = 0; _next = 0.15; _looks = 0; }

    /// <summary>Size and place the player: the film's picture as large as fits in the area, centred, shape kept.</summary>
    public void Apply(VideoStreamPlayer v, Vector2 at, Vector2 area, double delta)
    {
        var tex = v.GetVideoTexture();
        if (tex == null || tex.GetWidth() <= 0 || tex.GetHeight() <= 0 || area.X <= 0 || area.Y <= 0)
        { v.Position = at; v.Size = area; return; }
        int w = tex.GetWidth(), h = tex.GetHeight();
        if (w != _w || h != _h) { _top = -1; _w = w; _h = h; _looks = 0; }
        // look at a frame now and then: often at the start (fades from black), then once a second (a later, brighter
        // frame can only show that the bars are thinner - never thicker)
        _next -= delta;
        if (_next <= 0 && v.IsPlaying())
        {
            _next = _looks < 12 ? 0.25 : 1.0;
            _looks++;
            try { Look(tex.GetImage()); } catch (Exception) { /* no readable frame */ }
        }
        int top = _top < 0 ? 0 : _top;
        float ph = h - 2 * top;
        float s = Math.Min(area.X / w, area.Y / ph);
        var size = new Vector2(w * s, h * s);
        v.Size = size;
        v.Position = at + (area - size) / 2;
    }

    /// <summary>The bars of one frame: the black rows above and below a picture that is lit across the whole width.</summary>
    void Look(Image? img)
    {
        if (img == null || img.IsEmpty()) return;
        if (img.IsCompressed()) return;
        int w = img.GetWidth(), h = img.GetHeight();
        int first = -1, last = -1;
        for (int y = 0; y < h; y++)
            if (RowLit(img, y, w)) { if (first < 0) first = y; last = y; }
        if (first < 0) return;                                   // a black frame (fades): nothing learned
        // a picture lit from edge to edge (not a logo in the middle of black)
        if (!WideLit(img, first, last, w)) return;
        int bar = Math.Min(first, h - 1 - last);                 // the bars are as high as each other
        if (bar < h * 0.03) bar = 0;                             // (a few dark rows: no bars)
        double aspect = (double)w / Math.Max(1, h - 2 * bar);
        if (bar > 0 && (aspect < 1.5 || aspect > 2.6)) return;   // not a wide-screen letterbox
        // the bars only get thinner (a darker frame shows less of the picture)
        if (_top < 0 || bar < _top) _top = bar;
    }

    static bool RowLit(Image img, int y, int w)
    {
        int step = Math.Max(1, w / 64);
        for (int x = step / 2; x < w; x += step)
        {
            var c = img.GetPixel(x, y);
            if (Math.Max(c.R, Math.Max(c.G, c.B)) > 0.10f) return true;
        }
        return false;
    }

    // lit near both side edges somewhere in the picture rows
    static bool WideLit(Image img, int y0, int y1, int w)
    {
        int xl = Math.Max(0, w / 20), xr = Math.Min(w - 1, w - 1 - w / 20);
        bool l = false, r = false;
        int step = Math.Max(1, (y1 - y0) / 48);
        for (int y = y0; y <= y1 && !(l && r); y += step)
        {
            var a = img.GetPixel(xl, y); var b = img.GetPixel(xr, y);
            if (Math.Max(a.R, Math.Max(a.G, a.B)) > 0.06f) l = true;
            if (Math.Max(b.R, Math.Max(b.G, b.B)) > 0.06f) r = true;
        }
        return l && r;
    }
}
