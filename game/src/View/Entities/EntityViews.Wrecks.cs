// The wrecks of ships (World.ShipWreck, World.PirateWreck): a sunk ship is drawn as its wreck "<gfx>_dest" at
// destruction level 2, playing its clip "destroy" once from the death, instead of the ship (main.js: u.obj hidden, the
// wreck shown where the player sees the owner or the place was explored); MIRAGE's pirate boss ship also leaves its
// parts' wrecks (CPirateShipCorpse: the tail's, row's and sail's, the cannon deck's and its reduced one), each shown
// and played on the times the simulation gives (WreckPart), its hull's wreck only once those are gone (Wreck.From).
// Ported from remake/src/game/systems/combat.js shipWreck (the model) and main.js (its visibility) (pw-exporter 98c7703);
// the pirate ship's parts from MIRAGE Mirage.usl CPirateShipCorpse.
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.World;
using PwRemake.View.Models;

namespace PwRemake.View.Entities;

public partial class EntityViews
{
    sealed class WreckView
    {
        public ModelInstance? Hull;
        public readonly List<(WreckPart P, ModelInstance? M)> Parts = new();
        public int Seen;
    }
    readonly Dictionary<Unit, WreckView> _wrecks = new();

    ModelInstance? WreckModel(string? gfx, Unit u, string what)
    {
        if (string.IsNullOrEmpty(gfx) || !HasFile(Lib, gfx)) return null;
        var tpl = Lib.Template(gfx.ToLowerInvariant(), OptionsOf(gfx), true);
        if (tpl == null) return null;
        var m = Lib.Instantiate(tpl);
        m.Name = $"wreck_{u.Id}_{what}";
        m.Visible = false;
        AddChild(m);
        m.SetPartyColor(u.Owner?.PartyColor is int pc ? Hex(pc) : null);
        return m;
    }

    /// <summary>per frame: every sunk ship's wreck and its parts' wrecks</summary>
    void SyncWrecks(double gdt)
    {
        foreach (var u in W.Units)
        {
            if (u.Wreck == null || u.Removed) continue;
            if (!_wrecks.TryGetValue(u, out var wv))
            {
                wv = new WreckView { Hull = WreckModel(u.Wreck.Gfx, u, "hull") };
                if (wv.Hull != null) { wv.Hull.Position = new Vector3((float)u.Pos.X, (float)u.Pos.Y, (float)u.Pos.Z); wv.Hull.Rotation = new Vector3(0, (float)u.Heading, 0); wv.Hull.SetBuildingState(4, 2, 1); }
                foreach (var p in u.Wreck.Parts ?? new List<WreckPart>())
                {
                    var pm = WreckModel(p.Gfx, u, p.Gfx);
                    if (pm != null) { pm.Position = new Vector3((float)p.Pos.X, (float)p.Pos.Y, (float)p.Pos.Z); pm.Rotation = new Vector3(0, (float)p.Heading, 0); pm.SetBuildingState(4, p.VisDmg, 1); }
                    wv.Parts.Add((p, pm));
                }
                _wrecks[u] = wv;
            }
            wv.Seen = _frame;
            // (seen where the player sees the owner or the place was ever explored: main.js)
            bool shown = Hide?.Invoke(u) != true && (_sees(u.Owner) || Fog.Explored(u.Pos.X, u.Pos.Z));
            double died = W.Time - u.DeadT;
            if (wv.Hull != null)
            {
                bool on = shown && u.Wreck.ShownAt(u.DeadT);
                wv.Hull.Visible = on;
                if (on)
                {
                    var clip = u.Wreck.Anim?.CurName ?? (wv.Hull.Template.ClipNames.ContainsKey("destroy") ? "destroy" : null);
                    wv.Hull.SyncAnimation(clip, false, 1, died + u.Wreck.From, W.Time);
                    wv.Hull.Advance(gdt);
                }
            }
            foreach (var (p, pm) in wv.Parts)
            {
                if (pm == null) continue;
                bool on = shown && p.ShownAt(u.DeadT);
                pm.Visible = on;
                if (!on || double.IsNaN(p.PlayAt) || u.DeadT < p.PlayAt) continue;
                pm.SyncAnimation("destroy", false, 1, died + p.PlayAt, W.Time);
                pm.Advance(gdt);
            }
        }
        foreach (var (u, wv) in _wrecks.Where(kv => kv.Value.Seen != _frame).ToList())
        {
            foreach (var m in wv.Parts.Select(x => x.M).Append(wv.Hull))
                if (m != null && GodotObject.IsInstanceValid(m)) { m.GetParent()?.RemoveChild(m); m.QueueFree(); }
            _wrecks.Remove(u);
        }
    }
}
