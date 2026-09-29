using System.Text.Json;
using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

/// <summary>Source document transactions; preview compilation is independent from editable data.</summary>
public sealed class AnimationDocument
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly Type _type;
    private string _saved;
    public object Asset { get; private set; }
    public string Path { get; }
    public long Revision { get; private set; }
    public bool Dirty => Snapshot() != _saved;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public AnimationDocument(string path)
    {
        Path = path; _type = AnimationAssetJson.GetAssetType(path) ?? throw new ArgumentException("Unsupported animation document.");
        Asset = AnimationAssetJson.Read(path, _type); _saved = Snapshot();
    }
    public string Snapshot() => JsonSerializer.Serialize(Asset, _type, AnimationAssetJson.Options);
    public void Commit(string before)
    {
        if (before == Snapshot()) return;
        _undo.Push(before); _redo.Clear(); Revision++;
    }
    public void Edit(Action action) { var before = Snapshot(); action(); Commit(before); }
    public void Undo() { if (_undo.TryPop(out var state)) { _redo.Push(Snapshot()); Restore(state); } }
    public void Redo() { if (_redo.TryPop(out var state)) { _undo.Push(Snapshot()); Restore(state); } }
    private void Restore(string state) { Asset = JsonSerializer.Deserialize(state, _type, AnimationAssetJson.Options)!; Revision++; }
    public void Save() { AnimationAssetJson.Write(Path, Asset); _saved = Snapshot(); }
}
