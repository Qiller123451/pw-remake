// The Lua host of a map: one sandboxed MoonSharp interpreter per mission (docs/LUA_MAPS.md "Sandbox").
//
//   * only safe core modules: basic functions, tables, strings, math (with the world's deterministic random numbers),
//     bit32, metatables, pcall / error. No io, os, debug, dynamic, coroutines, no load / loadfile / dofile / loadstring
//     (and so no binary chunks): a map downloaded with a multiplayer game cannot touch the computer it runs on;
//   * `require "name"` loads name.lua from game/lua/std, else from the mission folder - nothing else (no "..", no
//     absolute paths); every file is run once;
//   * an instruction limit per call from the game into Lua (Run, Script actions, Check conditions): a runaway loop
//     ends with an error, which is logged with file:line, and the game goes on;
//   * print() goes to the log (category Lua).
// A Lua error never reaches the game loop: the call returns nil / false and the error is logged and kept in Errors.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.Interpreter.Loaders;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Scripting;

public sealed class LuaHost
{
    static readonly Logger L = Log.For("Lua");
    /// <summary>Instructions one call from the game may run (the loading of a file gets ten times as many).</summary>
    public long InstructionLimit = 2_000_000;
    public readonly Script Lua;
    public readonly string StdDir;
    public readonly string? MissionDir;
    /// <summary>Lua errors: "file:line: message".</summary>
    public readonly List<string> Errors = new();
    /// <summary>Random numbers for math.random (the world's generator; null = a fixed-seed generator of our own).</summary>
    public Func<double>? Random;
    readonly Budget budget = new();
    readonly Dictionary<string, DynValue> loaded = new(StringComparer.Ordinal);
    readonly SimRandom fallbackRng = new(1);
    int depth;

    public const CoreModules SafeModules = CoreModules.GlobalConsts | CoreModules.TableIterators | CoreModules.Metatables | CoreModules.String |
                                           CoreModules.Table | CoreModules.Basic | CoreModules.Math | CoreModules.Bit32 | CoreModules.ErrorHandling;

    /// <param name="stdDir">game/lua/std (the standard library every map gets).</param>
    /// <param name="missionDir">The mission folder (require looks here first), or null.</param>
    public LuaHost(string stdDir, string? missionDir)
    {
        StdDir = Path.GetFullPath(stdDir);
        MissionDir = missionDir == null ? null : Path.GetFullPath(missionDir);
        Lua = new Script(SafeModules);
        Lua.Options.ScriptLoader = new NoFileLoader();
        Lua.Options.UseLuaErrorLocations = true;
        Lua.Options.DebugPrint = s => L.Info(s);
        Lua.AttachDebugger(budget);
        var G = Lua.Globals;
        // what the safe modules still contain but a map must not have
        foreach (var k in new[] { "collectgarbage", "load", "loadsafe", "loadfile", "loadfilesafe", "dofile", "loadstring", "require", "__require_clr_impl", "getfenv", "setfenv" }) G[k] = DynValue.Nil;
        if (G.Get("string").Table is { } str)
        {
            str["dump"] = DynValue.Nil;
            var rep = str.Get("rep");
            // string.rep is the one way to make a huge string in one instruction
            str["rep"] = DynValue.NewCallback((ctx, args) =>
            {
                double n = args.Count > 1 && args[1].Type == DataType.Number ? args[1].Number : 0;
                long len = (args.Count > 0 ? args[0].CastToString()?.Length ?? 0 : 0) * (long)Math.Max(0, n);
                if (len > 4_000_000) throw new ScriptRuntimeException("string.rep: result too long");
                return Lua.Call(rep, args.GetArray());
            });
        }
        if (G.Get("math").Table is { } math)
        {
            // math.random from the world's generator (determinism: docs/ARCHITECTURE.md)
            math["random"] = DynValue.NewCallback((ctx, args) =>
            {
                double r = Random != null ? Random() : fallbackRng.Next();
                if (args.Count == 0) return DynValue.NewNumber(r);
                double lo = 1, hi = args[0].Number;
                if (args.Count > 1) { lo = args[0].Number; hi = args[1].Number; }
                if (hi < lo) throw new ScriptRuntimeException("math.random: interval is empty");
                return DynValue.NewNumber(Math.Floor(lo + r * (Math.Floor(hi) - lo + 1)));
            });
            math["randomseed"] = DynValue.NewCallback((ctx, args) => DynValue.Nil);
        }
        G["require"] = DynValue.NewCallback((ctx, args) => Require(args.Count > 0 ? args[0].CastToString() : null));
    }

    // ------------------------------------------------------------------------------------------- files
    static readonly Regex ModName = new(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*$");
    /// <summary>require: name.lua (dots = sub folders) from game/lua/std, else from the mission folder (a map cannot
    /// replace a module of the standard library; its own triggers.lua is not a module).</summary>
    public DynValue Require(string? name)
    {
        if (name == null || !ModName.IsMatch(name)) throw new ScriptRuntimeException($"require: bad module name \"{name}\" (letters, digits, _ - and dots only)");
        if (loaded.TryGetValue(name, out var v)) return v;
        var rel = name.Replace('.', '/') + ".lua";
        string? file = null;
        foreach (var dir in new[] { StdDir, MissionDir })
        {
            if (dir == null) continue;
            var f = Path.GetFullPath(Path.Combine(dir, rel));
            if (f.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(f)) { file = f; break; }
        }
        if (file == null) throw new ScriptRuntimeException($"require: module \"{name}\" not found in the mission folder or the standard library");
        loaded[name] = DynValue.True;               // (a module that requires itself)
        var r = Lua.DoString(File.ReadAllText(file), null, ChunkName(file));
        var res = r.IsNil() ? DynValue.True : r;
        loaded[name] = res;
        return res;
    }

    string ChunkName(string file)
    {
        if (MissionDir != null && file.StartsWith(MissionDir, StringComparison.Ordinal)) return Path.GetRelativePath(MissionDir, file).Replace('\\', '/');
        return "std/" + Path.GetRelativePath(StdDir, file).Replace('\\', '/');
    }

    /// <summary>Runs a file of the mission folder (or any path) as a chunk, guarded. False on an error (logged).</summary>
    public bool RunFile(string path)
    {
        var full = Path.GetFullPath(path);
        return Guarded(() => Lua.DoString(File.ReadAllText(full), null, ChunkName(full)), "load " + Path.GetFileName(full), 10) != null;
    }
    /// <summary>Runs Lua source, guarded.</summary>
    public DynValue? RunString(string code, string chunkName) => Guarded(() => Lua.DoString(code, null, chunkName), chunkName, 1);

    // ------------------------------------------------------------------------------------------- calls from the game
    /// <summary>Calls a Lua function (a MoonSharp Closure / DynValue) with arguments; null on an error (logged).</summary>
    public DynValue? Call(object fn, string what, params object?[] args)
    {
        var f = fn as DynValue ?? (fn is Closure c ? DynValue.NewClosure(c) : null);
        if (f == null) return null;
        return Guarded(() => Lua.Call(f, args), what, 1);
    }
    /// <summary>Script action: fn(api).</summary>
    public void CallAction(object fn, string what) => Call(fn, what, Api);
    /// <summary>Check condition: fn(api) is true (Lua truthiness); an error is false.</summary>
    public bool CallCheck(object fn, string what) => Call(fn, what, Api) is { } r && r.CastToBool();
    /// <summary>The table Script / Check functions get (GameApi); set when the engine is bound.</summary>
    public Table? Api;

    DynValue? Guarded(Func<DynValue> run, string what, int limitFactor)
    {
        bool outer = depth == 0;
        if (outer) budget.Start(InstructionLimit * limitFactor);
        depth++;
        try { return run(); }
        catch (InterpreterException e)
        {
            var msg = e.DecoratedMessage ?? e.Message;
            if (budget.Exceeded) msg = $"{msg} (more than {budget.Limit} instructions: endless loop?)";
            Fail(what, msg);
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(what, e.Message);
            return null;
        }
        finally
        {
            depth--;
            if (depth == 0) budget.Stop();
        }
    }

    void Fail(string what, string msg)
    {
        var s = $"{what}: {msg}";
        if (Errors.Count < 500) Errors.Add(s);
        L.Error(s);
    }

    /// <summary>Converts a Lua value of a trigger parameter to the data's string (numbers as the JS String(n)).</summary>
    public static string? ParamString(DynValue v) => v.Type switch
    {
        DataType.String => v.String,
        DataType.Number => TrigUtil.NumStr(v.Number),
        DataType.Boolean => v.Boolean ? "1" : "0",
        _ => null,
    };

    // ------------------------------------------------------------------------------------------- the instruction budget
    // MoonSharp has no instruction limit of its own; an attached "debugger" is asked IsPauseRequested() before every
    // instruction, also in nested calls (pcall, callbacks). Over the budget every further instruction raises a Lua
    // error, so a pcall inside the loop cannot catch its way out: the whole call unwinds.
    sealed class Budget : IDebugger
    {
        public long Limit, Used;
        public bool Active, Exceeded;
        public void Start(long limit) { Limit = limit; Used = 0; Active = true; Exceeded = false; }
        public void Stop() { Active = false; }
        public bool IsPauseRequested()
        {
            if (!Active) return false;
            if (++Used > Limit) { Exceeded = true; throw new ScriptRuntimeException("instruction limit reached"); }
            return false;
        }
        static readonly DebuggerAction RunAction = new() { Action = DebuggerAction.ActionType.Run };
        public DebuggerAction GetAction(int ip, SourceRef sourceref) => RunAction;
        public DebuggerCaps GetDebuggerCaps() => 0;
        public void SetDebugService(DebugService debugService) { }
        public void SetSourceCode(SourceCode sourceCode) { }
        public void SetByteCode(string[] byteCode) { }
        public bool SignalRuntimeException(ScriptRuntimeException ex) => false;
        public void SignalExecutionEnded() { }
        public void Update(WatchType watchType, IEnumerable<WatchItem> items, int stackFrameIndex) { }
        public List<DynamicExpression> GetWatchItems() => new();
        public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
    }

    /// <summary>MoonSharp's own file loading is off: only Require reads files.</summary>
    sealed class NoFileLoader : ScriptLoaderBase
    {
        public override object LoadFile(string file, Table globalContext) => throw new ScriptRuntimeException("file loading is not available in maps");
        public override bool ScriptFileExists(string name) => false;
    }
}
