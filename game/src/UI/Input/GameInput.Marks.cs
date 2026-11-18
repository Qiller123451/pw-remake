// The campaign's question / exclamation marks (QMRK, QuestionMark.usl) under the mouse: the original makes them
// selectable and hitable (SetSelectable / SetHitable). Hovering one shows its tooltip - IngameScreen.usl OnTick: the
// hovered object's root is a QMRK -> its attribute "QuestionMarkToolTip" (the key the trigger's QMRK action set,
// e.g. "_TTQ_G_01", a string of the mission's tables) translated; clicking selects it - Info.usl: the info panel names
// it "_UI_IngaScre_Button_Quest" with the same text; the mouse keeps the standard cursor (GameInputController.usl
// returns no action for a QMRK). Here: the tooltip window beside it on hover; a click deselects the rest, rings the
// mark and keeps its tooltip (titled) until the selection changes. docs/HUD.md "Question marks".
using System;
using Godot;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.UI.PlayerInput;

public partial class GameInput
{
    /// <summary>the question mark under the mouse (no unit / building in front of it)</summary>
    public QuestionMarkState? HoverMark { get; private set; }
    /// <summary>the question mark clicked (the original selects it); null once the selection changes</summary>
    public QuestionMarkState? PickedMark { get; private set; }
    // what the tooltip shows now
    (QuestionMarkState? M, bool Picked, string? Text) _markTip;

    /// <summary>a mark's place in game coordinates (the mission UI keeps it in map coordinates)</summary>
    public (double X, double Z)? MarkPos(QuestionMarkState q) => G.Mission?.Campaign is { } c ? c.ToGame(q.X, q.Y) : null;

    // the marks' models stand on the ground, about 4.5 m tall; a mark the player has not explored is not drawn
    const float MARK_H = 4.5f, MARK_W = 1.3f;

    /// <summary>the screen rectangle of a mark's model (null: off screen / behind the camera)</summary>
    Rect2? MarkRect(QuestionMarkState q)
    {
        if (MarkPos(q) is not { } at) return null;
        var (x, z) = at;
        var cam = G.Renderer.Camera;
        float y = (float)W.Height(x, z);
        float x0 = 1e9f, y0 = 1e9f, x1 = -1e9f, y1 = -1e9f;
        var right = cam.GlobalTransform.Basis.X.Normalized() * MARK_W;
        foreach (var p in new[] { new Vector3((float)x, y, (float)z), new Vector3((float)x, y + MARK_H, (float)z) })
            foreach (var w in new[] { p - right, p + right })
            {
                if (cam.IsPositionBehind(w)) return null;
                var s = cam.UnprojectPosition(w);
                x0 = Math.Min(x0, s.X); x1 = Math.Max(x1, s.X); y0 = Math.Min(y0, s.Y); y1 = Math.Max(y1, s.Y);
            }
        // (far out a few pixels: at least 14 x 18 px to point at)
        float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, hw = Math.Max(7, (x1 - x0) / 2), hh = Math.Max(9, (y1 - y0) / 2);
        return new Rect2(cx - hw, cy - hh, hw * 2, hh * 2);
    }

    /// <summary>the visible, explored question mark at a screen point (the nearest to the camera of several)</summary>
    public QuestionMarkState? MarkAt(Vector2 m)
    {
        if (G.Mission == null || G.Presenter == null) return null;
        QuestionMarkState? best = null; float bd = float.MaxValue;
        foreach (var q in G.Presenter.QuestionMarks.Values)
        {
            if (!q.Visible || MarkPos(q) is not { } at || !G.Fog.Explored(at.X, at.Z)) continue;
            var (x, z) = at;
            if (MarkRect(q) is not { } r || !r.HasPoint(m)) continue;
            float d = G.Renderer.Camera.GlobalPosition.DistanceTo(new Vector3((float)x, (float)W.Height(x, z), (float)z));
            if (d < bd) { bd = d; best = q; }
        }
        return best;
    }

    /// <summary>a left click on a mark: it is "selected" (the rest deselected); false: no mark there</summary>
    bool ClickMark(Vector2 m, bool shift)
    {
        var q = MarkAt(m);
        if (q == null) return false;
        if (!shift) G.Selection.Set(Array.Empty<PwRemake.Sim.World.Entity>());
        PickedMark = q;
        return true;
    }

    /// <summary>the selection changed: a picked mark is no longer selected once something else is</summary>
    void MarkSelectionChanged() { if (G.Selection.Count > 0) PickedMark = null; }

    /// <summary>per hover tick: the mark under the mouse and the tooltip (hovered mark, else the picked one)</summary>
    void UpdateMarks(bool overHud)
    {
        HoverMark = overHud || Hover != null ? null : MarkAt(_mouse);
        if (PickedMark != null && !PickedMark.Visible) PickedMark = null;
        var q = HoverMark ?? PickedMark;
        bool picked = q != null && q == PickedMark;
        var tip = q?.Tip(picked ? W.Data.Ui("_UI_IngaScre_Button_Quest", "Quest") : null);
        if (q == _markTip.M && picked == _markTip.Picked && tip?.Text == _markTip.Text) return;
        _markTip = (q, picked, tip?.Text);
        if (tip is not { } t) G.Hud?.WorldTip(null, null, null);
        // hovered: beside the mark; picked (the mouse elsewhere): the original's place above the minimap
        else G.Hud?.WorldTip(t.Title, t.Text, q == HoverMark ? MarkRect(q!) : null);
    }

    /// <summary>a mission ended / a cutscene: no mark is hovered or picked</summary>
    void ClearMarks()
    {
        if (HoverMark == null && PickedMark == null && _markTip.M == null) return;
        HoverMark = PickedMark = null;
        _markTip = default;
        G.Hud?.WorldTip(null, null, null);
    }
}
