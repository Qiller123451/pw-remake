// The hit point bars of the unit cards (the info window's portrait, Info.usl CUnitIcon; the army controller's cards,
// MIRAGE PyramidCard.usl), as the original draws them: blend layers of the card ("layer ovl_hpbar_bg" and one or two
// slices of "ovl_hpbar"), the slice commands done by the engine's bitmap blender (PWClient.exe 0x49bc1a "hslicehp",
// 0x49bf08 "hslicebar"; docs/HUD.md "Hit point bars").
//   - hslicebar <name> <p> r g b a: the left p % (p clamped to 0..100; width w * p / 100 texels, integer) of the
//     layer, each of its "red" texels (R > 2 (G + B): the mask - ovl_hpbar is only such texels, two rows at the
//     card's bottom) REPLACED by r g b (a = 256: fully), the other texels copied as they are.
//   - hslicehp <name> <p>: the same with the colour taken from p: below 50 red .. yellow (255, 5 p, 0), from 50
//     yellow .. green ((101 - p) * 5, 255, 0); full: (5, 255, 0), 0: (255, 0, 0).
// Which one (Info.usl 893-925, PyramidCard.usl 391-433): the option "Game/GraphicOptions/AlternativeColoringACHP"
// (OptionsMenu "_UI_OptionsM_CheckBox_ACHPAlternativeColoring") on: "layer ovl_hpbar_bg, hslicehp ovl_hpbar hp%";
// off (the original's default): the info window "layer ovl_hpbar_bg, hslicebar ovl_hpbar 100 0 0 0 256, hslicebar
// ovl_hpbar hp% <dark>", the owner's ACColors.txt "dark" colour (resources color11, wild animals color8..10); the army
// controller the same with the 100 % slice in the player's "counter" colour.
// No Godot types: the tests link this file.
using System;

namespace PwRemake.UI.Hud.Logic;

public static class HpBarRule
{
    /// <summary>the card's "hp%" (Info.usl: (iHP * 100) / iMaxHP, integers), 0..100</summary>
    public static int Slice(double hp, double maxHp)
    {
        if (!(maxHp > 0)) return 0;
        int max = (int)Math.Round(maxHp), cur = (int)Math.Ceiling(Math.Max(0, hp));
        return max <= 0 ? 0 : Math.Clamp(cur * 100 / max, 0, 100);
    }

    /// <summary>the colour hslicehp gives the bar at p % (0xRRGGBB)</summary>
    public static int HealthRgb(int p)
    {
        p = Math.Clamp(p, 0, 100);
        int r, g;
        if (p < 50) { r = 255; g = p * 5; }
        else { r = (101 - p) * 5; g = 255; }
        return (r << 16) | (g << 8);
    }

    /// <summary>the colour hslicehp gives a fraction of the hit points (the slice rounded as the card's)</summary>
    public static int HealthRgb(double hp, double maxHp) => HealthRgb(Slice(hp, maxHp));

    /// <summary>the texels of a layer w texels wide that a slice of p % covers</summary>
    public static int SliceWidth(int w, int p) => w * Math.Clamp(p, 0, 100) / 100;

    /// <summary>a texel the slice commands recolour ("red": R > 2 (G + B))</summary>
    public static bool IsMask(int r, int g, int b) => r > 2 * (g + b);

    // ------------------------------------------------------------ the option off: the colours of ACColors.txt
    /// <summary>ACColors.txt "dark" of color8 AnimalFriendly, color9 AnimalNeutral, color10 AnimalHostile, color11
    /// Resources (the values of MIRAGE's ReColor/Scripts/Game/misc/ACColors.txt; the same as GroundRings')</summary>
    public const int DARK_ANIMAL_FRIENDLY = 0x6ddc4d, DARK_ANIMAL_NEUTRAL = 0xe0e833, DARK_ANIMAL_HOSTILE = 0xe0612b, DARK_RESOURCES = 0x707983;

    /// <summary>the info window's bar colour with the option off (Info.usl 906-917): the owner's ACColors "dark"
    /// (colour slot 0..7; the remake's slots 8..15 and custom colours: the player's own dark colour); no owner:
    /// resources color11, animals by anml_aggressive_type (0 color9 neutral, 1 color10 hostile, else color8)</summary>
    public static int OwnerRgb(int? colorSlot, int ownerColor, bool resource, int aggressiveType)
    {
        if (colorSlot is int s)
        {
            if (s >= 0 && s < PwRemake.Sim.Rules.Colors.PLAYER_COLORS.Count) return PwRemake.Sim.Rules.Colors.Hex(PwRemake.Sim.Rules.Colors.PLAYER_COLORS[s].Dark);
            return ownerColor;
        }
        if (resource) return DARK_RESOURCES;
        return aggressiveType == 0 ? DARK_ANIMAL_NEUTRAL : aggressiveType == 1 ? DARK_ANIMAL_HOSTILE : DARK_ANIMAL_FRIENDLY;
    }

    /// <summary>ACColors "counter" of PlayerColor0..7: the army controller's full-width slice under the bar with the
    /// option off (MIRAGE PyramidCard.usl 427; the values of MIRAGE's ReColor ACColors.txt). -1: not one of the
    /// original's eight - the card then uses hslicehp (PyramidCard.usl 424)</summary>
    public static int CounterRgb(int colorSlot) => colorSlot switch
    {
        0 or 2 or 3 or 4 => 0xc9360a,
        1 or 5 or 6 or 7 => 0xe5c63c,
        _ => -1,
    };
}
