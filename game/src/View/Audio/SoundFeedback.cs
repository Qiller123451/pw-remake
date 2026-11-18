// Which sound plays for what the player does and what happens in the world: the sound side of ui/feedback.js.
// Mirrors the original client scripts: Game/mgr/UISndMgr.usl (selection/order acknowledgements with the
// "voice_<class>_<type>" -> "voice_<type>" fallback chain, the "gag" of a unit clicked again and again) and Game/Game.usl
// (Placed, Delivered, NextAge, ...), FeedbackEventMgr.usl (the minimap's attack events that also drive the combat music).
//
// The interface's feedback (messages, minimap markers, effects: the port of ui/feedback.js in game/src/UI) calls these
// at the same places the JS played its sounds; the visibility tests (fog of war, on screen) stay with the caller,
// which passes only what is seen. Nothing here changes the game.
// Ported from remake/src/ui/feedback.js (the sound decisions; pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

/// <summary>What the sound side needs of a selected / ordered entity. Key: its identity (the entity object or id).</summary>
public readonly record struct SoundUnit(object Key, string Name, double Level, bool Alive, bool IsUnit, bool Mine);

public sealed class SoundFeedback
{
    /// <summary>Order type of the interface -> UISndMgr's task name (the voice / ui event suffix).</summary>
    public static readonly Dictionary<string, string> OrderType = new()
    {
        ["move"] = "task_goto", ["attack"] = "task_attack", ["attackmove"] = "task_attack_walk", ["amove"] = "task_attack_walk",
        ["build"] = "task_build", ["heal"] = "task_healing", ["rally"] = "task_goto", ["wood"] = "task_get_wood",
        ["stone"] = "task_get_stone", ["food"] = "task_get_food",
        // the "trade" cursor's task (GameInputController: an item is taken with the trade cursor; the trade carts):
        // ui_task_trade is the treasure pick-up sound, no unit has a task_trade voice
        ["trade"] = "task_trade", ["pickup"] = "task_trade",
    };

    readonly AudioCore A;
    (object? E, int N) _rep = (null, 0);
    List<(double X, double Z)>? _hits;
    double _hitsT;

    public SoundFeedback(AudioCore audio) { A = audio; }

    /// <summary>The unit whose voice answers for a selection: the highest level one (UISndMgr.GetSoundEvent).</summary>
    public static SoundUnit? VoiceUnit(IEnumerable<SoundUnit> list)
    {
        SoundUnit? best = null;
        foreach (var e in list) if (e.Alive && e.IsUnit && (best == null || e.Level > best.Value.Level)) best = e;
        if (best != null) return best;
        foreach (var e in list) if (e.Alive) return e;
        return null;
    }

    public static (string[] Voice, string[] Ui) Names(string cls, string type) =>
        (new[] { $"voice_{cls}_{type}", $"voice_{type}" }, new[] { $"ui_{cls}_{type}", $"ui_{type}" });

    /// <summary>A selection: its click and the answer of its highest unit (others' units: the enemy / neutral click).</summary>
    public void Selected(IReadOnlyList<SoundUnit> list)
    {
        if (list.Count == 0) return;
        var v = VoiceUnit(list);
        if (v == null) return;
        var e = v.Value;
        if (!e.Mine) { A.PlayFirst(new[] { "ui_enemy_selected", "ui_neutral_selected", "ui_selected" }); return; }
        var n = Names(e.Name, "selected");
        // clicking the same unit again and again makes it complain ("gag")
        if (Equals(_rep.E, e.Key)) _rep.N++; else _rep = (e.Key, 0);
        A.PlayFirst(n.Ui);
        if (_rep.N >= 3 && A.Has($"voice_{e.Name}_gag")) { A.Ack(new[] { $"voice_{e.Name}_gag" }); _rep.N = 0; }
        else A.Ack(n.Voice);
    }

    /// <summary>An order (move, attack, build ... or a UISndMgr task name): its click and the voice.</summary>
    public void Ordered(string type, IReadOnlyList<SoundUnit> units)
    {
        var v = VoiceUnit(units);
        if (v == null) return;
        var t = OrderType.TryGetValue(type, out var tt) ? tt : type;
        var n = Names(v.Value.Name, t);
        A.PlayFirst(n.Ui);
        A.Ack(n.Voice);
    }

    /// <summary>A unit class's speech of a kind ("selected", "task_attack", "die" ...) - the fallback chain of UISndMgr.</summary>
    public void Voice(string unitClass, string kind) => A.Ack(Names(unitClass, kind).Voice);

    /// <summary>An interface sound (a button: the click when the named one does not exist).</summary>
    public void Ui(string name) => A.PlayFirst(new[] { name, "ui_click", "UI_click" });
    /// <summary>An order that cannot be given.</summary>
    public void Error() { A.PlayFirst(new[] { "ui_click_error" }); A.Ack(new[] { "voice_click_error" }); }

    // ---------------------------------------------------------------- world events (ui/feedback.js handle)
    /// <summary>itemTaken (seen): FightingObj.AddItem event 38.</summary>
    public void ItemTaken(V3 pos) => A.Play("FX_CPickupItem", pos);
    /// <summary>storagefull (mine): res = wood / stone / food.</summary>
    public void StorageFull(string res) => A.PlayFirst(new[] { $"ui_no_{res}", "ui_no_storage" });
    /// <summary>placed (my building): the tribe's placement click and speech.</summary>
    public void Placed(string tribe)
    {
        var t = (tribe ?? "").ToLowerInvariant();
        A.PlayFirst(new[] { "ui_" + t + "_placed", "ui_placed" });
        A.Ack(new[] { "voice_" + t + "_placed", "voice_placed" });
    }
    /// <summary>built (my building).</summary>
    public void Built(V3 pos) => A.Play("FX_CBuildingReadyFX", pos);
    /// <summary>delivered (my unit, on screen): unit class.</summary>
    public void Delivered(string unitClass) => A.PlayFirst(new[] { $"ui_{unitClass}_delivered" });
    /// <summary>trained (my unit).</summary>
    public void Trained(V3 pos)
    {
        // (JS: play(...) || play(...) - the second when the first event does not exist)
        if (A.Has("FX_CTecFX_CharacterSpawnFX")) A.Play("FX_CTecFX_CharacterSpawnFX", pos); else A.Play("Confirmping_Build_Character_Fx");
    }
    /// <summary>upgrade (mine): an epoch (age_N) or an invention.</summary>
    public void Upgrade(bool epoch)
    {
        if (epoch) { A.PlayFirst(new[] { "ui_nextage" }); A.Ack(new[] { "voice_nextage" }); }
        else A.PlayFirst(new[] { "ui_invention" });
    }
    /// <summary>levelup (seen): a character or a transport.</summary>
    public void LevelUp(bool character, V3 pos) => A.Play(character ? "FX_CLevelUpFX" : "FX_CLevelUpFX_Transport", pos);
    /// <summary>The under-attack warning of the player's own objects (the caller keeps Player.usl's timing:
    /// AttackWarnings).</summary>
    public void UnderAttackWarning() => A.PlayFirst(new[] { "ui_warn_under_attack" });
    /// <summary>An ally is under attack / calls for help: the newsticker line's sound (NewsTicker.usl:91 plays
    /// ui_message with every line - the computer ally's "Help! My Base is attacked ..." with its white AIHelp marker,
    /// AiGoalGuardVillage.usl:630, Game.usl:404-412). The caller keeps the timing (AttackWarnings, per ally).</summary>
    public void AllyUnderAttack() => A.PlayFirst(new[] { "ui_message" });
    /// <summary>died (seen): the vehicle's explosion, the unit's death cry (or the big dinosaurs' own) - world sounds
    /// with their hearing distance whoever the owner (the cries' events are "global" in the tables, but the original
    /// plays them with Play3DSound at the unit: Effects.usl CDeathSound).</summary>
    public void Died(string name, string cls, V3 pos)
    {
        if (cls == "VHCL") A.Play("FX_CVehicleExplosion", pos);
        if (A.Has($"voice_{name}_die")) A.Play($"voice_{name}_die", pos, AudioChannel.Unit, world: true);
        else if (name == "aje_allosaurus" || name == "Allosaurus") A.Play("FX_CAllosaurusDying", pos);
        else if (name.Contains("brachio", StringComparison.OrdinalIgnoreCase)) A.Play("FX_CBrachioDying", pos);
    }
    /// <summary>destroyed (explored, not silent): an Aje tent or a building.</summary>
    public void Destroyed(bool ajeOwner, V3 pos) => A.Play(ajeOwner ? "FX_CAje_Tent_Dem_Fx" : "FX_CBigBuildingExplosion", pos);
    /// <summary>The game's hit effect played (ui/gamefx.js impact): its sound event FX_&lt;effect&gt;.</summary>
    public void Effect(string fxName, V3 pos) => A.Play("FX_" + fxName, pos);
    /// <summary>impact (seen, when the game's hit effect did not play): a blast (splash radius &gt; 6: the medium one) or the
    /// weapon's ground hit when it hit no target.</summary>
    public void Impact(bool splash, double splashRadius, string? weaponFx, bool hasTarget, bool hasWeapon, V3 pos)
    {
        if (splash) A.Play(splashRadius > 6 ? "FX_CMedExplo02" : "FX_CMedExplo01", pos);
        else if (!hasTarget && hasWeapon) A.Hit(weaponFx, "Ground", pos);
    }
    /// <summary>hit (seen): MaterialEffects of the weapon on the target's material.</summary>
    public void Hit(string? weaponFx, string? targetFx, V3 pos) => A.Hit(weaponFx, string.IsNullOrEmpty(targetFx) ? "Unit" : targetFx, pos);
    /// <summary>boarded (mine).</summary>
    public void Boarded() => A.PlayFirst(new[] { "ui_board", "ui_click" });
    /// <summary>sfx (seen): a named event (the MIRAGE moves).</summary>
    public void Sfx(string name, V3 pos) => A.Play(name, pos);
    /// <summary>blast (seen).</summary>
    public void Blast(V3 pos) => A.Play("FX_CBigBuildingExplosion", pos);

    // ---------------------------------------------------------------- attacks -> minimap + combat music
    /// <summary>A hit on one of the player's objects (attacked: the entity is mine). Gathered for 0.15 s.</summary>
    public void Attacked(double x, double z, double time)
    {
        if (_hits == null) { _hits = new(); _hitsT = time; }
        _hits.Add((x, z));
    }

    /// <summary>After a frame's events: the hits of 0.15 s become one attack at their middle (FeedbackEventMgr.usl:72-103)
    /// - the minimap's marker (returned for the caller) and the music's combat switch (Game.usl MiniMapEvent ->
    /// MusicMgr.OnUnderAttack).</summary>
    public (double X, double Z)? FlushAttacks(double time)
    {
        if (_hits == null || time - _hitsT < 0.15) return null;
        int n = _hits.Count;
        double x = _hits.Sum(h => h.X) / n, z = _hits.Sum(h => h.Z) / n;
        _hits = null;
        A.Mus.Attack(x, z, time);
        return (x, z);
    }
}

/// <summary>
/// When "under attack" is said (Player.usl IWasAttacked, WARN_UNDER_ATTACK_TIME = 10): at the first attack, then not
/// again until the attacks stopped for more than 10 whole seconds - one warning per fight, not one every 10 s. One
/// timer per attacked player (the key: the player's own objects, or each ally). Game time.
/// </summary>
public sealed class AttackWarnings
{
    public const int WarnTime = 10;
    readonly Dictionary<object, (double Last, bool Spent)> _p = new();

    /// <summary>An attack on who's objects at time: true when it is to be warned of.</summary>
    public bool Hit(object who, double time)
    {
        bool spent = _p.TryGetValue(who, out var s) && s.Spent && (int)(time - s.Last) <= WarnTime;
        _p[who] = (time, true);
        return !spent;
    }
}
