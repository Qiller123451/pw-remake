// A building being built, drawn as the original engine draws it (PWClient 0x46e9b0, 0x7aa060, 0x7a9d30, 0x46fe90 -
// the rules: Parts.cs BuildUp): the parts of the next construction level rise out of the ground while the current
// level is built, the last level's scaffolding sinks into it in the half second after the level changed. Without this
// a site jumped from one stage to the next every 25 % (Kacper 2026-11-21: "in original there was gradual build step and
// here we just suddenly change to the next build stage").
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.View.Parts;

namespace PwRemake.View.Models;

public partial class ModelInstance
{
    /// <summary>the parts that take part: the node, its part, its own position, the model frame -> its parent's frame</summary>
    (Node3D Node, PartRef Part, Vector3 Base, Basis ToLocal)[]? _buParts;
    sbyte[] _bu0 = Array.Empty<sbyte>(), _bu1 = Array.Empty<sbyte>();
    readonly Dictionary<uint, float> _buUp = new(), _buDown = new();
    int _buLevel = -1;
    double _buChange, _buSeen = double.NegativeInfinity;
    bool _buActive;

    /// <summary>
    /// Every drawn frame of a building (EntityViews): progress 0..1 (1 = finished), now: the game time (s). Moves and shows the
    /// parts while the building is built and for the half second after each level; does nothing for a finished one.
    /// </summary>
    public void StepBuildUp(double progress, double now)
    {
        if (!Template.HasBuildUp || BuildUp.Of(Template.Fourcc) is not { } m) return;
        var s = _state;
        int L = Math.Clamp(s.Level, 0, 4);
        // a building first seen, or seen again after a while, shows its level as it is (nothing sinks for a level
        // reached out of sight)
        bool away = now - _buSeen > 1.0;
        _buSeen = now;
        if (_buLevel < 0 || (away && L != _buLevel)) _buChange = now - BuildUp.SINK_TIME;
        else if (L != _buLevel) _buChange = now;
        _buLevel = L;
        double g = Math.Clamp((now - _buChange) / BuildUp.SINK_TIME, 0, 1);
        if (L >= 4 && g >= 1) { if (_buActive) EndBuildUp(); return; }
        _buActive = true;
        double f = BuildUp.StageFraction(progress, L);

        var parts = _buParts ??= BuildUpParts();
        if (_bu0.Length != parts.Length) { _bu0 = new sbyte[parts.Length]; _bu1 = new sbyte[parts.Length]; }
        bool hasPrev = L > 0, hasNext = L < 4;
        var sPrev = s; sPrev.Level = L - 1;
        var sCur = s; sCur.Level = L;
        var sNext = s; sNext.Level = L + 1;
        _buUp.Clear(); _buDown.Clear();
        // the two transitions of the engine: last level -> this one (sinking; the rest of the last level goes), this
        // level -> the next (rising), and each group's highest top
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Part;
            uint a = p.Attr, grp = a & m.Group;
            bool cur = PartStates.Visible(p, sCur) == true;
            bool prev = hasPrev && PartStates.Visible(p, sPrev) == true;
            bool next = hasNext && PartStates.Visible(p, sNext) == true;
            _bu0[i] = (sbyte)(hasPrev ? BuildUp.Transition(prev, cur, false, (a & m.Disappear) != 0, true) : 0);
            _bu1[i] = (sbyte)(hasNext ? BuildUp.Transition(cur, next, (a & m.Appear) != 0, false, false) : 0);
            if (!float.IsNaN(p.Top))
            {
                if (_bu0[i] == -1) _buDown[grp] = Math.Max(_buDown.GetValueOrDefault(grp, float.MinValue), p.Top);
                if (_bu1[i] == 1) _buUp[grp] = Math.Max(_buUp.GetValueOrDefault(grp, float.MinValue), p.Top);
            }
        }
        // each part: the first transition that moves it decides; else not drawn when one of them hides it
        for (int i = 0; i < parts.Length; i++)
        {
            var (node, p, basePos, toLocal) = parts[i];
            uint grp = p.Attr & m.Group;
            bool hide = false, decided = false;
            float off = 0;
            for (int k = 0; k < 2 && !decided; k++)
            {
                int c = k == 0 ? _bu0[i] : _bu1[i];
                if (c == 0) continue;
                if (c == 2) { hide = true; continue; }
                decided = true; hide = false;
                if (c == 1)
                {
                    if (f <= 0) hide = true;
                    else off = -(float)((1 - f) * _buUp.GetValueOrDefault(grp, 0));
                }
                else
                {
                    if (g >= 1) hide = true;
                    else off = -(float)(g * _buDown.GetValueOrDefault(grp, 0));
                }
            }
            if (node.Visible == hide) node.Visible = !hide;
            var pos = off == 0 ? basePos : basePos + toLocal * new Vector3(0, 0, off);
            if (node.Position != pos) node.Position = pos;
        }
    }

    (Node3D, PartRef, Vector3, Basis)[] BuildUpParts()
    {
        var l = new List<(Node3D, PartRef, Vector3, Basis)>();
        foreach (var (n, p) in Parts)
        {
            if (p.Kind == PartKind.Particles || !p.HasAttr || p.Sig < 0) continue;
            // (the offset is along the GSF z of the model frame - the "Model" node's own frame; a part below another
            // node gets it in its parent's frame)
            var parent = n.GetParent() as Node3D;
            var toLocal = parent == null || parent == Model ? Basis.Identity : ModelLibrary.ToRoot(parent, Model).Basis.Inverse();
            l.Add((n, p, n.Position, toLocal));
        }
        return l.ToArray();
    }

    /// <summary>the building is finished and its last parts have sunk: every part back in its place, shown as its state says</summary>
    void EndBuildUp()
    {
        _buActive = false;
        if (_buParts != null) foreach (var (n, _, b, _) in _buParts) if (n.Position != b) n.Position = b;
        PartStates.ApplyState(Parts, _state);
    }
}
