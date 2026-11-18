// What the HUD makes of the world's events: the message lines in the middle of the screen (the newsticker lines of
// the game, "... completed", "Epoch II reached!", "Your base is under attack!" ...), the minimap's attack and
// production markers, and the selection and control groups losing what died, was removed or changed owner.
//
// The sounds, effects and the music's combat switch of the same events are the view's (game/src/View/Audio
// SoundFeedback.cs, docs/AUDIO.md; game effects: ui/gamefx.js, the world view's).
//
// No Godot types. Ported from remake/src/ui/feedback.js (pw-exporter 98c7703) - Feedback.ticker and the non-sound
// part of Feedback.handle.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud.Logic;

public sealed class NewsFeed
{
    static readonly string[] EPOCH = { "", "I", "II", "III", "IV", "V" };
    readonly IHudContext H;
    /// <summary>a message line: text, kind ("" | good | bad | warn)</summary>
    public Action<string, string> Message = (_, _) => { };
    /// <summary>a minimap marker: type (Attack, Finished, AIHelp, Ping), x, z</summary>
    public Action<string, double, double> Marker = (_, _, _) => { };
    /// <summary>an entity left the player's hands: out of the selection (and the control groups: the host's)</summary>
    public Action<Entity> Lost = _ => { };
    /// <summary>where the player was attacked last (G.pings: the "jump to the last event" key)</summary>
    public readonly List<(double X, double Z, double T)> Pings = new();

    readonly PwRemake.View.Audio.AttackWarnings _warn = new();
    List<(double X, double Z)>? _hits;
    double _hitsT;

    public NewsFeed(IHudContext host) { H = host; }

    /// <summary>a newsticker line of the game (Texts/newsticker: "_NT_Artefact_Gain" ...; %1 = the item's name)</summary>
    public string Ticker(WorldEvent e)
    {
        var D = H.World.Data;
        var key = e.Str("key") ?? "";
        var text = e.Str("text");
        var t = D.Ui(key, text ?? key);
        if (t.Contains("DUMMY")) t = text ?? "";          // (_NT_PickUpFailInvFull has no text in the game)
        // %1 %2 %3: the item's name, or the arguments (one or a list)
        var args = new List<string>();
        var item = e.Str("item");
        if (item != null) args.Add(D.Text(item).Name is { Length: > 0 } n ? n : item);
        else if (e["arg"] is System.Collections.IEnumerable list and not string) foreach (var a in list) args.Add(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        else if (e["arg"] != null) args.Add(Convert.ToString(e["arg"], System.Globalization.CultureInfo.InvariantCulture) ?? "");
        t = Regex.Replace(t, @"%(\d)", m => { int i = m.Groups[1].Value[0] - '1'; return i >= 0 && i < args.Count ? args[i] : ""; });
        // a computer player's chat line (ai/chat.js): "<its name>: <text>"
        var from = e.P("from");
        return from != null && !string.IsNullOrEmpty(from.Name) ? from.Name + ": " + t : t;
    }

    string Name(Entity en) => H.World.Data.Text(en.Name, en.Owner).Name;

    /// <summary>the events of a frame (World.DrainEvents)</summary>
    public void Handle(IEnumerable<WorldEvent> events)
    {
        var W = H.World; var me = H.Me;
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case "msg":
                    if (e.P("player") == me)
                    {
                        string text = e.Str("key") != null ? Ticker(e) : e.Str("text") ?? "";
                        // a computer player talking (Ai/Chat.cs): "Name: text", the name in its colour (Hud.ChatKind)
                        if (e.Str("kind") == "ai" && e.P("from") is { } from) Message($"{from.Name}: {text}", "chat:" + ((from.PartyColor ?? from.Color) & 0xffffff).ToString("x6"));
                        else Message(text, e.Str("kind") ?? "");
                    }
                    break;
                case "storagefull":
                    if (e.P("player") == me) Message("Storage full", "warn");
                    break;
                case "built":
                {
                    var b = e.B("building");
                    if (b != null && b.Owner == me) Message($"{Name(b)} completed", "");
                    break;
                }
                case "trained":
                {
                    var u = e.U("unit");
                    if (u != null && u.Owner == me) Marker("Finished", u.Pos.X, u.Pos.Z);
                    break;
                }
                case "upgrade":
                {
                    var a = e["action"] as Sim.Rules.ActionDef;
                    var p = e.P("player");
                    if (a == null) break;
                    if (Regex.IsMatch(a.Id, @"^age_\d$"))
                    {
                        var txt = $"Epoch {EPOCH[a.Id[4] - '0']}";
                        Message(p == me ? $"{txt} reached!" : $"The {p?.Name} have reached {txt}", p == me ? "good" : "warn");
                    }
                    else if (p == me) Message($"{W.Data.ActionText(a).Name} researched", "good");
                    var en = e.E("entity");
                    if (p == me && en != null) Marker("Finished", en.Pos.X, en.Pos.Z);     // (FeedbackEventMgr.usl:105-111)
                    break;
                }
                case "attacked":
                {
                    var en = e.E("entity");
                    if (en?.Owner == null || me == null) break;
                    if (en.Owner != me)
                    {
                        // an ally's (co-op seats, computer allies, a mission's allies): once a fight, the white marker
                        // of an ally's call for help (MiniMap2.usl AddEvent "AIHelp") and a line (the sound: the view's)
                        if (me.IsFriend(en.Owner) && _warn.Hit(en.Owner, W.Time))
                        {
                            Marker("AIHelp", en.Pos.X, en.Pos.Z);
                            Message($"Your ally {en.Owner.Name} is under attack!", "warn");
                        }
                        break;
                    }
                    // the minimap's attack marker: the hits of 0.15 s gathered, one marker at their middle (FeedbackEventMgr.usl:72-103)
                    if (_hits == null) { _hits = new(); _hitsT = W.Time; }
                    _hits.Add((en.Pos.X, en.Pos.Z));
                    // the warning: once a fight - again after 10 s without attacks (Player.usl IWasAttacked)
                    if (_warn.Hit(me, W.Time))
                    {
                        Message(en.Kind == "building" ? "Your base is under attack!" : "Your units are under attack!", "bad");
                        Pings.Add((en.Pos.X, en.Pos.Z, W.Time));
                    }
                    break;
                }
                case "died":
                case "destroyed":
                {
                    var en = e.E("entity");
                    if (en == null) break;
                    Lost(en);
                    if (e.Type == "destroyed" && en.Owner == me && !(e["silent"] is true)) Message($"{Name(en)} destroyed", "bad");
                    break;
                }
                // deleted by a mission (world.removeEntity): no death effects, just out of the selection and the groups
                case "removed": { var en = e.E("entity"); if (en != null) Lost(en); break; }
                case "owner": { var en = e.E("entity"); if (en != null && e.P("from") == me) Lost(en); break; }
                // _NT_DiplChangeEnemyAttacked: an attack order on a neutral player started a war (FightingObj.usl:6613-6617)
                case "diplomacy":
                {
                    if (e["attacked"] is true && !H.Multiplayer)
                    {
                        Player? a = e.P("a"), b = e.P("b");
                        if (a == me || b == me) Message($"You are now at war with the {(a == me ? b : a)?.Name}", "bad");
                    }
                    break;
                }
                // a computer ally's call for help (AiGoalGuardVillage.usl:630 SendChatMsg with its base: Game.usl:404-412
                // puts the white AIHelp marker there; the line is its "msg")
                case "aiHelp":
                {
                    var p = e.P("player");
                    double x = e.Num("x"), z = e.Num("z");
                    if (p != null && p != me && me != null && p.IsFriend(me) && (x != 0 || z != 0)) Marker("AIHelp", x, z);
                    break;
                }
                case "trap": { var v = e.E("victim"); if (v != null && v.Owner == me) Message("A trap was sprung!", "bad"); break; }
                case "warpgate":
                {
                    var b = e.B("building");
                    if (b == null) break;
                    bool mine = b.Owner == me;
                    var st = e.Str("state");
                    string? txt = st switch
                    {
                        "started" => "A warp gate is being built", "finished" => "A warp gate is complete - it must be destroyed within 10 minutes",
                        "destroyed" => "A warp gate was destroyed", "won" => "The warp gate has opened", _ => null,
                    };
                    if (txt != null) Message((mine ? "Your warp gate: " : "Enemy warp gate: ") + txt, mine ? "good" : "bad");
                    if (st != "destroyed") { Pings.Add((b.Pos.X, b.Pos.Z, W.Time)); Marker("Attack", b.Pos.X, b.Pos.Z); }
                    break;
                }
            }
        }
        if (_hits != null && W.Time - _hitsT >= 0.15)
        {
            int n = _hits.Count;
            double x = _hits.Sum(h => h.X) / n, z = _hits.Sum(h => h.Z) / n;
            _hits = null;
            Marker("Attack", x, z);
        }
    }
}
