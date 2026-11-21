// Lua data files of the map format (docs/MAP_FORMAT.md "Lua data files"): map.lua, world.lua, objects.lua, regions.lua,
// terrain/plants.lua, terrain/forest.lua, texts/<lang>.lua - each one `return { ... }`, a table of strings, numbers,
// booleans and tables. They are data, never code:
//
//   * a token check first (Check): the file may hold `return`, table constructors ({ }, [key] =, name =, commas and
//     semicolons), string literals, numbers (with a leading minus), true / false / nil and comments - nothing else. No
//     function, no call, no operator, no variable, no loop: a data file cannot compute anything, so it cannot run
//     forever or reach anything;
//   * then MoonSharp (the game's Lua) evaluates it in an empty environment (CoreModules.None: no libraries, no globals);
//   * the value is turned into JSON nodes (ToJson): a table with the keys 1..n is an array, a table with string keys an
//     object (in the order of the file); a table mixing both, a hole in an array, a key of another type is an error
//     naming the file and the path. An empty table is an array unless the reader's hint says the path holds an object
//     (EmptyObjects: the paths of the format whose value is a map, e.g. a mission's players.*.heroes).
//
// The writer (Write) is deterministic: keys in the order of the JSON object, numbers as the JSON had them (or the
// shortest text that reads back to the same double), strings in double quotes with escapes for \ " and control
// characters; short tables on one line.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;

namespace PwRemake.Sim.Maps;

/// <summary>An error in a Lua data file: the file (chunk) and line or path it is about.</summary>
public sealed class LuaDataException : Exception
{
    public LuaDataException(string message) : base(message) { }
}

public static class LuaData
{
    // ------------------------------------------------------------------------------------------- the token check
    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not",
        "or", "repeat", "return", "then", "true", "until", "while",
    };

    /// <summary>Throws a LuaDataException ("chunk:line: ...") unless the source is only `return` and a pure data
    /// constructor (see the file header).</summary>
    public static void Check(string src, string chunk)
    {
        int i = 0, line = 1, n = src.Length;
        // the tokens that matter for the grammar: kind and text
        string prev = "";            // the previous significant token kind: "return" "{" "}" "[" "]" "=" "," "name" "value" "-"
        bool sawReturn = false;
        int depth = 0;
        void Fail(string what) => throw new LuaDataException($"{chunk}:{line}: {what} - a data file holds only `return {{ ... }}` with strings, numbers, booleans and tables");
        if (n > 0 && src[0] == '﻿') i = 1;
        while (i < n)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (c == ' ' || c == '\t' || c == '\r') { i++; continue; }
            if (c == '-' && i + 1 < n && src[i + 1] == '-')
            {
                // a comment: --[[ ... ]] / --[==[ ... ]==] or to the end of the line
                i += 2;
                int lv = LongBracket(src, i);
                if (lv >= 0) { i = SkipLong(src, i, lv, ref line, chunk); continue; }
                while (i < n && src[i] != '\n') i++;
                continue;
            }
            if (!sawReturn)
            {
                if (IsNameStart(c) && Name(src, i) == "return") { sawReturn = true; prev = "return"; i += 6; continue; }
                Fail($"`{Snippet(src, i)}` before `return`");
            }
            if (c == '"' || c == '\'')
            {
                i = SkipShortString(src, i, ref line, chunk);
                Value();
                continue;
            }
            if (c == '[')
            {
                int lv = LongBracket(src, i);
                if (lv >= 0) { i = SkipLong(src, i, lv, ref line, chunk); Value(); continue; }
                if (prev != "{" && prev != ",") Fail("`[` not starting a key");
                prev = "["; i++; continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
            {
                i = SkipNumber(src, i);
                Value();
                continue;
            }
            if (IsNameStart(c))
            {
                var nm = Name(src, i);
                i += nm.Length;
                if (nm is "true" or "false" or "nil") { Value(); continue; }
                if (Keywords.Contains(nm)) Fail($"`{nm}` is not allowed");
                // a field name: { name = ... } / , name = ...
                if (prev != "{" && prev != ",") Fail($"`{nm}` (a variable or a call)");
                int j = i;
                while (j < n && (src[j] == ' ' || src[j] == '\t' || src[j] == '\r' || src[j] == '\n')) j++;
                if (j >= n || src[j] != '=' || (j + 1 < n && src[j + 1] == '=')) Fail($"`{nm}` (a variable or a call)");
                prev = "name";
                continue;
            }
            switch (c)
            {
                case '{':
                    if (prev is not ("return" or "{" or "," or "=" )) Fail("`{` in the wrong place");
                    depth++; prev = "{"; i++; continue;
                case '}':
                    if (depth == 0 || prev is not ("{" or "," or "value")) Fail("`}` in the wrong place");
                    depth--; prev = "value"; i++; continue;
                case ']':
                    if (prev != "value") Fail("`]` in the wrong place");
                    prev = "]"; i++; continue;
                case '=':
                    if (prev is not ("name" or "]")) Fail("`=` in the wrong place");
                    prev = "="; i++; continue;
                case ',':
                case ';':
                    if (depth == 0) { if (c == ';' && prev == "value") { prev = "end"; i++; continue; } Fail($"`{c}` outside a table"); }
                    if (prev != "value") Fail($"`{c}` in the wrong place");
                    prev = ","; i++; continue;
                case '-':
                    if (prev is not ("return" or "{" or "," or "=" or "[")) Fail("`-` (operators are not allowed)");
                    i++;
                    while (i < n && (src[i] == ' ' || src[i] == '\t')) i++;
                    if (i >= n || !(char.IsDigit(src[i]) || src[i] == '.')) Fail("`-` not before a number");
                    prev = "-"; continue;
                default:
                    Fail($"`{Snippet(src, i)}` is not allowed");
                    break;
            }
        }
        if (!sawReturn) Fail("no `return`");
        if (depth != 0 || prev is not ("value" or "end")) Fail("the table is not closed");

        void Value()
        {
            // a value may stand after `return`, `{`, `,`, `=`, `[` (a key), `-` (a number)
            if (prev is not ("return" or "{" or "," or "=" or "[" or "-")) Fail("a value in the wrong place (a missing comma?)");
            if (prev == "return" && depth == 0) { prev = "value"; return; }
            prev = "value";
        }
    }

    static string Snippet(string s, int i) => s.Substring(i, Math.Min(12, s.Length - i)).Split('\n')[0];
    static bool IsNameStart(char c) => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    static string Name(string s, int i)
    {
        int j = i;
        while (j < s.Length && (s[j] == '_' || char.IsAsciiLetterOrDigit(s[j]))) j++;
        return s.Substring(i, j - i);
    }

    /// <summary>at s[i] == '[': the level of a long bracket ([[ = 0, [=[ = 1 ...), else -1</summary>
    static int LongBracket(string s, int i)
    {
        if (i >= s.Length || s[i] != '[') return -1;
        int j = i + 1, lv = 0;
        while (j < s.Length && s[j] == '=') { lv++; j++; }
        return j < s.Length && s[j] == '[' ? lv : -1;
    }

    static int SkipLong(string s, int i, int lv, ref int line, string chunk)
    {
        string close = "]" + new string('=', lv) + "]";
        int start = i + lv + 2;
        int e = s.IndexOf(close, start, StringComparison.Ordinal);
        if (e < 0) throw new LuaDataException($"{chunk}:{line}: an unfinished long string or comment");
        for (int k = i; k < e; k++) if (s[k] == '\n') line++;
        return e + close.Length;
    }

    static int SkipShortString(string s, int i, ref int line, string chunk)
    {
        char q = s[i++];
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\') { if (i + 1 < s.Length && s[i + 1] == '\n') line++; i += 2; continue; }
            if (c == '\n') throw new LuaDataException($"{chunk}:{line}: an unfinished string");
            i++;
            if (c == q) return i;
        }
        throw new LuaDataException($"{chunk}:{line}: an unfinished string");
    }

    static int SkipNumber(string s, int i)
    {
        int n = s.Length;
        if (s[i] == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X'))
        {
            i += 2;
            while (i < n && (Uri.IsHexDigit(s[i]) || s[i] == '.' || s[i] == 'p' || s[i] == 'P' || ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'p' || s[i - 1] == 'P')))) i++;
            return i;
        }
        while (i < n && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E')))) i++;
        return i;
    }

    // ------------------------------------------------------------------------------------------- reading
    /// <summary>the value of a data file (checked, then evaluated by MoonSharp in an empty environment)</summary>
    public static DynValue Eval(string src, string chunk)
    {
        Check(src, chunk);
        var script = new Script(CoreModules.None);
        try { return script.DoString(src, null, chunk); }
        catch (InterpreterException e) { throw new LuaDataException($"{chunk}: {e.DecoratedMessage ?? e.Message}"); }
    }

    /// <summary>the data file as JSON nodes (see the header); emptyObjects: the paths whose empty table is an object
    /// ("players.*.heroes"; * = any key or index)</summary>
    public static JsonNode? ParseJson(string src, string chunk, IReadOnlyCollection<string>? emptyObjects = null)
    {
        var v = Eval(src, chunk);
        var buf = new ArrayBufferWriter();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { SkipValidation = false, Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            var patterns = emptyObjects?.Select(p => p.Split('.')).ToList() ?? new List<string[]>();
            var path = new List<string>();
            WriteJson(w, v, chunk, path, patterns);
        }
        return JsonNode.Parse(buf.WrittenSpan);
    }

    public static JsonNode? ReadJsonFile(string path, string chunk, IReadOnlyCollection<string>? emptyObjects = null) =>
        ParseJson(File.ReadAllText(path, Encoding.UTF8), chunk, emptyObjects);

    sealed class ArrayBufferWriter : System.Buffers.IBufferWriter<byte>
    {
        byte[] _b = new byte[1 << 16];
        int _n;
        public ReadOnlySpan<byte> WrittenSpan => _b.AsSpan(0, _n);
        public void Advance(int count) => _n += count;
        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _b.AsMemory(_n); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _b.AsSpan(_n); }
        void Ensure(int hint) { if (_b.Length - _n < Math.Max(hint, 256)) Array.Resize(ref _b, Math.Max(_b.Length * 2, _n + Math.Max(hint, 256))); }
    }

    static string PathText(List<string> path) => path.Count == 0 ? "(the file)" : string.Join(".", path);

    static bool Matches(List<string> path, List<string[]> patterns)
    {
        foreach (var p in patterns)
        {
            if (p.Length != path.Count) continue;
            bool ok = true;
            for (int i = 0; i < p.Length && ok; i++) ok = p[i] == "*" || p[i] == path[i];
            if (ok) return true;
        }
        return false;
    }

    static void WriteJson(Utf8JsonWriter w, DynValue v, string chunk, List<string> path, List<string[]> patterns)
    {
        switch (v.Type)
        {
            case DataType.Nil: case DataType.Void: w.WriteNullValue(); return;
            case DataType.Boolean: w.WriteBooleanValue(v.Boolean); return;
            case DataType.String: w.WriteStringValue(v.String); return;
            case DataType.Number:
            {
                double d = v.Number;
                if (!double.IsFinite(d)) throw new LuaDataException($"{chunk}: {PathText(path)}: a number that is not finite");
                w.WriteRawValue(NumText(d), skipInputValidation: true);
                return;
            }
            case DataType.Table:
            {
                var t = v.Table;
                int count = 0, ints = 0, strs = 0;
                foreach (var p in t.Pairs)
                {
                    count++;
                    if (p.Key.Type == DataType.String) strs++;
                    else if (p.Key.Type == DataType.Number && p.Key.Number == Math.Floor(p.Key.Number) && p.Key.Number >= 1) ints++;
                    else throw new LuaDataException($"{chunk}: {PathText(path)}: a key that is neither a name nor 1, 2, 3 ...");
                }
                if (count == 0)
                {
                    if (Matches(path, patterns)) { w.WriteStartObject(); w.WriteEndObject(); }
                    else { w.WriteStartArray(); w.WriteEndArray(); }
                    return;
                }
                if (strs > 0 && ints > 0) throw new LuaDataException($"{chunk}: {PathText(path)}: a table with both a list part and named fields");
                if (ints > 0)
                {
                    for (int k = 1; k <= ints; k++)
                        if (t.Get(k).IsNil()) throw new LuaDataException($"{chunk}: {PathText(path)}: a list with a gap at {k}");
                    w.WriteStartArray();
                    for (int k = 1; k <= ints; k++) { path.Add(k.ToString(CultureInfo.InvariantCulture)); WriteJson(w, t.Get(k), chunk, path, patterns); path.RemoveAt(path.Count - 1); }
                    w.WriteEndArray();
                    return;
                }
                w.WriteStartObject();
                foreach (var p in t.Pairs)
                {
                    w.WritePropertyName(p.Key.String);
                    path.Add(p.Key.String);
                    WriteJson(w, p.Value, chunk, path, patterns);
                    path.RemoveAt(path.Count - 1);
                }
                w.WriteEndObject();
                return;
            }
            default:
                throw new LuaDataException($"{chunk}: {PathText(path)}: a {v.Type.ToString().ToLowerInvariant()} is not data");
        }
    }

    /// <summary>a number as JSON / Lua text: whole numbers without a point, others the shortest text that reads back to
    /// the same double; -0 as -0.0</summary>
    public static string NumText(double d)
    {
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>a float32 value (the original's positions, quaternions) as the shortest text that reads back to the same
    /// float (through a double, as the reader does)</summary>
    public static string FloatText(double v)
    {
        float f = (float)v;
        if (f == 0) return float.IsNegative(f) ? "-0.0" : "0";
        if (f == MathF.Floor(f) && Math.Abs(f) < 1e7) return ((long)f).ToString(CultureInfo.InvariantCulture);
        var s = f.ToString("R", CultureInfo.InvariantCulture);
        if ((float)double.Parse(s, CultureInfo.InvariantCulture) == f) return s;
        return ((double)f).ToString("R", CultureInfo.InvariantCulture);        // (the rare double-rounding case: exact)
    }

    // ------------------------------------------------------------------------------------------- writing
    static readonly System.Text.RegularExpressions.Regex Ident = new("^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string Key(string k) => Ident.IsMatch(k) && !Keywords.Contains(k) ? k : "[" + Str(k) + "]";

    public static string Str(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32 || c == 127) sb.Append('\\').Append(((int)c).ToString("000", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>`return &lt;node&gt;` with a header comment (each line prefixed "-- "), ending in a new line</summary>
    public static string Write(JsonNode? node, string? header = null)
    {
        var sb = new StringBuilder();
        if (header != null) foreach (var l in header.Split('\n')) sb.Append("-- ").Append(l).Append('\n');
        sb.Append("return ");
        Value(sb, node, 0, "");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>a value as Lua text (tables over several lines with this indentation)</summary>
    public static void Value(StringBuilder sb, JsonNode? node, int indent, string where)
    {
        switch (node)
        {
            case null: throw new LuaDataException($"{where}: null in a list (Lua has no holes)");
            case JsonValue v: sb.Append(Scalar(v)); return;
            case JsonObject o:
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                var inline = Inline(o);
                if (inline != null && inline.Length + indent * 2 <= 116) { sb.Append(inline); return; }
                sb.Append("{\n");
                foreach (var (k, x) in o)
                {
                    if (x == null) continue;                            // (nil: the key is absent)
                    sb.Append(' ', indent * 2 + 2).Append(Key(k)).Append(" = ");
                    Value(sb, x, indent + 1, where + "." + k);
                    sb.Append(",\n");
                }
                sb.Append(' ', indent * 2).Append('}');
                return;
            }
            case JsonArray a:
            {
                if (a.Count == 0) { sb.Append("{}"); return; }
                var inline = Inline(a);
                if (inline != null && inline.Length + indent * 2 <= 116) { sb.Append(inline); return; }
                sb.Append("{\n");
                for (int i = 0; i < a.Count; i++)
                {
                    sb.Append(' ', indent * 2 + 2);
                    Value(sb, a[i], indent + 1, where + "[" + (i + 1) + "]");
                    sb.Append(",\n");
                }
                sb.Append(' ', indent * 2).Append('}');
                return;
            }
        }
    }

    /// <summary>a small table (scalars and small tables of scalars, at most 24 leaves) on one line, else null</summary>
    static string? Inline(JsonNode n)
    {
        int leaves = 0;
        bool Small(JsonNode? x, int depth)
        {
            if (x is JsonValue) return ++leaves <= 24;
            if (depth >= 2) return false;
            if (x is JsonObject o) { foreach (var (_, y) in o) if (y != null && !Small(y, depth + 1)) return false; return true; }
            if (x is JsonArray a) { foreach (var y in a) if (y == null || !Small(y, depth + 1)) return false; return true; }
            return true;
        }
        if (!Small(n, 0)) return null;
        var sb = new StringBuilder();
        void W(JsonNode x)
        {
            if (x is JsonValue v) { sb.Append(Scalar(v)); return; }
            if (x is JsonObject o)
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{ ");
                bool first = true;
                foreach (var (k, y) in o) { if (y == null) continue; if (!first) sb.Append(", "); first = false; sb.Append(Key(k)).Append(" = "); W(y); }
                sb.Append(first ? "}" : " }");
                return;
            }
            var a = (JsonArray)x;
            if (a.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{ ");
            for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(", "); W(a[i]!); }
            sb.Append(" }");
        }
        W(n);
        return sb.ToString();
    }

    public static string Scalar(JsonValue v)
    {
        if (v.TryGetValue<JsonElement>(out var el))
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return Str(el.GetString()!);
                case JsonValueKind.Number:
                {
                    // (a JSON number is a Lua number, written as the reader writes the numbers it reads - NumText - so a
                    // file converted from JSON ("16.0", "0.0") and the same data read and written again by the map
                    // editor are the same bytes; "-0" stays negative)
                    var raw = el.GetRawText();
                    return el.TryGetDouble(out var dn) && double.IsFinite(dn) ? NumText(dn) : raw == "-0" ? "-0.0" : raw;
                }
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                default: return "nil";
            }
        }
        if (v.TryGetValue<string>(out var s)) return Str(s);
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (v.TryGetValue<double>(out var d)) return NumText(d);
        if (v.TryGetValue<float>(out var f)) return FloatText(f);
        if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue<int>(out var i)) return i.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue<byte>(out var by)) return by.ToString(CultureInfo.InvariantCulture);
        return Str(v.ToJsonString());
    }
}
