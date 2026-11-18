// JSON helpers. The game data from the toolkit (gamedata.json, techtree.json, manifest.json, campaign data ...) is
// read as JsonNode trees and converted into typed objects where the code needs them often; settings and our own
// files use System.Text.Json serialisation with these options (human-readable, indented: docs/CONTENT.md).
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PwRemake.Sim.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    static readonly Logger L = Log.For("Json");

    public static JsonNode? ParseFile(string path)
    {
        using var fs = File.OpenRead(path);
        return JsonNode.Parse(fs, documentOptions: DocOptions);
    }

    public static JsonNode? TryParseFile(string path)
    {
        try { return File.Exists(path) ? ParseFile(path) : null; }
        catch (Exception ex) { L.Error($"cannot read {path}", ex); return null; }
    }

    public static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null; }
        catch (Exception ex) { L.Error($"cannot read {path}", ex); return null; }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }

    // ---- reading loosely typed values (the toolkit's data keeps many numbers as strings, like the original) ----

    public static string Str(this JsonNode? n, string def = "")
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out string? s)) return s ?? def;
            if (v.TryGetValue(out double d)) return d.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue(out bool b)) return b ? "1" : "0";
        }
        return def;
    }

    public static double Num(this JsonNode? n, double def = 0)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out double d)) return d;
            if (v.TryGetValue(out string? s) && TryNum(s, out d)) return d;
            if (v.TryGetValue(out bool b)) return b ? 1 : 0;
        }
        return def;
    }

    public static int Int(this JsonNode? n, int def = 0) => n == null ? def : (int)Math.Round(n.Num(def));

    public static bool Bool(this JsonNode? n, bool def = false)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out bool b)) return b;
            if (v.TryGetValue(out double d)) return d != 0;
            if (v.TryGetValue(out string? s)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return def;
    }

    /// <summary>parseFloat semantics of the JS remake: leading number of the string, else false.</summary>
    public static bool TryNum(string? s, out double d)
    {
        d = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return true;
        int i = 0;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
        return i > 0 && double.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    public static double ParseNum(string? s, double def = 0) => TryNum(s, out var d) ? d : def;
}
