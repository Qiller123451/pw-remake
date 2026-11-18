// Player ("party") colours of the original game: Data/Base/Scripts/Game/misc/ACColors.txt, PlayerColor0..7.
//   light - used to tint the party-colour parts of models (materials with GSF flag 0x1000: banners, cloth, shields)
//   dark  - used on the minimap and for selection rings / health bars
// "none" keeps the models untinted (the look of the remake before colours were added).
//
// Ported from remake/src/game/colors.js (pw-exporter 98c7703). (The simulation keeps a player's colour as its id or
// index; this is the drawing side: the RGB values.)
using System;
using System.Linq;
using Godot;

namespace PwRemake.View.Materials;

public sealed record PartyColor(string Id, string Name, byte[] LightRgb, byte[] DarkRgb)
{
    /// <summary>The tint of party-colour materials (sRGB, as the albedo colour of a material).</summary>
    public Color Light => Color.Color8(LightRgb[0], LightRgb[1], LightRgb[2]);
    public Color Dark => Color.Color8(DarkRgb[0], DarkRgb[1], DarkRgb[2]);
    /// <summary>0xRRGGBB of the light colour (the JS kept colours as numbers).</summary>
    public int LightHex => (LightRgb[0] << 16) | (LightRgb[1] << 8) | LightRgb[2];
}

public static class PartyColors
{
    /// <summary>the palette (the simulation's Colors.PLAYER_COLORS: the original's eight, the remake's eight more)</summary>
    public static readonly PartyColor[] All = PwRemake.Sim.Rules.Colors.PLAYER_COLORS.Select(Of).ToArray();

    /// <summary>the drawing side of a simulation colour (a palette one or a custom "#rrggbb": Colors.ColorById)</summary>
    public static PartyColor Of(PwRemake.Sim.Rules.PlayerColor c) =>
        new(c.Id, c.Name, c.Light.Select(v => (byte)v).ToArray(), c.Dark.Select(v => (byte)v).ToArray());

    /// <summary>minimap / UI colours when a player has no party colour</summary>
    public static readonly Color NeutralMe = Color.FromHtml("#3a8cff"), NeutralAi = Color.FromHtml("#e0402a");

    public static PartyColor? ById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var c in All) if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) return c;
        return PwRemake.Sim.Rules.Colors.ColorById(id) is { } cu ? Of(cu) : null;
    }

    public static Color FromHex(int rgb) => Color.Color8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>GSF material flag "use player colour" (attr1 bit 12, see claude/gsf_format_notes.md)</summary>
    public const uint PARTY_FLAG = 0x1000;

    /// <summary>The first word of a material's gsf_flags extra ("00001001/00000000" -> 0x1001).</summary>
    public static uint GsfFlags1(string? flags)
    {
        if (string.IsNullOrEmpty(flags)) return 0;
        var s = flags.Split('/')[0];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    public static bool IsPartyFlags(string? gsfFlags) => (GsfFlags1(gsfFlags) & PARTY_FLAG) != 0;
}
