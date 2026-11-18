// A small test runner: every public static method `void Name(TestContext t)` of a class marked [TestClass] in this
// assembly is a test. `dotnet run --project tests/PwRemake.Tests [name filter]`. Test data (the toolkit's built game
// data of an installation) comes from the environment variable PW_DATA (a folder with gamedata.json ...) and
// PW_CONTENT (a content folder with configs/); tests that need it are skipped without it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using PwRemake.Sim.Core;

namespace PwRemake.Tests;

[AttributeUsage(AttributeTargets.Class)] public sealed class TestClassAttribute : Attribute { }

public sealed class SkipException : Exception { public SkipException(string m) : base(m) { } }

public sealed class TestContext
{
    public string Name { get; init; } = "";
    public int Checks;
    public void True(bool ok, string what) { Checks++; if (!ok) throw new Exception("FAILED: " + what); }
    public void Equal<T>(T expected, T actual, string what)
    {
        Checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"FAILED: {what}: expected {expected}, got {actual}");
    }
    public void Near(double expected, double actual, double tol, string what)
    {
        Checks++;
        if (Math.Abs(expected - actual) > tol) throw new Exception($"FAILED: {what}: expected {expected} ± {tol}, got {actual}");
    }
    public void Skip(string why) => throw new SkipException(why);
    public string Data(string var = "PW_DATA")
    {
        var d = Environment.GetEnvironmentVariable(var);
        if (string.IsNullOrEmpty(d) || !System.IO.Directory.Exists(d)) Skip($"{var} not set");
        return d!;
    }
    public void Log(string s) => Console.WriteLine("    " + s);
}

public static class Program
{
    public static int Main(string[] args)
    {
        PwRemake.Sim.Core.SimCulture.Use();          // (numbers as text the same on every machine: docs/MULTIPLAYER.md)
        Log.AddSink(new ConsoleLogSink());
        Log.DefaultLevel = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("PW_LOG"), true, out var lv) ? lv : LogLevel.Warn;   // PW_LOG=Info: more of the log
        string filter = args.Length > 0 ? args[0] : "";
        var tests = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t.GetCustomAttribute<TestClassAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(TestContext))
                .Select(m => (name: t.Name + "." + m.Name, m)))
            .Where(x => x.name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.name).ToList();
        int pass = 0, fail = 0, skip = 0;
        foreach (var (name, m) in tests)
        {
            var ctx = new TestContext { Name = name };
            var sw = Stopwatch.StartNew();
            try
            {
                m.Invoke(null, new object[] { ctx });
                pass++;
                Console.WriteLine($"ok    {name} ({ctx.Checks} checks, {sw.ElapsedMilliseconds} ms)");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is SkipException s)
            {
                skip++; Console.WriteLine($"skip  {name}: {s.Message}");
            }
            catch (TargetInvocationException tie)
            {
                fail++; Console.WriteLine($"FAIL  {name}: {tie.InnerException}");
            }
        }
        Console.WriteLine($"{pass} passed, {fail} failed, {skip} skipped");
        return fail == 0 ? 0 : 1;
    }
}
