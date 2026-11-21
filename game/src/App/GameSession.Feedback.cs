// Player feedback for world events (ui/feedback.js handle): what the player hears of what happened in the steps of a
// frame - sounds (through Sfx.Feedback: game/src/View/Audio/SoundFeedback.cs decides which sound) - and the view's
// part (the fog's reveals, fallen trees, the grass, the control groups losing the dead). The message lines, the
// minimap's markers and the selection are the HUD's (NewsFeed, game/src/UI/Hud).
// The visibility tests are the JS's: a world sound plays when its place is visible to the player (fog of war).
// The effects (engine/fx.js blood / dust / explosions, ui/gamefx.js effect models) are the effects layer's
// (View/Fx/WorldFx.HandleEvents, first): an impact whose effect model played has that model's sound instead.
// Ported from remake/src/ui/feedback.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.View.Audio;

namespace PwRemake.App;

public partial class GameSession
{
    readonly AttackWarnings _warn = new();

    static SoundUnit Su(Entity e, Player? me) => new(e, e.Name, e.Level, e.Alive, e is Unit, e.Owner != null && e.Owner == me);
    static SoundFeedback? Fb => GameAudio.Current?.Feedback;

    /// <summary>a selection's click and speech (ui/feedback.js selected)</summary>
    public void PlaySelected(IReadOnlyList<Entity> list) { try { Fb?.Selected(list.Select(e => Su(e, Me)).ToList()); } catch (Exception e) { ErrOnce("fb-sel", "selection sound", e); } }
    /// <summary>an order's click and speech (move, attack, amove, build, heal, food, wood, stone ...)</summary>
    public void PlayOrdered(string type, IEnumerable<Entity> units) { try { Fb?.Ordered(type, units.Select(e => Su(e, Me)).ToList()); } catch (Exception e) { ErrOnce("fb-ord", "order sound", e); } }
    public void PlayUi(string name) { try { Fb?.Ui(name); } catch (Exception) { } }
    public void PlayError() { try { Fb?.Error(); } catch (Exception) { } }

    bool Vis(V3 p) => Fog.Visible(p.X, p.Z);

    // the sounds of the world's events (feedback.js handle, the sound part; the message lines, the minimap's markers
    // and the selection losing the dead are the HUD's NewsFeed: Hud.OnEvents)
    void HandleEvents(List<WorldEvent> events)
    {
        var W = World; var me = Me; var F = Fb;
        Effects?.HandleEvents(events);
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case "itemTaken": if (e.U("unit") is { } iu && Vis(iu.Pos)) F?.ItemTaken(iu.Pos); break;
                case "storagefull": if (e.P("player") == me) F?.StorageFull(e.Str("res") ?? ""); break;
                case "placed": if (me != null && e.B("building") is { } pb && pb.Owner == me) F?.Placed(me.Tribe); break;
                case "built": if (e.B("building") is { } bb && bb.Owner == me) F?.Built(bb.Pos); break;
                case "delivered": if (e.U("unit") is { } du && du.Owner == me && Entities.OnScreen(du)) F?.Delivered(du.Name); break;
                case "trained": if (e.U("unit") is { } tu && tu.Owner == me) F?.Trained(tu.Pos); break;
                case "upgrade":
                    {
                        if (e.Data["action"] is not ActionDef a || e.P("player") != me) break;
                        F?.Upgrade(System.Text.RegularExpressions.Regex.IsMatch(a.Id, @"^age_\d$"));
                        break;
                    }
                // a computer ally's call for help (AI Chat.CryForHelp; its line and marker: the HUD's)
                case "aiHelp": if (e.P("player") is { } hp && hp != me && hp.IsFriend(me)) F?.AllyUnderAttack(); break;
                case "levelup": if (e.U("unit") is { } lu && Vis(lu.Pos)) F?.LevelUp(lu.Cls == "CHTR", lu.Pos); break;
                case "attacked":
                    {
                        var en = e.E("entity");
                        if (en?.Owner == null || me == null) break;
                        if (en.Owner != me)
                        {
                            // an ally's: the newsticker sound, once a fight (the line and the white marker: the HUD's)
                            if (me.IsFriend(en.Owner) && _warn.Hit(en.Owner, W.Time)) F?.AllyUnderAttack();
                            break;
                        }
                        // the hits of 0.15 s gathered: the music's combat switch (FeedbackEventMgr.usl:72-103)
                        F?.Attacked(en.Pos.X, en.Pos.Z, W.Time);
                        // the warning: once a fight (Player.usl IWasAttacked; the line: the HUD's)
                        if (_warn.Hit(me, W.Time)) { Pings.Add((en.Pos.X, en.Pos.Z, W.Time)); F?.UnderAttackWarning(); }
                        break;
                    }
                case "died":
                    {
                        var en = e.E("entity");
                        if (en == null) break;
                        if (Vis(en.Pos)) F?.Died(en.Name, en.Cls, en.Pos);
                        if (en.Owner == me) Input.Forget(en);
                        break;
                    }
                case "destroyed":
                    {
                        var en = e.E("entity");
                        if (en == null || e.Data["silent"] is true) break;     // removed without a fight (a gate / tower took the tile)
                        if (Fog.Explored(en.Pos.X, en.Pos.Z)) F?.Destroyed(en.Owner?.Tribe == "Aje", en.Pos);
                        if (en.Owner == me) Input.Forget(en);
                        break;
                    }
                case "impact":
                    {
                        if (e.Data["pos"] is not V3 p || !Vis(p) || Effects?.ImpactPlayed(e) == true) break;
                        var w = e.Data["weapon"] as Weapon;
                        double sp = e.Data["big"] is true ? 10 : w?.Splash ?? 0;     // (mines / torpedo turtle blasts)
                        F?.Impact(e.Data["splash"] is true, sp, w?.Fx, e.Data["target"] != null, w != null, p);
                        break;
                    }
                case "hit":
                    {
                        var t = e.E("target");
                        if (t == null || !Vis(t.Pos)) break;
                        F?.Hit((e.Data["weapon"] as Weapon)?.Fx, t.Def?.Fx, t.Pos);
                        break;
                    }
                // deleted by a mission or given away: out of the control groups (the selection: the HUD's NewsFeed)
                case "removed": if (e.E("entity") is { } re && re.Owner == me) Input.Forget(re); break;
                case "owner": if (e.E("entity") is { } oe && e.P("from") == me) Input.Forget(oe); break;
                // (revealarea: the world's fog of war takes the oracle's circles itself - World.FogStep)
                case "boarded": if (e.U("unit") is { } bu && bu.Owner == me) F?.Boarded(); break;
                case "treefall": if (e.E("node") is ResNode tn) Entities.TreeFelled(tn); break;
                case "timbergone": if (e.E("node") is ResNode gn) Entities.TimberGone(gn); break;
                case "ground": View.InvalidateGrass(e.Num("x"), e.Num("z"), e.Num("r")); break;
                // the MIRAGE moves (game/systems/moves_mirage.js): sounds, blasts
                case "sfx": if (e.Data["pos"] is V3 sp2 && Vis(sp2)) F?.Sfx(e.Str("name") ?? "", sp2); break;
                case "blast": if (e.Data["pos"] is V3 bp && Vis(bp)) F?.Blast(bp); break;
                // the clips' sound events of what is on screen (anim.js setEvents; one of the alternatives)
                case "animSound":
                    {
                        if (e.E("entity") is not { } ae || e.Data["alts"] is not List<PwRemake.Sim.Content.AnimSoundEvent> alts || alts.Count == 0) break;
                        // the clips' effect events (camera shakes: Sim AnimFx) - not alternatives of the sounds
                        if (alts.Exists(a => a.Wav == PwRemake.Sim.World.AnimFx.FX))
                        {
                            foreach (var a in alts) if (a.Wav == PwRemake.Sim.World.AnimFx.FX) Effects?.AnimEffect(a.Group, ae);
                            alts = alts.FindAll(a => a.Wav != PwRemake.Sim.World.AnimFx.FX);
                            if (alts.Count == 0) break;
                        }
                        if (!Entities.OnScreen(ae)) break;
                        var ev = alts.Count == 1 ? alts[0] : alts[_rnd.Next(alts.Count)];
                        // (the slot it takes: an idle unit's chatter gives way to walking and work, they to the fights)
                        var prio = AnimSounds.Priority(e.Str("clip"), ev.Group, ae is Unit au ? au.Task.Type : ae is Building ? "building" : null);
                        try { Sfx.Anim(new PwRemake.View.Audio.AnimSoundEvent(ev.T, ev.Wav, ev.Volume, ev.MaxHear, ev.Group, ev.MinFade, ev.Pitch), ae.Pos, prio, ae is Unit ? AudioChannel.Unit : AudioChannel.Sfx); } catch (Exception) { }
                        break;
                    }
            }
        }
        F?.FlushAttacks(W.Time);
    }
    readonly Random _rnd = new();

}
