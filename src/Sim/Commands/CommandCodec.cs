// Commands as JSON (saved games, replays, later the network): {"t": "OrderCommand", "c": {fields}}. Every public
// field of the command class is written (System.Text.Json with fields; [JsonIgnore] leaves one out); the class is found
// again by its name among the Command classes of the loaded assemblies (the game's own commands - the HUD's stance and
// unload commands live in the game's assembly - are found as well as src/Sim's). A command class must therefore be
// plain data: ids of entities, numbers, strings, arrays and lists of them (tuples too), no references to world objects.
// Not a JS module (the JS saved its state, not its commands: docs/SAVES.md).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PwRemake.Sim.Commands;

public static class CommandCodec
{
    static readonly JsonSerializerOptions Opts = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    static Dictionary<string, Type>? _types;
    static readonly object _lock = new();

    /// <summary>the Command classes of the loaded assemblies by name (looked up again when a name is missing: an assembly
    /// loaded later)</summary>
    static Type? TypeOf(string name)
    {
        lock (_lock)
        {
            if (_types != null && _types.TryGetValue(name, out var t)) return t;
            _types = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray()!; }
                catch (Exception) { continue; }
                foreach (var x in types)
                    if (x.IsClass && !x.IsAbstract && typeof(Command).IsAssignableFrom(x)) { _types.TryAdd(x.Name, x); _types.TryAdd(x.FullName ?? x.Name, x); }
            }
            return _types.TryGetValue(name, out var t2) ? t2 : null;
        }
    }

    /// <summary>{"t": class name, "c": the command's fields}</summary>
    public static JsonObject Encode(Command c) => new()
    {
        ["t"] = c.GetType().Name,
        ["c"] = JsonSerializer.SerializeToNode(c, c.GetType(), Opts),
    };

    /// <summary>the command of Encode's object; throws for an unknown class (a save of another version)</summary>
    public static Command Decode(JsonNode n)
    {
        var name = n["t"]?.GetValue<string>() ?? throw new FormatException("a command without its class");
        var t = TypeOf(name) ?? throw new FormatException($"unknown command {name}");
        var c = (Command?)n["c"].Deserialize(t, Opts) ?? throw new FormatException($"command {name} without fields");
        return c;
    }
}
