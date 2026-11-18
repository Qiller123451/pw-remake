// Position and length of an AnimationPlayer's clip without Godot's "AnimationPlayer has no current animation" error:
// once a clip played to its end (a one-shot clip during Advance) the player has no current animation, and
// CurrentAnimationPosition / CurrentAnimationLength log an error and return 0. These read the assigned clip instead.
using Godot;

namespace PwRemake.View.Anim;

public static class AnimPos
{
    /// <summary>the clip's position; at its end when it played out, 0 when there is none</summary>
    public static double Position(AnimationPlayer p)
    {
        // (IsPlaying: Godot's current_animation is the assigned clip while playing, else "" - asking for it made a
        // string every frame for every unit)
        if (p.IsPlaying()) return p.CurrentAnimationPosition;
        return p.HasAnimation(p.AssignedAnimation) ? p.GetAnimation(p.AssignedAnimation).Length : 0;
    }
    /// <summary>the assigned clip's length (0 when there is none)</summary>
    public static double Length(AnimationPlayer p) =>
        p.IsPlaying() ? p.CurrentAnimationLength : p.HasAnimation(p.AssignedAnimation) ? p.GetAnimation(p.AssignedAnimation).Length : 0;
}
