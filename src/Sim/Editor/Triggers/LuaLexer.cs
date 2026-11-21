// A Lua 5.2 lexer for the map editor's trigger file reader (TriggerScript.Parse, docs/MAP_EDITOR.md "Triggers"): turns
// triggers.lua into tokens that keep their place in the text (offset, line), so the reader can take the source of a
// code block (Script / Check functions, custom statements) verbatim and keep the comments next to the triggers and
// their conditions / actions. It also finds Lua syntax errors that are lexical (an unfinished string or comment); the
// rest of the syntax check is MoonSharp's (TriggerValidation.CheckLua). No Godot types.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PwRemake.Sim.Editor.Triggers;

public enum LuaTok { Name, Keyword, String, Number, Symbol, Comment, Eof }

/// <summary>one token: its kind, the source text (Raw), for a string its value, for a comment its text without the
/// dashes; Start / End are offsets into the source (End exclusive), Line is 1-based</summary>
public sealed record LuaToken(LuaTok Kind, string Raw, string Value, int Start, int End, int Line)
{
    public bool Is(string sym) => (Kind == LuaTok.Symbol || Kind == LuaTok.Keyword) && Raw == sym;
    public bool IsName(string n) => Kind == LuaTok.Name && Raw == n;
    public override string ToString() => $"{Kind} '{Raw}' @{Line}";
}

public sealed class LuaSyntaxException : Exception
{
    public readonly int Line;
    public LuaSyntaxException(int line, string message) : base($"line {line}: {message}") { Line = line; }
}

public static class LuaLexer
{
    public static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not",
        "or", "repeat", "return", "then", "true", "until", "while",
    };

    // the symbols, longest first
    static readonly string[] Symbols =
    {
        "...", "..", "==", "~=", "<=", ">=", "::", "//", ">>", "<<",
        "+", "-", "*", "/", "%", "^", "#", "&", "~", "|", "<", ">", "=", "(", ")", "{", "}", "[", "]", ";", ":", ",", ".",
    };

    /// <summary>every token of the source, comments included, ending with Eof; throws LuaSyntaxException for an
    /// unfinished string or long comment, a malformed number or a character Lua does not know</summary>
    public static List<LuaToken> Tokens(string src)
    {
        var res = new List<LuaToken>();
        int i = 0, line = 1, n = src.Length;
        // a "#!" first line is skipped by Lua
        if (src.StartsWith("#", StringComparison.Ordinal)) { while (i < n && src[i] != '\n') i++; }
        while (true)
        {
            // white space
            while (i < n && (src[i] is ' ' or '\t' or '\r' or '\n' or '\f' or '\v'))
            {
                if (src[i] == '\n') line++;
                i++;
            }
            if (i >= n) { res.Add(new LuaToken(LuaTok.Eof, "", "", n, n, line)); return res; }
            int start = i, startLine = line;
            char c = src[i];
            // comments
            if (c == '-' && i + 1 < n && src[i + 1] == '-')
            {
                i += 2;
                int lvl = LongOpen(src, i);
                if (lvl >= 0)
                {
                    int bodyStart = i + lvl + 2;
                    int close = LongClose(src, bodyStart, lvl);
                    if (close < 0) throw new LuaSyntaxException(startLine, "unfinished long comment");
                    string body = src.Substring(bodyStart, close - bodyStart);
                    i = close + lvl + 2;
                    line += Count(body, '\n');
                    res.Add(new LuaToken(LuaTok.Comment, src[start..i], body, start, i, startLine));
                }
                else
                {
                    while (i < n && src[i] != '\n') i++;
                    var text = src.Substring(start + 2, i - start - 2).TrimEnd('\r');
                    res.Add(new LuaToken(LuaTok.Comment, src[start..i].TrimEnd('\r'), text, start, start + 2 + text.Length, startLine));
                }
                continue;
            }
            // names and keywords
            if (char.IsLetter(c) || c == '_')
            {
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                var w = src[start..i];
                res.Add(new LuaToken(Keywords.Contains(w) ? LuaTok.Keyword : LuaTok.Name, w, w, start, i, startLine));
                continue;
            }
            // numbers
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
            {
                if (c == '0' && i + 1 < n && (src[i + 1] is 'x' or 'X'))
                {
                    i += 2;
                    while (i < n && (Uri.IsHexDigit(src[i]) || src[i] == '.' || ((src[i] is 'p' or 'P') && i + 1 < n) || ((src[i] is '+' or '-') && (src[i - 1] is 'p' or 'P')))) i++;
                }
                else
                {
                    while (i < n && (char.IsDigit(src[i]) || src[i] == '.' || src[i] is 'e' or 'E' || ((src[i] is '+' or '-') && (src[i - 1] is 'e' or 'E')))) i++;
                }
                var raw = src[start..i];
                if (ParseNumber(raw) is null) throw new LuaSyntaxException(startLine, $"malformed number near '{raw}'");
                res.Add(new LuaToken(LuaTok.Number, raw, raw, start, i, startLine));
                continue;
            }
            // strings
            if (c == '"' || c == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= n || src[i] == '\n') throw new LuaSyntaxException(startLine, "unfinished string");
                    char d = src[i];
                    if (d == c) { i++; break; }
                    if (d == '\\')
                    {
                        i++;
                        if (i >= n) throw new LuaSyntaxException(startLine, "unfinished string");
                        char e = src[i];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); i++; break;
                            case 't': sb.Append('\t'); i++; break;
                            case 'r': sb.Append('\r'); i++; break;
                            case 'a': sb.Append('\a'); i++; break;
                            case 'b': sb.Append('\b'); i++; break;
                            case 'f': sb.Append('\f'); i++; break;
                            case 'v': sb.Append('\v'); i++; break;
                            case '\\': sb.Append('\\'); i++; break;
                            case '"': sb.Append('"'); i++; break;
                            case '\'': sb.Append('\''); i++; break;
                            case '\n': sb.Append('\n'); line++; i++; break;
                            case '\r': sb.Append('\n'); i++; if (i < n && src[i] == '\n') { i++; } line++; break;
                            case 'x':
                                if (i + 2 >= n || !Uri.IsHexDigit(src[i + 1]) || !Uri.IsHexDigit(src[i + 2])) throw new LuaSyntaxException(startLine, "hexadecimal digit expected");
                                sb.Append((char)int.Parse(src.Substring(i + 1, 2), NumberStyles.HexNumber)); i += 3; break;
                            case 'z':
                                i++;
                                while (i < n && char.IsWhiteSpace(src[i])) { if (src[i] == '\n') line++; i++; }
                                break;
                            case 'u':
                                {
                                    if (i + 1 >= n || src[i + 1] != '{') throw new LuaSyntaxException(startLine, "missing '{' in \\u{xxxx}");
                                    int j = i + 2; while (j < n && Uri.IsHexDigit(src[j])) j++;
                                    if (j >= n || src[j] != '}' || j == i + 2) throw new LuaSyntaxException(startLine, "malformed \\u{xxxx}");
                                    sb.Append(char.ConvertFromUtf32(int.Parse(src.Substring(i + 2, j - i - 2), NumberStyles.HexNumber)));
                                    i = j + 1;
                                    break;
                                }
                            default:
                                if (char.IsDigit(e))
                                {
                                    int j = i, v = 0;
                                    while (j < n && j < i + 3 && char.IsDigit(src[j])) { v = v * 10 + (src[j] - '0'); j++; }
                                    if (v > 255) throw new LuaSyntaxException(startLine, "decimal escape too large");
                                    sb.Append((char)v); i = j;
                                }
                                else throw new LuaSyntaxException(startLine, $"invalid escape sequence '\\{e}'");
                                break;
                        }
                        continue;
                    }
                    sb.Append(d); i++;
                }
                res.Add(new LuaToken(LuaTok.String, src[start..i], sb.ToString(), start, i, startLine));
                continue;
            }
            if (c == '[')
            {
                int lvl = LongOpen(src, i);
                if (lvl >= 0)
                {
                    int bodyStart = i + lvl + 2;
                    int close = LongClose(src, bodyStart, lvl);
                    if (close < 0) throw new LuaSyntaxException(startLine, "unfinished long string");
                    string body = src.Substring(bodyStart, close - bodyStart);
                    // (a new line right after the opening bracket is not part of the string)
                    string val = body.StartsWith("\r\n", StringComparison.Ordinal) ? body[2..] : body.StartsWith('\n') ? body[1..] : body;
                    i = close + lvl + 2;
                    line += Count(body, '\n');
                    res.Add(new LuaToken(LuaTok.String, src[start..i], val.Replace("\r\n", "\n"), start, i, startLine));
                    continue;
                }
            }
            // symbols
            string? sym = null;
            foreach (var s in Symbols) if (string.CompareOrdinal(src, i, s, 0, s.Length) == 0) { sym = s; break; }
            if (sym == null) throw new LuaSyntaxException(startLine, $"unexpected symbol '{c}'");
            i += sym.Length;
            res.Add(new LuaToken(LuaTok.Symbol, sym, sym, start, i, startLine));
        }
    }

    /// <summary>at src[i] = '[' followed by '='* and '[': the number of '='; else -1</summary>
    static int LongOpen(string src, int i)
    {
        if (i >= src.Length || src[i] != '[') return -1;
        int j = i + 1, lvl = 0;
        while (j < src.Length && src[j] == '=') { j++; lvl++; }
        return j < src.Length && src[j] == '[' ? lvl : -1;
    }

    /// <summary>the offset of the closing ']' '='* ']' of a long bracket of that level from i; -1 if none</summary>
    static int LongClose(string src, int i, int lvl)
    {
        var close = "]" + new string('=', lvl) + "]";
        return src.IndexOf(close, i, StringComparison.Ordinal);
    }

    static int Count(string s, char c) { int k = 0; foreach (var x in s) if (x == c) k++; return k; }

    /// <summary>a Lua number literal's value (decimal, hexadecimal with an optional binary exponent); null if malformed</summary>
    public static double? ParseNumber(string raw)
    {
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string body = raw[2..];
            int p = body.IndexOfAny(new[] { 'p', 'P' });
            string mant = p >= 0 ? body[..p] : body;
            int exp = 0;
            if (p >= 0 && !int.TryParse(body[(p + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp)) return null;
            int dot = mant.IndexOf('.');
            string ip = dot >= 0 ? mant[..dot] : mant, fp = dot >= 0 ? mant[(dot + 1)..] : "";
            if (ip.Length + fp.Length == 0) return null;
            double v = 0;
            foreach (var ch in ip) { if (!Uri.IsHexDigit(ch)) return null; v = v * 16 + Convert.ToInt32(ch.ToString(), 16); }
            double f = 1.0 / 16;
            foreach (var ch in fp) { if (!Uri.IsHexDigit(ch)) return null; v += Convert.ToInt32(ch.ToString(), 16) * f; f /= 16; }
            return v * Math.Pow(2, exp);
        }
        return double.TryParse(raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>a Lua string literal of a value (double quotes; \\ \" \n \t \r and other control characters escaped,
    /// everything else kept - the converter's lua_str)</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (ch < 32 || ch == 127) sb.Append('\\').Append(((int)ch).ToString("000", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>is s a Lua name that can stand as a table key without brackets</summary>
    public static bool IsIdent(string s)
    {
        if (s.Length == 0 || !(char.IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        foreach (var ch in s) if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) return false;
        return !Keywords.Contains(s);
    }

    /// <summary>a table key as written: name or ["any string"]</summary>
    public static string Key(string k) => IsIdent(k) ? k : "[" + Quote(k) + "]";
}
