// The log in Godot's output panel (editor) and console: warnings as GD.PushWarning, errors as GD.PushError (they
// show in the editor's Debugger > Errors tab with the message), the rest as GD.Print. Lines from other threads are
// queued and printed on the main thread (Godot's print functions are not meant for every thread).
using System.Collections.Concurrent;
using Godot;
using PwRemake.Sim.Core;

namespace PwRemake.App;

public sealed class GodotLogSink : ILogSink
{
	public string Name => "godot";
	readonly int _mainThread = System.Environment.CurrentManagedThreadId;
	readonly ConcurrentQueue<LogEntry> _pending = new();

	public void Write(in LogEntry e)
	{
		if (System.Environment.CurrentManagedThreadId != _mainThread) { _pending.Enqueue(e); return; }
		Print(e);
	}

	/// <summary>Called once per frame by the app: prints what other threads logged.</summary>
	public void Pump()
	{
		while (_pending.TryDequeue(out var e)) Print(e);
	}

	static void Print(in LogEntry e)
	{
		string s = e.Format();
		if (e.Level >= LogLevel.Error) GD.PushError(s);
		else if (e.Level == LogLevel.Warn) GD.PushWarning(s);
		else GD.Print(s);
	}

	public void Dispose() { }
}
