using System.Numerics;
using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor.UI;

public sealed class AnimationWindow : EditorWindow, IDisposable
{
    private readonly IEditorProjectService _project;
    private readonly IProjectAssetPipelineService _pipeline;
    private readonly IAssetSelectionService _selection;
    private readonly IEditorWorkspaceService _workspaces;
    private readonly IRenderViewHost _host;
    private readonly EditorRenderViews _views;
    private readonly EditorViewportStateBus _viewUi;
    private readonly Dictionary<Guid, AnimationDocument> _documents = new();
    private AnimationDocument? _document;
    private Guid _assetId, _selectedNode, _connectFrom, _modelId, _setId;
    private string? _gestureSnapshot;
    private AnimationPreviewScene? _preview;
    private string? _projectPath, _diagnostic;
    private long _compiledRevision = -1;
    private Vector2 _pan = new(20, 20);
    private float _zoom = 1, _scrub;
    private Entity _liveEntity;
    public override string Name => "Animation";
    public AnimationDocument? Document => _document;
    public AnimationGraphInstance? PreviewInstance => _preview?.Instance;
    public string? Diagnostic => _diagnostic;
    public RenderViewId? PreviewView => _preview?.View;
    public AnimationWindow(IEditorProjectService project, IProjectAssetPipelineService pipeline, IAssetSelectionService selection,
        IEditorWorkspaceService workspaces, IRenderViewHost host, EditorRenderViews views, EditorViewportStateBus viewUi)
    { _project = project; _pipeline = pipeline; _selection = selection; _workspaces = workspaces; _host = host; _views = views; _viewUi = viewUi; }
    public void Open(AssetDatabaseEntry asset)
    {
        CheckProject();
        if (AnimationAssetJson.GetAssetType(asset.RelativeSourcePath) is null) return;
        if (!_documents.TryGetValue(asset.Id, out _document))
        {
            _document = new AnimationDocument(_project.GetAbsoluteAssetPath(asset.Id, asset.RelativeSourcePath));
            _documents.Add(asset.Id, _document);
        }
        _assetId = asset.Id;
        _gestureSnapshot = null; _selectedNode = default; _compiledRevision = -1; _diagnostic = null;
        if (_workspaces.Workspaces.Any(w => w.Id == EditorWorkspaceService.AnimationWorkspaceId)) _workspaces.Activate(EditorWorkspaceService.AnimationWorkspaceId);
        _workspaces.OpenWindow(EditorWindowIds.Animation); RequestFocus();
    }
    private void CheckProject()
    {
        if (_projectPath == _project.ProjectRootPath) return;
        DisposePreview(); _documents.Clear(); _document = null; _projectPath = _project.ProjectRootPath;
        _assetId = _modelId = _setId = Guid.Empty; _liveEntity = default;
    }
    public override void OnHidden() { CheckProject(); if (_preview is not null) _viewUi.PublishUiState(_preview.View, SceneViewportUiState.Hidden); }
    public void Dispose() => DisposePreview();
    private void DisposePreview() { _preview?.Dispose(); _preview = null; }
    public override void Draw(EditorScene scene)
    {
        CheckProject(); Begin();
        if (ImGui.IsWindowDocked() && !IsSelectedTab) { OnHidden(); ImGui.End(); return; }
        if (!_project.HasOpenProject) { ImGui.TextUnformatted("Open a project to edit animation."); ImGui.End(); return; }
        if (_selection.SelectedAssetId is { } selected && selected != _assetId && _project.TryGetAsset(selected, out var asset) && AnimationAssetJson.GetAssetType(asset.RelativeSourcePath) is not null)
        {
            try { Open(asset); } catch (Exception exception) { _diagnostic = exception.Message; }
        }
        if (_document is null) { ImGui.TextUnformatted("Open an animation graph from Assets."); DrawLive(scene); ImGui.End(); return; }
        ImGui.TextUnformatted(System.IO.Path.GetFileName(_document.Path) + (_document.Dirty ? " *" : ""));
        var readOnly = _project.IsAssetReadOnly(_assetId);
        ImGui.BeginDisabled(readOnly);
        if (ImGui.Button("Save")) Save(); ImGui.SameLine();
        ImGui.BeginDisabled(!_document.CanUndo); if (ImGui.Button("Undo")) _document.Undo(); ImGui.EndDisabled(); ImGui.SameLine();
        ImGui.BeginDisabled(!_document.CanRedo); if (ImGui.Button("Redo")) _document.Redo(); ImGui.EndDisabled();
        var before = _document.Snapshot();
        var available = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("Authoring", new Vector2(available.X * .58f, available.Y), ImGuiChildFlags.Borders);
        switch (_document.Asset)
        {
            case AnimationGraph graph: DrawGraph(graph); break;
        }
        if (!readOnly)
        {
            if (ImGui.IsAnyItemActive()) _gestureSnapshot ??= before;
            else { _document.Commit(_gestureSnapshot ?? before); _gestureSnapshot = null; }
        }
        ImGui.EndChild();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginChild("Preview and inspection", new Vector2(0, available.Y), ImGuiChildFlags.Borders);
        if (_diagnostic is not null) ImGui.TextWrapped(_diagnostic);
        DrawPreview(); DrawLive(scene);
        ImGui.EndChild();
        ImGui.End();
    }
    public void Save()
    {
        if (_document is null || _project.IsAssetReadOnly(_assetId)) return;
        try { _document.Save(); var source = _project.TryGetAsset(_assetId, out var asset) ? asset.RelativeSourcePath : null; if (source is not null) _project.RefreshAssetSource(source); }
        catch (Exception exception) { _diagnostic = exception.Message; }
    }
    public static bool DataAssetChoice<T>(IEditorProjectService project, string label, ref Guid id) where T : IDataAsset
    {
        var current = project.TryGetAsset(id, out var selected) ? selected.RelativeSourcePath : "None";
        var changed = false;
        if (ImGui.BeginCombo(label, current))
        {
            if (ImGui.Selectable("None", id == Guid.Empty)) { id = Guid.Empty; changed = true; }
            foreach (var asset in project.CurrentAssetDatabase.Assets)
                if (asset.Type == AssetType.DataAsset && asset.TryGetSummary<DataAssetSummary>(out var summary) &&
                    summary.DataAssetType.StartsWith(typeof(T).FullName + ",", StringComparison.Ordinal) &&
                    ImGui.Selectable(asset.RelativeSourcePath + "##" + asset.Id, id == asset.Id))
                { id = asset.Id; changed = true; }
            ImGui.EndCombo();
        }
        return changed;
    }
    public static bool AssetChoice(IEditorProjectService project, string label, AssetType type, ref Guid id)
    {
        var current = project.TryGetAsset(id, out var selected) ? selected.RelativeSourcePath : "None";
        var changed = false;
        if (ImGui.BeginCombo(label, current))
        {
            if (ImGui.Selectable("None", id == Guid.Empty)) { id = Guid.Empty; changed = true; }
            foreach (var asset in project.CurrentAssetDatabase.Assets)
                if (asset.Type == type && ImGui.Selectable(asset.RelativeSourcePath + "##" + asset.Id, id == asset.Id)) { id = asset.Id; changed = true; }
            ImGui.EndCombo();
        }
        return changed;
    }
    private void DrawGraph(AnimationGraph graph)
    {
        var name = graph.Name; if (ImGui.InputText("Graph name", ref name, 128)) graph.Name = name;
        if (ImGui.BeginCombo("Add node", "Choose operation"))
        {
            foreach (var kind in Enum.GetValues<AnimationNodeKind>())
                if (ImGui.Selectable(kind.ToString()))
                {
                    var node = new AnimationNode { Kind = kind, Name = kind.ToString() };
                    var count = kind is AnimationNodeKind.Blend or AnimationNodeKind.MaskedBlend or AnimationNodeKind.Locomotion1D ? 2 :
                        kind is AnimationNodeKind.Output or AnimationNodeKind.CurveRemap or AnimationNodeKind.Select or AnimationNodeKind.StateMachine ? 1 : 0;
                    for (var i = 0; i < count; i++) node.Inputs.Add(Guid.Empty);
                    if (kind == AnimationNodeKind.Locomotion1D) node.Thresholds = [0, 1];
                    graph.Nodes.Add(node); graph.Layout.Add(new() { NodeId = node.Id, X = 40 + graph.Nodes.Count * 20, Y = 40 + graph.Nodes.Count * 15 });
                    if (kind == AnimationNodeKind.Output) graph.Output = node.Id;
                    _selectedNode = node.Id;
                }
            ImGui.EndCombo();
        }
        DrawCanvas(graph);
        var selected = graph.Nodes.FirstOrDefault(n => n.Id == _selectedNode);
        if (selected is not null) DrawNode(graph, selected);
        if (ImGui.CollapsingHeader("Parameters"))
        {
            for (var i = 0; i < graph.Parameters.Count; i++)
            {
                var parameter = graph.Parameters[i]; ImGui.PushID(i);
                var parameterName = parameter.Name; if (ImGui.InputText("Name", ref parameterName, 128)) parameter.Name = parameterName;
                var type = (int)parameter.Type; if (ImGui.Combo("Type", ref type, "Float\0Bool\0Integer\0")) parameter.Type = (AnimationParameterType)type;
                var value = parameter.Default; if (ImGui.InputFloat("Default", ref value)) parameter.Default = value;
                if (ImGui.Button("Remove parameter")) { graph.Parameters.RemoveAt(i--); }
                ImGui.PopID();
            }
            if (ImGui.Button("Add parameter")) graph.Parameters.Add(new() { Name = "Parameter" + graph.Parameters.Count });
        }
    }
    private void DrawCanvas(AnimationGraph graph)
    {
        var size = new Vector2(Math.Max(100, ImGui.GetContentRegionAvail().X), 300);
        ImGui.BeginChild("PoseCanvas", size, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var origin = ImGui.GetCursorScreenPos() + _pan; var draw = ImGui.GetWindowDrawList();
        if (ImGui.IsWindowHovered())
        {
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle)) _pan += ImGui.GetIO().MouseDelta;
            if (ImGui.GetIO().MouseWheel != 0) _zoom = Math.Clamp(_zoom + ImGui.GetIO().MouseWheel * 0.1f, 0.4f, 2);
        }
        foreach (var node in graph.Nodes)
        {
            var position = Position(node.Id);
            for (var i = 0; i < node.Inputs.Count; i++)
                if (graph.Nodes.Any(n => n.Id == node.Inputs[i]))
                {
                    var from = Position(node.Inputs[i]) + new Vector2(160, 24) * _zoom;
                    var to = position + new Vector2(0, 50 + i * 22) * _zoom;
                    draw.AddBezierCubic(from, from + new Vector2(55, 0), to - new Vector2(55, 0), to, 0xFF60C0E8, 2);
                }
        }
        foreach (var node in graph.Nodes)
        {
            var position = Position(node.Id); var height = Math.Max(80, 62 + node.Inputs.Count * 22);
            draw.AddRectFilled(position, position + new Vector2(160, height) * _zoom, node.Id == _selectedNode ? 0xFF5B5140 : 0xFF353535, 5);
            draw.AddText(position + new Vector2(8, 6), 0xFFFFFFFF, node.Name.Length == 0 ? node.Kind.ToString() : node.Name);
            ImGui.SetCursorScreenPos(position); ImGui.PushID(node.Id.ToString());
            ImGui.InvisibleButton("Move", new Vector2(140, 25) * _zoom);
            if (ImGui.IsItemClicked()) _selectedNode = node.Id;
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                var layout = graph.Layout.FirstOrDefault(p => p.NodeId == node.Id);
                if (layout is null) { layout = new() { NodeId = node.Id }; graph.Layout.Add(layout); }
                layout.X += ImGui.GetIO().MouseDelta.X / _zoom; layout.Y += ImGui.GetIO().MouseDelta.Y / _zoom;
            }
            ImGui.SetCursorScreenPos(position + new Vector2(145, 18) * _zoom);
            if (ImGui.SmallButton("o")) _connectFrom = node.Id;
            for (var i = 0; i < node.Inputs.Count; i++)
            {
                ImGui.SetCursorScreenPos(position + new Vector2(3, 45 + i * 22) * _zoom);
                if (ImGui.SmallButton("Pose " + i) && _connectFrom != Guid.Empty && _connectFrom != node.Id) { node.Inputs[i] = _connectFrom; _connectFrom = Guid.Empty; }
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
        Vector2 Position(Guid id)
        {
            var layout = graph.Layout.FirstOrDefault(p => p.NodeId == id);
            return origin + new Vector2(layout?.X ?? 20, layout?.Y ?? 20) * _zoom;
        }
    }
    private void DrawNode(AnimationGraph graph, AnimationNode node)
    {
        ImGui.PushID(node.Id.ToString()); ImGui.SeparatorText(node.Kind.ToString());
        var name = node.Name; if (ImGui.InputText("Node name", ref name, 128)) node.Name = name;
        var parameter = node.Parameter; ParameterChoice(graph, "Parameter", ref parameter); node.Parameter = parameter;
        var value = node.Value; if (ImGui.InputFloat("Value", ref value)) node.Value = value;
        if (node.Kind == AnimationNodeKind.Clip)
        {
            var slot = node.ClipSlot; if (ImGui.InputText("Clip slot", ref slot, 128)) node.ClipSlot = slot;
            var loop = node.Loop; if (ImGui.Checkbox("Loop", ref loop)) node.Loop = loop;
            var speed = node.Speed; if (ImGui.InputFloat("Playback speed", ref speed)) node.Speed = speed;
            var start = node.StartTime; if (ImGui.InputFloat("Start time", ref start)) node.StartTime = start;
            var time = node.TimeParameter; ParameterChoice(graph, "External normalized time", ref time); node.TimeParameter = time;
            var sequence = node.SequenceParameter; ParameterChoice(graph, "Action sequence", ref sequence); node.SequenceParameter = sequence;
        }
        var restart = node.RestartOnActivation; if (ImGui.Checkbox("Restart when activated", ref restart)) node.RestartOnActivation = restart;
        if (node.Kind == AnimationNodeKind.MaskedBlend) { var mask = node.MaskId; DataAssetChoice<BoneMask>(_project, "Bone mask", ref mask); node.MaskId = mask; }
        for (var i = 0; i < node.Inputs.Count; i++)
        {
            ImGui.PushID(i); var input = node.Inputs[i];
            if (ImGui.BeginCombo("Pose input " + i, graph.Nodes.FirstOrDefault(n => n.Id == input)?.Name ?? "Unconnected"))
            {
                foreach (var source in graph.Nodes) if (source.Id != node.Id && ImGui.Selectable(source.Name + "##" + source.Id)) node.Inputs[i] = source.Id;
                ImGui.EndCombo();
            }
            if (node.Kind == AnimationNodeKind.StateMachine && ImGui.SmallButton("Inspect state subgraph")) _selectedNode = node.Inputs[i];
            if (node.Kind == AnimationNodeKind.Locomotion1D)
            { while (node.Thresholds.Count <= i) node.Thresholds.Add(i); var threshold = node.Thresholds[i]; if (ImGui.InputFloat("Speed threshold", ref threshold)) node.Thresholds[i] = threshold; }
            ImGui.PopID();
        }
        if (node.Kind is AnimationNodeKind.Select or AnimationNodeKind.StateMachine or AnimationNodeKind.Locomotion1D)
        {
            if (ImGui.Button("Add pose input")) node.Inputs.Add(Guid.Empty);
            ImGui.SameLine(); if (node.Inputs.Count > 1 && ImGui.Button("Remove last input")) { node.Inputs.RemoveAt(node.Inputs.Count - 1); if (node.Thresholds.Count > node.Inputs.Count) node.Thresholds.RemoveAt(node.Thresholds.Count - 1); }
        }
        if (node.Kind == AnimationNodeKind.StateMachine) DrawTransitions(graph, node);
        if (node.Kind == AnimationNodeKind.CurveRemap)
        {
            var curve = node.Curve; if (ImGui.InputText("Curve name", ref curve, 128)) node.Curve = curve;
            var scale = node.CurveScale; if (ImGui.InputFloat("Scale", ref scale)) node.CurveScale = scale;
            var offset = node.CurveOffset; if (ImGui.InputFloat("Offset", ref offset)) node.CurveOffset = offset;
        }
        if (node.Kind == AnimationNodeKind.Output && ImGui.Button("Use as graph output")) graph.Output = node.Id;
        if (ImGui.Button("Delete node"))
        { graph.Nodes.Remove(node); graph.Layout.RemoveAll(p => p.NodeId == node.Id); foreach (var other in graph.Nodes) for (var i = 0; i < other.Inputs.Count; i++) if (other.Inputs[i] == node.Id) other.Inputs[i] = Guid.Empty; }
        ImGui.PopID();
    }
    private static void ParameterChoice(AnimationGraph graph, string label, ref string name)
    {
        if (!ImGui.BeginCombo(label, name.Length == 0 ? "Constant / internal" : name)) return;
        if (ImGui.Selectable("Constant / internal")) name = "";
        foreach (var parameter in graph.Parameters) if (ImGui.Selectable(parameter.Name, parameter.Name == name)) name = parameter.Name;
        ImGui.EndCombo();
    }
    private static void DrawTransitions(AnimationGraph graph, AnimationNode node)
    {
        for (var i = 0; i < node.Transitions.Count; i++)
        {
            var transition = node.Transitions[i]; ImGui.PushID("transition" + i);
            ImGui.TextUnformatted("Transition " + i + " (first match wins)");
            var from = transition.From; if (ImGui.InputInt("From (-1 = any)", ref from)) transition.From = from;
            var to = transition.To; if (ImGui.InputInt("To state index", ref to)) transition.To = to;
            var duration = transition.Duration; if (ImGui.InputFloat("Blend duration", ref duration)) transition.Duration = duration;
            var exit = transition.ExitTime; if (ImGui.InputFloat("Exit progress (-1 = none)", ref exit)) transition.ExitTime = exit;
            for (var j = 0; j < transition.Conditions.Count; j++)
            {
                var condition = transition.Conditions[j]; ImGui.PushID(j);
                var parameter = condition.Parameter; ParameterChoice(graph, "Condition parameter", ref parameter); condition.Parameter = parameter;
                var comparison = (int)condition.Comparison; if (ImGui.Combo("Comparison", ref comparison, "Equal\0NotEqual\0Less\0LessOrEqual\0Greater\0GreaterOrEqual\0")) condition.Comparison = (AnimationComparison)comparison;
                var value = condition.Value; if (ImGui.InputFloat("Compare value", ref value)) condition.Value = value;
                if (ImGui.Button("Remove condition")) transition.Conditions.RemoveAt(j--);
                ImGui.PopID();
            }
            if (ImGui.Button("Add condition")) transition.Conditions.Add(new());
            if (ImGui.Button("Remove transition")) node.Transitions.RemoveAt(i--);
            ImGui.PopID();
        }
        if (ImGui.Button("Add transition")) node.Transitions.Add(new());
    }
    private void DrawPreview()
    {
        var model = _modelId; var set = _setId;
        var changed = false;
        var current = _project.TryGetAsset(model, out var modelAsset) ? modelAsset.RelativeSourcePath : "None";
        if (ImGui.BeginCombo("Preview model / prefab", current))
        {
            if (ImGui.Selectable("None", model == Guid.Empty)) { model = Guid.Empty; changed = true; }
            foreach (var asset in _project.CurrentAssetDatabase.Assets)
                if (asset.Type is AssetType.Model3D or AssetType.Prefab && ImGui.Selectable(asset.RelativeSourcePath + "##" + asset.Id, model == asset.Id)) { model = asset.Id; changed = true; }
            ImGui.EndCombo();
        }
        changed |= DataAssetChoice<AnimationSet>(_project, "Preview clip set", ref set);
        _modelId = model; _setId = set;
        if (changed || _document?.Revision != _compiledRevision) TryCompilePreview();
        if (_preview is null) return;
        var instance = _preview.Instance;
        var playing = instance.Playing; if (ImGui.Checkbox("Play preview", ref playing)) instance.Playing = playing;
        ImGui.SameLine(); if (ImGui.Button("Step frame")) _preview.Step(1f / 30);
        var speed = instance.Speed; if (ImGui.SliderFloat("Speed", ref speed, 0, 3)) instance.Speed = speed;
        _scrub = instance.Time; if (ImGui.SliderFloat("Scrub seconds", ref _scrub, 0, 10)) { instance.Playing = false; _preview.Seek(_scrub); }
        var size = new Vector2(Math.Min(512, ImGui.GetContentRegionAvail().X), 300);
        if (size.X <= 0) return;
        var minimum = ImGui.GetCursorScreenPos(); ImGui.Image(UiTextureIds.Viewport(_preview.View), size);
        var scale = ImGui.GetIO().DisplayFramebufferScale;
        _viewUi.PublishUiState(_preview.View, new SceneViewportUiState(!ImGui.IsWindowCollapsed() && ImGui.IsItemVisible(),
            new Int2((int)(size.X * scale.X), (int)(size.Y * scale.Y)), 1, SceneDebugViewIds.FinalColor,
            ImGui.IsItemHovered(), ImGui.IsWindowFocused(), false, false, false, minimum, minimum + size));
        if (ImGui.CollapsingHeader("Parameters and evaluation")) DrawInstance(instance, false);
    }
    public bool TryCompilePreview()
    {
        _compiledRevision = _document?.Revision ?? -1;
        if (_modelId == Guid.Empty || _document is null) return false;
        try
        {
            var graph = (AnimationGraph)_document.Asset;
            var clips = _setId == Guid.Empty ? null : AssetDatabase.GetInstance<AnimationSet>(_setId);
            object? Resolve(Guid id, Type type) => id == _assetId && type == typeof(AnimationGraph) ? graph : Animator.Resolve(id, type);
            var preview = new AnimationPreviewScene(_host, _views, _viewUi, _pipeline, _project, _modelId, graph, clips, Resolve);
            var previous = _preview?.Instance;
            if (previous is not null)
            {
                foreach (var parameter in preview.Instance.Program.ParameterSchema)
                {
                    var oldParameter = previous.Program.ParameterSchema.FirstOrDefault(p => p.Name == parameter.Name && p.Type == parameter.Type);
                    if (oldParameter is null) continue;
                    var oldHandle = previous.Program.GetParameter(parameter.Name, parameter.Type);
                    var handle = preview.Instance.Program.GetParameter(parameter.Name, parameter.Type);
                    if (parameter.Type == AnimationParameterType.Integer) preview.Instance.SetInteger(handle, previous.GetInteger(oldHandle));
                    else if (parameter.Type == AnimationParameterType.Bool) preview.Instance.SetBool(handle, previous.GetParameter(oldHandle) != 0);
                    else preview.Instance.SetFloat(handle, previous.GetParameter(oldHandle));
                }
                preview.Instance.Speed = previous.Speed;
                preview.Seek(previous.Time);
                preview.Instance.Playing = previous.Playing;
            }
            DisposePreview(); _preview = preview; _diagnostic = null; return true;
        }
        catch (Exception exception) { _diagnostic = exception.Message; return false; }
    }
    public void ConfigurePreview(Guid assetId, Guid modelId, Guid setId)
    {
        if (!_project.TryGetAsset(assetId, out var asset)) throw new InvalidOperationException("Animation asset is missing.");
        Open(asset); _modelId = modelId; _setId = setId;
        if (!TryCompilePreview()) throw new InvalidOperationException(_diagnostic ?? "Preview could not compile.");
    }
    public void EditGraph(string operation, Guid nodeId, int inputIndex, Guid inputId)
    {
        if (_document?.Asset is not AnimationGraph graph) throw new InvalidOperationException("Open an animation graph first.");
        if (_project.IsAssetReadOnly(_assetId)) throw new InvalidOperationException("Asset is read-only.");
        if (operation == "undo") _document.Undo();
        else if (operation == "redo") _document.Redo();
        else if (operation == "connect")
        {
            var node = graph.Nodes.Single(n => n.Id == nodeId);
            if (inputIndex < 0 || inputIndex >= node.Inputs.Count) throw new ArgumentOutOfRangeException(nameof(inputIndex));
            _document.Edit(() => node.Inputs[inputIndex] = inputId);
        }
        else throw new ArgumentException("Expected connect, undo or redo.", nameof(operation));
        TryCompilePreview();
    }
    public void PreviewTransport(bool playing, float? seek, float? step)
    {
        if (_preview is null) throw new InvalidOperationException("No animation preview is open.");
        _preview.Instance.Playing = playing;
        if (seek is { } time) _preview.Seek(time);
        if (step is { } delta) _preview.Step(delta);
    }
    private void DrawLive(EditorScene scene)
    {
        if (!ImGui.CollapsingHeader("Inspect scene animator")) return;
        if (ImGui.BeginCombo("Animator", _liveEntity.IsValid && scene.World.HasComponent<NameComponent>(_liveEntity) ? scene.World.GetComponent<NameComponent>(_liveEntity).Name : "None"))
        {
            foreach (var entry in scene.World.View<Animator>())
            {
                var name = scene.World.HasComponent<NameComponent>(entry.Entity) ? scene.World.GetComponent<NameComponent>(entry.Entity).Name : entry.Entity.ToString();
                if (ImGui.Selectable(name)) _liveEntity = entry.Entity;
            }
            ImGui.EndCombo();
        }
        if (_liveEntity.IsValid && scene.World.HasComponent<Animator>(_liveEntity))
        {
            ref var animator = ref scene.World.GetComponent<Animator>(_liveEntity);
            if (animator.Diagnostic is not null) ImGui.TextWrapped(animator.Diagnostic);
            if (animator.GraphInstance is { } instance) DrawInstance(instance, true);
        }
    }
    private static void DrawInstance(AnimationGraphInstance instance, bool readOnly)
    {
        ImGui.PushID(readOnly ? "Live animator parameters" : "Preview parameters");
        ImGui.BeginDisabled(readOnly);
        foreach (var parameter in instance.Program.ParameterSchema)
        {
            var handle = instance.Program.GetParameter(parameter.Name, parameter.Type); var value = instance.GetParameter(handle);
            switch (parameter.Type)
            {
                case AnimationParameterType.Float: if (ImGui.InputFloat(parameter.Name, ref value)) instance.SetFloat(handle, value); break;
                case AnimationParameterType.Bool: var boolean = value != 0; if (ImGui.Checkbox(parameter.Name, ref boolean)) instance.SetBool(handle, boolean); break;
                case AnimationParameterType.Integer: var integer = (int)value; if (ImGui.InputInt(parameter.Name, ref integer)) instance.SetInteger(handle, integer); break;
            }
        }
        ImGui.EndDisabled();
        foreach (var contribution in instance.Contributions) ImGui.TextUnformatted($"{contribution.ClipSlot}: weight {contribution.Weight:F2}, phase {contribution.NormalizedTime:F2}");
        foreach (var state in instance.States) ImGui.TextUnformatted($"State {state.State}, blend {state.BlendRemaining:F2}s, reason: {state.Reason}");
        foreach (var curve in instance.Program.Curves) ImGui.TextUnformatted($"{curve}: {instance.GetCurve(instance.Program.GetCurve(curve)):F3}");
        foreach (var marker in instance.Markers) ImGui.TextUnformatted($"Marker: {marker.Name} @ {marker.ClipTime:F2}s");
        ImGui.PopID();
    }
}
