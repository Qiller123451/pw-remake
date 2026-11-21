// The map editor's model of a map's triggers.lua (docs/MAP_EDITOR.md "Triggers", the syntax: docs/LUA_MAPS.md): the
// declarative part - `trigger "name" { ... }` with its flags, expression, conditions and actions and their parameters -
// as objects the trigger editor changes, and everything it cannot express kept as Lua text ("custom script" blocks):
//
//   EdTrigger      a trigger whose fields are all literals (name, guid, folder, description, flags, expr, the lists)
//   EdNode         one condition / action: Typed (Name{ key = literal, ... } or Node("XXXX"){ ... }; a parameter that is
//                  not a literal is kept as its Lua expression), Script / Check (Lua functions: their source as text),
//                  Raw (any other expression in a list: kept as text)
//   EdCode         a statement of the file that is not such a trigger (a local, a function, a loop that makes triggers,
//                  a trigger with a computed field): kept as text, in its place among the triggers
//
// TriggerScript.Parse reads the text (LuaLexer: comments are kept - a trigger's description lines, a node's label line
// before it and the reference notes after it); Write gives the text back in the converter's layout
// (tools/prepare/campaign_to_lua.py: the folder separators, one node per line up to 150 characters), so an original
// mission's triggers.lua read and written again is the same file (EditorTriggersTests). Parameter values keep how they
// were written (a number stays a number, "1" stays a string: the engine reads both as the same string, LuaHost.ParamString).
// No Godot types.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PwRemake.Sim.Scripting;

namespace PwRemake.Sim.Editor.Triggers;

/// <summary>how a parameter value is written</summary>
public enum ParamKind { String, Number, Bool, Expr }

/// <summary>a parameter value as written in the file (Text: the string's value; a number's or an expression's source; "true" / "false")</summary>
public sealed record ParamValue(ParamKind Kind, string Text)
{
    /// <summary>the string the engine gets (LuaHost.ParamString); null for an expression (known only when the file runs)</summary>
    public string? Value => Kind switch
    {
        ParamKind.String => Text,
        ParamKind.Number => LuaLexer.ParseNumber(Text.TrimStart('-')) is double d ? TrigUtil.NumStr(Text.StartsWith('-') ? -d : d) : Text,
        ParamKind.Bool => Text == "true" ? "1" : "0",
        _ => null,
    };

    /// <summary>the Lua source</summary>
    public string ToLua() => Kind == ParamKind.String ? LuaLexer.Quote(Text) : Text;

    static readonly System.Text.RegularExpressions.Regex CanonInt = new(@"^(0|-?[1-9][0-9]{0,14})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>a value typed into the editor: a canonical integer becomes a number (as the converter writes them), the
    /// rest a string</summary>
    public static ParamValue Of(string value) => CanonInt.IsMatch(value) ? new ParamValue(ParamKind.Number, value) : new ParamValue(ParamKind.String, value);
    public static ParamValue Str(string s) => new(ParamKind.String, s);
    public override string ToString() => ToLua();
}

/// <summary>one parameter of a node: its key and value</summary>
public sealed class EdParam
{
    public string Key;
    public ParamValue Value;
    public EdParam(string key, ParamValue value) { Key = key; Value = value; }
    public EdParam Clone() => new(Key, Value);
}

public enum NodeForm { Typed, Script, Check, Raw }

/// <summary>a condition or an action of a trigger</summary>
public sealed class EdNode
{
    public NodeForm Form = NodeForm.Typed;
    /// <summary>Typed: the 4-letter type ("REGN"); Script: SCRP, Check: CHCK, Raw: ""</summary>
    public string Code = "";
    /// <summary>Typed: the constructor as written ("Region", "REGN" or "Node(\"XXXX\")")</summary>
    public string Ctor = "";
    public List<EdParam> Params = new();
    /// <summary>Script / Check / Raw: the whole expression's source</summary>
    public string Source = "";
    /// <summary>the comment lines before the node (the original editor's node name and note), without "-- "</summary>
    public List<string> Label = new();
    /// <summary>the comment after the node on its line (the converter's reference notes), without "-- "; null: none</summary>
    public string? Trail;

    public bool IsCode => Form != NodeForm.Typed;

    public ParamValue? Get(string key) => Params.FirstOrDefault(p => p.Key == key)?.Value;
    /// <summary>the engine's string of a parameter, null when absent</summary>
    public string? Value(string key) => Get(key)?.Value;

    /// <summary>sets a parameter (kept at its place, else added at the end); null removes it</summary>
    public void Set(string key, ParamValue? v)
    {
        int i = Params.FindIndex(p => p.Key == key);
        if (v == null) { if (i >= 0) Params.RemoveAt(i); return; }
        if (i >= 0) Params[i].Value = v; else Params.Add(new EdParam(key, v));
    }

    public EdNode Clone() => new()
    {
        Form = Form, Code = Code, Ctor = Ctor, Params = Params.Select(p => p.Clone()).ToList(), Source = Source, Label = Label.ToList(), Trail = Trail,
    };

    /// <summary>a new node of a type: its readable name as the constructor</summary>
    public static EdNode Of(string code, bool condition)
    {
        if (code == "SCRP") return new EdNode { Form = NodeForm.Script, Code = code, Source = "Script(function(api)\n      -- your code\n    end)" };
        if (code == "CHCK") return new EdNode { Form = NodeForm.Check, Code = code, Source = "Check(function(api)\n      return true\n    end)" };
        var e = TriggerTypes.All.FirstOrDefault(t => t.Code == code && t.IsCondition == condition);
        return new EdNode { Code = code, Ctor = e?.Name ?? $"Node({LuaLexer.Quote(code)})" };
    }

    public override string ToString() => Form == NodeForm.Typed ? Ctor : Form.ToString();
}

/// <summary>an item of the file: a trigger or a block of Lua code</summary>
public abstract class TriggerItem
{
    /// <summary>comment lines before the item (not the folder separators the writer makes), without "-- "</summary>
    public List<string> Lead = new();
    public abstract TriggerItem Copy();
}

/// <summary>a statement the editor keeps as Lua text (shown and edited as "custom script")</summary>
public sealed class EdCode : TriggerItem
{
    /// <summary>the statement's source (its own comments included)</summary>
    public string Source = "";
    /// <summary>what it is, for the tree ("trigger \"x\" (custom)", "local function ...")</summary>
    public string Title = "";
    /// <summary>the name of the trigger it defines, when it is one (`trigger "name"` with a field the editor cannot express)</summary>
    public string? TriggerName;
    public override TriggerItem Copy() => new EdCode { Source = Source, Title = Title, TriggerName = TriggerName, Lead = Lead.ToList() };
    public override string ToString() => Title;
}

public sealed class EdTrigger : TriggerItem
{
    public string Name = "", Guid = "", Folder = "Root", Expr = "";
    /// <summary>the description: as comment lines at the top of the trigger (the converter's), or as a field when the
    /// file had one (DescriptionField)</summary>
    public string Description = "";
    public bool DescriptionField;
    public bool Enabled, Once, Random, ByDifficulty, NodeOff;
    /// <summary>a guid = field was written (the converter writes one always)</summary>
    public bool HasGuid = true;
    public List<EdNode> Conditions = new(), Actions = new();

    public List<EdNode> List(bool conditions) => conditions ? Conditions : Actions;

    public override TriggerItem Copy() => new EdTrigger
    {
        Name = Name, Guid = Guid, Folder = Folder, Expr = Expr, Description = Description, DescriptionField = DescriptionField, Enabled = Enabled,
        Once = Once, Random = Random, ByDifficulty = ByDifficulty, NodeOff = NodeOff, HasGuid = HasGuid, Lead = Lead.ToList(),
        Conditions = Conditions.Select(n => n.Clone()).ToList(), Actions = Actions.Select(n => n.Clone()).ToList(),
    };
    public override string ToString() => Name;
}

public sealed class TriggerScript
{
    /// <summary>the comment lines at the top of the file (without "-- "; "" = an empty line)</summary>
    public List<string> Header = new();
    public List<TriggerItem> Items = new();
    /// <summary>the comments after the last item, verbatim (the converter's list of triggers that were not compiled)</summary>
    public string Footer = "";

    public IEnumerable<EdTrigger> Triggers => Items.OfType<EdTrigger>();
    public EdTrigger? Find(string nameOrGuid) => Triggers.FirstOrDefault(t => t.Guid.Length > 0 && t.Guid == nameOrGuid) ?? Triggers.FirstOrDefault(t => t.Name == nameOrGuid);

    /// <summary>every folder path the triggers use (and "Root"), sorted</summary>
    public List<string> Folders()
    {
        var s = new SortedSet<string>(StringComparer.Ordinal) { "Root" };
        foreach (var t in Triggers)
        {
            var parts = t.Folder.Split('/');
            for (int k = 1; k <= parts.Length; k++) s.Add(string.Join('/', parts.Take(k)));
        }
        return s.ToList();
    }

    public TriggerScript Clone() => new() { Header = Header.ToList(), Items = Items.Select(i => i.Copy()).ToList(), Footer = Footer };

    /// <summary>a file for a new map</summary>
    public static TriggerScript New(string title) => new()
    {
        Header = new List<string>
        {
            $"{title} - the map's triggers, written by the map editor (docs/LUA_MAPS.md has the syntax and every condition / action type).",
            "Own code belongs in custom.lua (the editor never changes it).", "",
        },
    };

    // =========================================================================================== writing
    const int Wrap = 150;
    static readonly string Separator = "-- " + new string('=', 100);

    /// <summary>the file's text (the converter's layout)</summary>
    public string Write()
    {
        var L = new List<string>();
        foreach (var h in Header) L.Add(h.Length == 0 ? "" : "-- " + h);
        string? folder = null;
        foreach (var it in Items)
        {
            if (it is EdTrigger t)
            {
                if (t.Folder != folder)
                {
                    folder = t.Folder;
                    L.Add(Separator);
                    L.Add("-- " + CommentText(folder.Length > 0 ? folder : "Root"));
                    L.Add("");
                }
                foreach (var c in it.Lead) L.Add(c.Length == 0 ? "" : "-- " + c);
                WriteTrigger(t, L);
            }
            else if (it is EdCode code)
            {
                foreach (var c in it.Lead) L.Add(c.Length == 0 ? "" : "-- " + c);
                L.Add(code.Source.TrimEnd());
            }
            L.Add("");
        }
        var s = L.Count > 0 ? string.Join("\n", L) + "\n" : "";
        if (Footer.Length > 0) s += Footer.TrimEnd('\n') + "\n";
        return s;
    }

    /// <summary>a comment's text on one line (white space collapsed: the converter's comment_text)</summary>
    public static string CommentText(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static void WriteTrigger(EdTrigger t, List<string> L)
    {
        L.Add($"trigger {LuaLexer.Quote(t.Name)} {{");
        if (!t.DescriptionField)
            foreach (var line in t.Description.Split('\n'))
                if (line.Trim().Length > 0) L.Add("  -- " + CommentText(line));
        if (t.HasGuid || t.Guid.Length > 0) L.Add($"  guid = {LuaLexer.Quote(t.Guid)},");
        L.Add($"  folder = {LuaLexer.Quote(t.Folder.Length > 0 ? t.Folder : "Root")},");
        if (t.DescriptionField) L.Add($"  description = {LuaLexer.Quote(t.Description)},");
        var flags = new List<string>();
        if (t.Enabled) flags.Add("enabled = true");
        if (t.Once) flags.Add("once = true");
        if (t.Random) flags.Add("random = true");
        if (t.ByDifficulty) flags.Add("by_difficulty = true");
        if (t.NodeOff) flags.Add("node_off = true");
        if (flags.Count > 0) L.Add("  " + string.Join(", ", flags) + ",");
        if (t.Expr.Trim().Length > 0) L.Add($"  expr = {LuaLexer.Quote(t.Expr.Trim())},");
        foreach (var (key, nodes) in new[] { ("conditions", t.Conditions), ("actions", t.Actions) })
        {
            if (nodes.Count == 0) { L.Add($"  {key} = {{}},"); continue; }
            L.Add($"  {key} = {{");
            foreach (var n in nodes) WriteNode(n, "    ", L);
            L.Add("  },");
        }
        L.Add("}");
    }

    /// <summary>one node: its label lines, then the node on one line (with its notes after it) or, when longer than 150
    /// characters, one parameter per line</summary>
    public static void WriteNode(EdNode n, string indent, List<string> L)
    {
        foreach (var l in n.Label) L.Add(indent + "-- " + l);
        string tail = n.Trail != null ? "  -- " + n.Trail : "";
        if (n.IsCode)
        {
            L.Add(indent + n.Source.TrimEnd() + "," + tail);
            return;
        }
        var parts = n.Params.Select(p => $"{LuaLexer.Key(p.Key)} = {p.Value.ToLua()}").ToList();
        string one = parts.Count > 0 ? $"{indent}{n.Ctor}{{ {string.Join(", ", parts)} }}," : $"{indent}{n.Ctor}{{}},";
        if (one.Length <= Wrap) { L.Add(one + tail); return; }
        L.Add($"{indent}{n.Ctor}{{{tail}");
        foreach (var p in parts) L.Add($"{indent}  {p},");
        L.Add($"{indent}}},");
    }

    // =========================================================================================== reading
    /// <summary>reads a triggers.lua; throws LuaSyntaxException for what the lexer finds wrong (a statement it cannot
    /// read as a trigger is kept as an EdCode block, so any valid Lua file can be read)</summary>
    public static TriggerScript Parse(string src) => new Reader(src).Read();

    sealed class Reader
    {
        readonly string _src;
        readonly List<LuaToken> _all;
        // the tokens without comments, and for each its index into _all
        readonly List<LuaToken> _t = new();
        readonly List<int> _ix = new();
        int _p;

        public Reader(string src)
        {
            _src = src.Replace("\r\n", "\n");
            if (_src.StartsWith('﻿')) _src = _src[1..];
            _all = LuaLexer.Tokens(_src);
            for (int i = 0; i < _all.Count; i++) if (_all[i].Kind != LuaTok.Comment) { _t.Add(_all[i]); _ix.Add(i); }
        }

        LuaToken Tok(int k) => _t[Math.Min(k, _t.Count - 1)];
        LuaToken Cur => Tok(_p);

        /// <summary>the comment tokens between non-comment tokens a and b (indices into _t)</summary>
        IEnumerable<LuaToken> CommentsBetween(int a, int b)
        {
            int from = a < 0 ? 0 : _ix[a] + 1, to = _ix[Math.Min(b, _t.Count - 1)];
            for (int i = from; i < to; i++) if (_all[i].Kind == LuaTok.Comment) yield return _all[i];
        }

        static string LineText(LuaToken c) => c.Value.StartsWith(' ') ? c.Value[1..] : c.Value;

        public TriggerScript Read()
        {
            var s = new TriggerScript();
            int prevEnd = -1;              // index into _t of the last token of the previous item
            bool first = true;
            while (Cur.Kind != LuaTok.Eof)
            {
                int start = _p;
                var lead = CommentsBetween(prevEnd, start).ToList();
                var item = ReadTrigger(start) ?? (TriggerItem)ReadCode(start);
                // comments before the item: the header (the first item's, up to an empty line), separators dropped
                var mine = SplitLead(lead, first ? s.Header : null, Tok(start).Line);
                item.Lead = item is EdCode ? new List<string>() : mine.Lead;
                if (item is EdCode code && mine.Lead.Count > 0)
                {
                    // (a code block keeps its own comments in its source: take them in from the first one)
                    var firstC = mine.FirstToken!;
                    code.Source = _src[firstC.Start..Tok(_p - 1).End];
                }
                s.Items.Add(item);
                prevEnd = _p - 1;
                first = false;
            }
            // the comments after the last item: the footer, verbatim
            var tail = CommentsBetween(prevEnd, _t.Count - 1).ToList();
            if (s.Items.Count == 0)
            {
                foreach (var c in tail) s.Header.Add(LineText(c));
                if (s.Header.Count > 0) s.Header.Add("");
            }
            else if (tail.Count > 0) s.Footer = _src[tail[0].Start..tail[^1].End] + "\n";
            return s;
        }

        /// <summary>the comments before an item: for the first item those up to the first empty line go to the header;
        /// a separator ("-- ====" and the folder line after it) is dropped (the writer makes them); the rest is the item's</summary>
        (List<string> Lead, LuaToken? FirstToken) SplitLead(List<LuaToken> cs, List<string>? header, int itemLine)
        {
            var lead = new List<string>();
            LuaToken? firstTok = null;
            int k = 0;
            if (header != null && cs.Count > 0)
            {
                // the header runs until a line that is not directly below the previous comment
                int line = cs[0].Line - 1;
                while (k < cs.Count && cs[k].Line == line + 1 && !IsSeparator(cs[k]) && cs[k].Raw.StartsWith("--", StringComparison.Ordinal) && !cs[k].Raw.StartsWith("--[", StringComparison.Ordinal))
                {
                    header.Add(LineText(cs[k])); line = cs[k].Line; k++;
                }
                // (an empty line after the header is part of it; comments right above the item are not)
                if (k > 0 && (k < cs.Count ? cs[k].Line > line + 1 : itemLine > line + 1)) header.Add("");
                else if (k > 0 && k == cs.Count && itemLine == line + 1) { header.Clear(); k = 0; }
            }
            for (; k < cs.Count; k++)
            {
                if (IsSeparator(cs[k])) { if (k + 1 < cs.Count && cs[k + 1].Line == cs[k].Line + 1) k++; continue; }
                firstTok ??= cs[k];
                lead.Add(LineText(cs[k]));
            }
            return (lead, firstTok);
        }

        static bool IsSeparator(LuaToken c) => c.Value.StartsWith(" =====", StringComparison.Ordinal);

        // ------------------------------------------------------------------ a trigger statement
        /// <summary>`trigger "name" { fields }` (or `trigger("name") { ... }`) whose fields are literals: an EdTrigger;
        /// null (position unchanged) when the statement is something else</summary>
        EdTrigger? ReadTrigger(int start)
        {
            int p = start;
            if (!Tok(p).IsName("trigger")) return null;
            p++;
            bool paren = false;
            if (Tok(p).Is("(")) { paren = true; p++; }
            if (Tok(p).Kind != LuaTok.String) return null;
            var name = Tok(p).Value; p++;
            if (paren) { if (!Tok(p).Is(")")) return null; p++; }
            if (!Tok(p).Is("{")) return null;
            int open = p;
            var t = new EdTrigger { Name = name, HasGuid = false };
            p++;
            // the description: the comments at the top of the table (before the first field)
            var desc = CommentsBetween(open, p).Where(c => !c.Raw.StartsWith("--[", StringComparison.Ordinal)).Select(LineText).ToList();
            bool sawFolder = false;
            while (!Tok(p).Is("}"))
            {
                if (Tok(p).Kind != LuaTok.Name || !Tok(p + 1).Is("=")) return null;
                var key = Tok(p).Raw; p += 2;
                switch (key)
                {
                    case "guid": case "folder": case "expr": case "description":
                        {
                            if (Tok(p).Kind != LuaTok.String) return null;
                            var v = Tok(p).Value; p++;
                            if (key == "guid") { t.Guid = v; t.HasGuid = true; }
                            else if (key == "folder") { t.Folder = v.Length > 0 ? v : "Root"; sawFolder = true; }
                            else if (key == "expr") t.Expr = v;
                            else { t.Description = v; t.DescriptionField = true; }
                            break;
                        }
                    case "enabled": case "once": case "random": case "by_difficulty": case "node_off":
                        {
                            bool v;
                            if (Tok(p).Is("true")) v = true;
                            else if (Tok(p).Is("false")) v = false;
                            else if (Tok(p).Kind == LuaTok.Number && LuaLexer.ParseNumber(Tok(p).Raw) is double d) v = d != 0;
                            else return null;
                            p++;
                            if (key == "enabled") t.Enabled = v; else if (key == "once") t.Once = v; else if (key == "random") t.Random = v;
                            else if (key == "by_difficulty") t.ByDifficulty = v; else t.NodeOff = v;
                            break;
                        }
                    case "conditions": case "actions":
                        {
                            var list = ReadNodes(ref p);
                            if (list == null) return null;
                            if (key == "conditions") t.Conditions = list; else t.Actions = list;
                            break;
                        }
                    default: return null;
                }
                if (Tok(p).Is(",") || Tok(p).Is(";")) p++;
                else if (!Tok(p).Is("}")) return null;
            }
            p++;
            if (!t.DescriptionField) t.Description = string.Join("\n", desc);
            if (!sawFolder) t.Folder = "Root";
            _p = p;
            return t;
        }

        /// <summary>a list of nodes `{ Node, Node, ... }`; null when it is not one</summary>
        List<EdNode>? ReadNodes(ref int p)
        {
            if (!Tok(p).Is("{")) return null;
            int open = p;
            p++;
            var res = new List<EdNode>();
            int prev = open;
            while (!Tok(p).Is("}"))
            {
                if (Tok(p).Kind == LuaTok.Eof) return null;
                int ns = p;
                var n = ReadNode(ref p);
                if (n == null) return null;
                // the label: the comments between the previous node (or the list's brace) and this one, not on the
                // previous node's line
                int prevLine = Tok(prev).Line;
                foreach (var c in CommentsBetween(prev, ns))
                    if (c.Line != prevLine || prev == open) n.Label.Add(LineText(c));
                int nodeEnd = p - 1;
                bool sep = Tok(p).Is(",") || Tok(p).Is(";");
                if (sep) p++;
                else if (!Tok(p).Is("}")) return null;
                // the notes: comments inside the node's text (not inside a function's code) and right after it on its line
                var trail = new List<string>();
                if (!n.IsCode) foreach (var c in CommentsBetween(ns, nodeEnd)) trail.Add(LineText(c));
                int endLine = Tok(p - 1).Line;
                foreach (var c in CommentsBetween(p - 1, p)) if (c.Line == endLine) trail.Add(LineText(c));
                if (trail.Count > 0) n.Trail = string.Join("; ", trail);
                res.Add(n);
                prev = p - 1;
            }
            p++;
            return res;
        }

        /// <summary>one node of a list; null when the list holds something the reader cannot keep apart (a positional
        /// parameter of a constructor)</summary>
        EdNode? ReadNode(ref int p)
        {
            int s = p;
            var t0 = Tok(p);
            // Script(...) / Check(...)
            if ((t0.IsName("Script") || t0.IsName("Check")) && Tok(p + 1).Is("("))
            {
                int e = SkipExpr(p);
                if (e < 0) return null;
                p = e;
                return new EdNode { Form = t0.Raw == "Script" ? NodeForm.Script : NodeForm.Check, Code = t0.Raw == "Script" ? "SCRP" : "CHCK", Source = Text(s, p) };
            }
            // Name{ ... } of a known type, Node("XXXX"){ ... }
            string? ctor = null, code = null;
            int q = p;
            if (t0.Kind == LuaTok.Name && Tok(p + 1).Is("{"))
            {
                var e = TriggerTypes.All.FirstOrDefault(x => x.Name == t0.Raw || x.Code == t0.Raw);
                if (e != null && t0.Raw != "Script" && t0.Raw != "Check") { ctor = t0.Raw; code = e.Code; q = p + 1; }
            }
            else if (t0.IsName("Node") && Tok(p + 1).Is("(") && Tok(p + 2).Kind == LuaTok.String && Tok(p + 3).Is(")") && Tok(p + 4).Is("{"))
            {
                ctor = Text(p, p + 4); code = Tok(p + 2).Value; q = p + 4;
            }
            if (ctor != null)
            {
                var n = new EdNode { Code = code!, Ctor = ctor };
                int r = q + 1;
                bool ok = true;
                while (!Tok(r).Is("}"))
                {
                    string key;
                    if (Tok(r).Kind == LuaTok.Name && Tok(r + 1).Is("=")) { key = Tok(r).Raw; r += 2; }
                    else if (Tok(r).Is("[") && Tok(r + 1).Kind == LuaTok.String && Tok(r + 2).Is("]") && Tok(r + 3).Is("=")) { key = Tok(r + 1).Value; r += 4; }
                    else { ok = false; break; }
                    int vs = r;
                    ParamValue? v = null;
                    var tv = Tok(r);
                    bool End(int k) => Tok(k).Is(",") || Tok(k).Is(";") || Tok(k).Is("}");
                    if (tv.Kind == LuaTok.String && End(r + 1)) { v = new ParamValue(ParamKind.String, tv.Value); r++; }
                    else if (tv.Kind == LuaTok.Number && End(r + 1)) { v = new ParamValue(ParamKind.Number, tv.Raw); r++; }
                    else if (tv.Is("-") && Tok(r + 1).Kind == LuaTok.Number && Tok(r + 1).Start == tv.End && End(r + 2)) { v = new ParamValue(ParamKind.Number, "-" + Tok(r + 1).Raw); r += 2; }
                    else if ((tv.Is("true") || tv.Is("false")) && End(r + 1)) { v = new ParamValue(ParamKind.Bool, tv.Raw); r++; }
                    else
                    {
                        int e = SkipExpr(r);
                        if (e < 0) { ok = false; break; }
                        v = new ParamValue(ParamKind.Expr, Text(vs, e)); r = e;
                    }
                    n.Params.Add(new EdParam(key, v));
                    if (Tok(r).Is(",") || Tok(r).Is(";")) r++;
                    else if (!Tok(r).Is("}")) { ok = false; break; }
                }
                if (ok && (Tok(r + 1).Is(",") || Tok(r + 1).Is(";") || Tok(r + 1).Is("}")))
                {
                    p = r + 1;
                    return n;
                }
            }
            // anything else: the expression as text
            int end = SkipExpr(s);
            if (end < 0 || end == s) return null;
            p = end;
            return new EdNode { Form = NodeForm.Raw, Source = Text(s, end) };
        }

        /// <summary>the source from token a to the end of token b - 1</summary>
        string Text(int a, int b) => _src[Tok(a).Start..Tok(b - 1).End];

        /// <summary>the end (exclusive token index) of an expression from p: up to a ',' ';' or a closing bracket at the
        /// same depth, counting brackets and the blocks of functions; -1 if unbalanced</summary>
        int SkipExpr(int p)
        {
            int depth = 0, blocks = 0;
            for (; ; p++)
            {
                var t = Tok(p);
                if (t.Kind == LuaTok.Eof) return depth == 0 && blocks == 0 ? p : -1;
                if (t.Kind == LuaTok.Keyword)
                {
                    if (t.Raw is "function" or "if" or "do" or "repeat") blocks++;
                    else if (t.Raw is "end" or "until") { if (--blocks < 0) return -1; }
                    continue;
                }
                if (t.Kind != LuaTok.Symbol) continue;
                if (t.Raw is "(" or "{" or "[") { depth++; continue; }
                if (t.Raw is ")" or "}" or "]")
                {
                    if (depth == 0) return blocks == 0 ? p : -1;
                    depth--; continue;
                }
                if (depth == 0 && blocks == 0 && t.Raw is "," or ";") return p;
            }
        }

        // ------------------------------------------------------------------ other statements
        /// <summary>a statement that is not a trigger the editor can express: up to the start of the next statement that
        /// begins `trigger "..."` at the top level (or the end of the file)</summary>
        EdCode ReadCode(int start)
        {
            int p = start, depth = 0, blocks = 0;
            string? trig = Tok(p).IsName("trigger") && Tok(p + 1).Kind == LuaTok.String ? Tok(p + 1).Value
                         : Tok(p).IsName("trigger") && Tok(p + 1).Is("(") && Tok(p + 2).Kind == LuaTok.String ? Tok(p + 2).Value : null;
            p++;
            for (; Tok(p).Kind != LuaTok.Eof; p++)
            {
                var t = Tok(p);
                if (depth == 0 && blocks == 0 && t.IsName("trigger") && (Tok(p + 1).Kind == LuaTok.String || (Tok(p + 1).Is("(") && Tok(p + 2).Kind == LuaTok.String))
                    && !Tok(p - 1).Is(".") && !Tok(p - 1).Is(":") && !Tok(p - 1).Is("=") && !Tok(p - 1).Is(",") && !Tok(p - 1).Is("return")
                    && !(Tok(p - 1).Kind == LuaTok.Keyword && Tok(p - 1).Raw is "and" or "or" or "not" or "local" or "in"))
                    break;
                if (t.Kind == LuaTok.Keyword)
                {
                    if (t.Raw is "function" or "if" or "do" or "repeat") blocks++;
                    else if (t.Raw is "end" or "until") blocks = Math.Max(0, blocks - 1);
                }
                else if (t.Kind == LuaTok.Symbol)
                {
                    if (t.Raw is "(" or "{" or "[") depth++;
                    else if (t.Raw is ")" or "}" or "]") depth = Math.Max(0, depth - 1);
                }
            }
            _p = p;
            var src = Text(start, p);
            var firstLine = src.Split('\n')[0].Trim();
            return new EdCode
            {
                Source = src, TriggerName = trig,
                Title = trig != null ? $"trigger \"{trig}\" (custom script)" : (firstLine.Length > 60 ? firstLine[..57] + "..." : firstLine),
            };
        }
    }
}
