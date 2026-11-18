// Player ("party") colours of the original game: Data/Base/Scripts/Game/misc/ACColors.txt, PlayerColor0..7.
//   light - used to tint the party-colour parts of models (materials with GSF flag 0x1000: banners, cloth, shields)
//   dark  - used on the minimap and for selection rings / health bars
// "none" keeps the models untinted (the look of the remake before colours were added).
// The remake's (games of up to 16 players, docs/SKIRMISH.md "Colours"): eight more colours after the original's eight
// (white, grey, lime, teal, navy, maroon, brown, olive - set apart from those by brightness or hue), and any colour of
// the RGB spectrum: a slot's colour id "#rrggbb" (the light colour as picked; the dark one derived: CustomColor).
//
// Ported from remake/src/game/colors.js (pw-exporter 98c7703). (isPartyMaterial, which reads a three.js material, is
// the view's: game/src/View.)
using System;
using System.Collections.Generic;

namespace PwRemake.Sim.Rules;

public sealed record PlayerColor(string Id, string Name, int[] Light, int[] Dark);

public static class Colors
{
    public static readonly IReadOnlyList<PlayerColor> PLAYER_COLORS = new[]
    {
        new PlayerColor("yellow", "Yellow", new[] { 255, 232, 128 }, new[] { 229, 198, 60 }),
        new PlayerColor("red", "Red", new[] { 225, 85, 85 }, new[] { 204, 51, 51 }),
        new PlayerColor("cyan", "Cyan", new[] { 115, 229, 229 }, new[] { 48, 191, 191 }),
        new PlayerColor("blue", "Blue", new[] { 121, 161, 242 }, new[] { 51, 102, 204 }),
        new PlayerColor("green", "Green", new[] { 121, 242, 121 }, new[] { 67, 191, 67 }),
        new PlayerColor("purple", "Purple", new[] { 153, 102, 204 }, new[] { 102, 51, 153 }),
        new PlayerColor("pink", "Pink", new[] { 242, 121, 202 }, new[] { 204, 51, 153 }),
        new PlayerColor("orange", "Orange", new[] { 242, 161, 121 }, new[] { 204, 102, 51 }),
        // the remake's (16 players)
        new PlayerColor("white", "White", new[] { 245, 245, 245 }, new[] { 220, 220, 220 }),
        new PlayerColor("grey", "Grey", new[] { 128, 128, 132 }, new[] { 110, 110, 116 }),
        new PlayerColor("lime", "Lime", new[] { 200, 255, 90 }, new[] { 150, 215, 30 }),
        new PlayerColor("teal", "Teal", new[] { 60, 170, 150 }, new[] { 20, 135, 115 }),
        new PlayerColor("navy", "Navy", new[] { 80, 90, 190 }, new[] { 45, 55, 150 }),
        new PlayerColor("maroon", "Maroon", new[] { 165, 50, 80 }, new[] { 130, 30, 60 }),
        new PlayerColor("brown", "Brown", new[] { 170, 118, 72 }, new[] { 130, 82, 40 }),
        new PlayerColor("olive", "Olive", new[] { 170, 165, 70 }, new[] { 130, 125, 35 }),
    };
    /// <summary>the original's colours (ACColors.txt PlayerColor0..7): the first eight of PLAYER_COLORS</summary>
    public const int ORIGINAL_COLORS = 8;

    /// <summary>minimap / UI colours when a player has no party colour</summary>
    public const int NEUTRAL_UI_ME = 0x3a8cff, NEUTRAL_UI_AI = 0xe0402a;

    public static int Hex(int[] rgb) => (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];

    /// <summary>a palette colour by id, or a custom colour "#rrggbb" (CustomColor); null: none / unknown</summary>
    public static PlayerColor? ColorById(string? id)
    {
        foreach (var c in PLAYER_COLORS) if (c.Id == id) return c;
        return ParseCustom(id) is int rgb ? CustomColor(rgb) : null;
    }

    /// <summary>the palette index of a colour id (-1: a custom colour or none)</summary>
    public static int IndexOf(string? id) { for (int i = 0; i < PLAYER_COLORS.Count; i++) if (PLAYER_COLORS[i].Id == id) return i; return -1; }

    /// <summary>"#rrggbb" -> 0xRRGGBB (null: not a custom colour)</summary>
    public static int? ParseCustom(string? id) =>
        id is { Length: 7 } && id[0] == '#' && int.TryParse(id.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : null;
    /// <summary>0xRRGGBB -> the custom colour id "#rrggbb"</summary>
    public static string CustomId(int rgb) => "#" + (rgb & 0xffffff).ToString("x6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>a colour picked from the whole RGB spectrum: the light colour (model tint) as picked, the dark one (minimap,
    /// rings, names) like the palette's pairs - more saturated, a little darker (integer maths: the same on every machine)</summary>
    public static PlayerColor CustomColor(int rgb)
    {
        int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        int max = Math.Max(r, Math.Max(g, b));
        // more saturation: every channel pulled 35 % further from the brightest one, then 88 % of the brightness
        int D(int c) => Math.Clamp((max - (max - c) * 135 / 100) * 88 / 100, 0, 255);
        return new PlayerColor(CustomId(rgb), "Custom " + CustomId(rgb), new[] { r, g, b }, new[] { D(r), D(g), D(b) });
    }

    /// <summary>how far apart two colours look (0..~765: a weighted RGB distance, "redmean"); below NEAR_DISTANCE two players
    /// are hard to tell apart</summary>
    public static double Distance(int a, int b)
    {
        int r1 = (a >> 16) & 255, g1 = (a >> 8) & 255, b1 = a & 255, r2 = (b >> 16) & 255, g2 = (b >> 8) & 255, b2 = b & 255;
        double rm = (r1 + r2) / 2.0, dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return Math.Sqrt((2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db);
    }
    /// <summary>colours closer than this (Distance of the light colours) count as the same in a game's setup (the
    /// palette's closest pair, cyan / teal, is ~150 apart)</summary>
    public const double NEAR_DISTANCE = 60;

    /// <summary>GSF material flag "use player colour" (attr1 bit 12, see claude/gsf_format_notes.md)</summary>
    public const int PARTY_FLAG = 0x1000;

    /// <summary>isPartyMaterial on the material's gsf_flags string ("1004/..." hex attr1 first)</summary>
    public static bool IsPartyFlags(string? gsfFlags)
    {
        if (string.IsNullOrEmpty(gsfFlags)) return false;
        var s = gsfFlags.Split('/')[0];
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int f) && (f & PARTY_FLAG) != 0;
    }
}
