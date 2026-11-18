// The effects layer of the game (game/src/View/Fx, docs/EFFECTS.md): the models' particle emitters (the view's
// Particles), the game's effect models and the drawn effects (WorldFx), fed by the world's events (HandleEvents in
// GameSession.Feedback.cs) and by what the simulation keeps (damaged buildings, burning resin, spirits, missions'
// effects, question marks). The Options' "Particles" switches them (off / low / on / high).
// Ported from remake/src/main.js (G.fx, G.mfx, G.particles set up in startGame; the 'effects' draw step).
using System;
using System.Linq;
using PwRemake.View.Fx;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>the effects layer (null before the view is built)</summary>
    public WorldFx? Effects { get; private set; }

    void BuildEffects()
    {
        var fx = new WorldFx(() => World, Models, View.Particles, Entities.ModelOf, Entities.PosOf, View.Ground.At)
        {
            Visible = (x, z) => Fog.Visible(x, z),
            Explored = (x, z) => Fog.Explored(x, z),
            MissionEffects = Mission?.Host.Effects,                 // (a skirmish: none)
        };
        fx.Level = GameApp.Settings.Particles;
        // a place the player has not explored sprays nothing (fog of war; main.js particles.seen)
        View.Particles.Seen = (x, z) => Fog.Explored(x, z);
        // the weapons' attack clips (not all named "attack": "seas_flamethrower_0"): their fire sprays only while one plays
        PwRemake.View.Fx.Particles.WeaponClips = World.Data.WeaponClips();
        fx.Gfx.Shake = (dur, s, fade, pos, range) => Camera.AddShake(dur, s, fade, pos, range);
        fx.Gfx.Sound = (n, p) => { try { Fb?.Effect(n, new PwRemake.Sim.Core.V3(p.X, p.Y, p.Z)); } catch (Exception) { } };
        fx.QuestionMarks = () => Presenter.QuestionMarks.Values.Where(q => q.Model.Length > 0)
            .Where(_ => Mission != null).Select(q => { var (x, z) = Mission!.Campaign.ToGame(q.X, q.Y); return (q.Guid, q.Model, x, z); });
        Entities.LeafFall = fx.LeafFall;
        // the spirits show while one of the player's own aje_shaman is selected (MainController.UpdateSpirits)
        fx.ShowSpirits = () => Selection != null && Selection.OwnUnits.Exists(u => string.Equals(u.Name, "aje_shaman", StringComparison.OrdinalIgnoreCase));
        Effects = fx;
        AddChild(fx);
    }

    /// <summary>the "Particles" setting changed (the in-game Options)</summary>
    public void SetParticles(string level)
    {
        if (Effects != null) Effects.Level = level;
        if (View != null) View.Particles.Level = Effects?.Level ?? level;
    }

    // one frame of the effects (main.js 'effects' step): their clock is the game's, the frame's while a cutscene stands
    // the game still
    void StepEffects(double dt, double gdt)
    {
        if (Effects == null) return;
        double fxDt = Cinematic && gdt == 0 ? dt : gdt;
        try { Effects.Step(dt, gdt, fxDt); } catch (Exception e) { ErrOnce("fx", "the effects", e); }
    }
}
