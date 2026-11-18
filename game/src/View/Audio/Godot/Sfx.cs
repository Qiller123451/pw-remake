// The short way for the game to make a sound: Sfx.PlayEvent("FX_CBigBuildingExplosion", pos), Sfx.Ui("ui_click"),
// Sfx.Voice("aje_allosaurus", "selected"), Sfx.Feedback.Selected(...), Sfx.Music.Trigger(...). All go to the one
// GameAudio node (GameAudio.I). Positions are the simulation's (x, y, z in metres); world sounds are heard within
// their original hearing distance of the camera's target (GameAudio.UpdateWorld) and limited as the original's
// (AudioCore.Limits: at most 21 at a time, 4 of one wav; docs/AUDIO.md "How many sounds").
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

public static class Sfx
{
    static AudioCore A => GameAudio.I.Core;

    /// <summary>A sound event of sounds.json (Scripts/Server/init sounds): a world sound at pos, or 2D when the event
    /// is global or pos is null. The trigger action PSND, the MIRAGE moves' sfx, effects' FX_ events.</summary>
    public static ISoundHandle? PlayEvent(string name, V3? pos = null) => A.Play(name, pos);
    /// <summary>The first existing event of a list.</summary>
    public static ISoundHandle? PlayFirst(IEnumerable<string> names, V3? pos = null) => A.PlayFirst(names, pos);
    /// <summary>An interface sound; the click when the named one does not exist.</summary>
    public static void Ui(string name) => GameAudio.I.Feedback.Ui(name);
    /// <summary>A unit class's speech ("selected", "task_goto", "gag" ...): voice_&lt;class&gt;_&lt;kind&gt;, else voice_&lt;kind&gt;; one at a time.</summary>
    public static void Voice(string unitClass, string kind) => GameAudio.I.Feedback.Voice(unitClass, kind);
    /// <summary>A weapon's hit on a material (MaterialEffects.txt).</summary>
    public static ISoundHandle? Hit(string? weaponFx, string? targetFx, V3 pos) => A.Hit(weaponFx, targetFx, pos);
    /// <summary>An animation sound event (AnimSounds.Fire's pick) of an entity the player sees; prio: the slot it takes
    /// (AnimSounds.Priority: idle chatter, walking / work, the fights).</summary>
    public static ISoundHandle? Anim(AnimSoundEvent ev, V3 pos, SoundPriority prio = SoundPriority.Activity, AudioChannel channel = AudioChannel.Sfx) => A.AnimEvent(ev, pos, prio, channel);
    /// <summary>A file below assets/snd/ (mission speech, cutscene sounds): volume 0..100, channel, offset seconds.</summary>
    public static ISoundHandle? File(string file, double vol = 100, V3? pos = null, double maxHear = 0, AudioChannel channel = AudioChannel.Sfx, double offset = 0) =>
        A.PlayFile(file, vol, pos, maxHear, channel, 1, 0, offset);

    /// <summary>The feedback rules of ui/feedback.js (selection, orders, world events).</summary>
    public static SoundFeedback Feedback => GameAudio.I.Feedback;
    /// <summary>The music manager (MusicMgr).</summary>
    public static Music Music => A.Mus;
    /// <summary>The ambience.</summary>
    public static Atmo Atmo => A.Atmo;
    /// <summary>Music turned down while somebody speaks (ui/mission.js: 0.5 during a dialogue line).</summary>
    public static void Duck(bool on) { A.DuckK = on ? 0.5 : 1; A.Mus.ApplyVolume(); }
}
