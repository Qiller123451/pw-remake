// Undo / redo of the map editor: every edit is a command (IEditCommand: Do / Undo, and merging for brush strokes - the
// many small steps of one mouse drag become one undo step). EditHistory keeps the done and undone commands and knows
// whether the document differs from its last saved state (the dirty flag: "unsaved changes"). Plain classes, no Godot.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Editor;

/// <summary>an edit of the document that can be undone</summary>
public interface IEditCommand
{
    /// <summary>what the Undo / Redo buttons say ("Raise / lower terrain")</summary>
    string Name { get; }
    void Do(IEditorMapDoc doc);
    void Undo(IEditorMapDoc doc);
    /// <summary>takes the next command (already done) into this one, so both undo as one step; false: they stay two
    /// (a brush stroke merges its steps; a click of another tool does not)</summary>
    bool TryMerge(IEditCommand next);
}

public sealed class EditHistory
{
    static readonly Logger L = Log.For("Editor");
    readonly IEditorMapDoc _doc;
    // each state of the document after a command has a number of its own; the state with nothing done is _base (0, or
    // the number of the last command dropped off the bottom); dirty = the state now is not the saved one
    readonly List<(IEditCommand Cmd, long State)> _undo = new(), _redo = new();
    long _next = 1, _base, _saved;
    bool _sealed = true;
    /// <summary>at most this many undo steps (the oldest go)</summary>
    public int Limit = 200;

    public EditHistory(IEditorMapDoc doc) { _doc = doc; }

    /// <summary>raised after Execute, Undo, Redo, MarkSaved (the buttons and the title follow it)</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Cmd.Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Cmd.Name : null;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    long State => _undo.Count > 0 ? _undo[^1].State : _base;
    /// <summary>the document differs from the last save (or from how it was opened)</summary>
    public bool Dirty => State != _saved;

    /// <summary>does the command and keeps it; with merge, a command that the last one takes (TryMerge) joins it - a
    /// brush stroke's steps - unless EndMerge came between</summary>
    public void Execute(IEditCommand c, bool merge = false)
    {
        c.Do(_doc);
        _redo.Clear();
        if (merge && !_sealed && _undo.Count > 0 && _undo[^1].Cmd.TryMerge(c))
            _undo[^1] = (_undo[^1].Cmd, _next++);           // (a new state: a save in the middle of a stroke is left)
        else
        {
            _undo.Add((c, _next++));
            if (_undo.Count > Limit) { _base = _undo[0].State; _undo.RemoveAt(0); }
        }
        _sealed = !merge;
        Changed?.Invoke();
    }

    /// <summary>the end of a stroke: the next command starts a new undo step</summary>
    public void EndMerge() => _sealed = true;

    public bool Undo()
    {
        _sealed = true;
        if (_undo.Count == 0) return false;
        var top = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        top.Cmd.Undo(_doc);
        _redo.Add(top);
        L.Debug($"undo: {top.Cmd.Name}");
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        _sealed = true;
        if (_redo.Count == 0) return false;
        var top = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        top.Cmd.Do(_doc);
        _undo.Add(top);
        L.Debug($"redo: {top.Cmd.Name}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>the document was saved: this state is clean</summary>
    public void MarkSaved() { _saved = State; Changed?.Invoke(); }

    /// <summary>forgets everything (a new or opened document is clean)</summary>
    public void Clear()
    {
        _undo.Clear(); _redo.Clear();
        _base = _saved = 0; _next = 1; _sealed = true;
        Changed?.Invoke();
    }
}
