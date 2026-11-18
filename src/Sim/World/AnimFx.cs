// The effect events of the models' animations that the remake plays: the camera shakes of the brachiosaurus' footfalls
// and fall and of the mammoth's stomp - from the GSF header 1 "dusttrail" table (gsf.ksy dusttrail_table: model,
// animation, effect class, frame at 25 fps; remake/docs/GAME_EFFECTS.md section 4). They join the clips' sound events
// (AnimCtl.SetEvents) as entries with the wav "!fx" and the effect class as their group, so they come as "animSound"
// events like the sounds (only while World.AnimSounds is on - nothing in the simulation depends on them); the view
// shakes the camera (and shows the effect's model) for them.
//
// Ported from remake/src/game/animfx.js ANIM_FX and the merge in game/entities.js buildModel (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Content;

namespace PwRemake.Sim.World;

public static class AnimFx
{
    /// <summary>the wav of an effect event (its group is the effect class)</summary>
    public const string FX = "!fx";

    /// <summary>model (or the model whose clips it plays) -> clip -> (frame at 25 fps, effect class)</summary>
    public static readonly Dictionary<string, Dictionary<string, (int Frame, string Cls)[]>> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brachiosaurus"] = new(StringComparer.Ordinal)
        {
            ["walk_1"] = new[] { (24, "CBrachioWalking"), (38, "CBrachioWalking"), (60, "CBrachioWalking"), (84, "CBrachioWalking") },
            ["walk_2"] = new[] { (21, "CBrachioWalking"), (37, "CBrachioWalking"), (45, "CBrachioWalking"), (57, "CBrachioWalking") },
            ["walk_left"] = new[] { (24, "CBrachioWalking") },
            ["walk_right"] = new[] { (24, "CBrachioWalking") },
            ["dying"] = new[] { (58, "CBrachioDying") },
        },
        ["mammoth"] = new(StringComparer.Ordinal) { ["stomp"] = new[] { (52, "CPlaceBuildingFX") } },
    };

    /// <summary>a model's sound events with its effect events added (a new map; the manifest's stays as it is), or the
    /// sound events themselves when the model has none</summary>
    public static Dictionary<string, List<AnimSoundEvent>>? With(Dictionary<string, List<AnimSoundEvent>>? sounds, string? model)
    {
        if (string.IsNullOrEmpty(model) || !Table.TryGetValue(model, out var fx)) return sounds;
        var res = new Dictionary<string, List<AnimSoundEvent>>(StringComparer.Ordinal);
        if (sounds != null) foreach (var (k, v) in sounds) res[k] = new List<AnimSoundEvent>(v);
        foreach (var (clip, list) in fx)
        {
            if (!res.TryGetValue(clip, out var l)) res[clip] = l = new List<AnimSoundEvent>();
            foreach (var (f, cls) in list) l.Add(new AnimSoundEvent(f / 25.0, FX, 0, 0, cls));
        }
        return res;
    }
}
