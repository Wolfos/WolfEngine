using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class AnimationEditorAssetHandler(AssetType type, AnimationWindow window, IEditorProjectService project, IProjectAssetPipelineService pipeline) : IEditorAssetHandler
{
    public AssetType AssetType => type;
    public string DisplayName => type switch { AssetType.AnimationGraph => "Animation Graph", AssetType.AnimationSequence => "Animation Clip", AssetType.AnimationSet => "Animation Clip Set", _ => "Bone Mask" };
    public string ThumbnailLabel => "ANIM";
    public string GetSubtitle(AssetDatabaseEntry asset) => DisplayName;
    public void DrawEditor(AssetDatabaseEntry asset) { if (ImGui.Button("Open in Animation")) window.Open(asset); }
    public IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems() => [new() { Label = DisplayName, CreateAction = Create }];
    private EditorAssetCreationResult Create(string folder)
    {
        try
        {
            object asset = type switch
            {
                AssetType.AnimationGraph => RestGraph(), AssetType.AnimationSet => new AnimationSet(),
                AssetType.AnimationSequence => new AnimationSequence(), _ => new BoneMask()
            };
            var extension = type switch { AssetType.AnimationGraph => AnimationGraph.Extension, AssetType.AnimationSet => AnimationSet.Extension, AssetType.AnimationSequence => AnimationSequence.Extension, _ => BoneMask.Extension };
            var path = folder.TrimEnd('/') + "/New " + DisplayName + extension; var suffix = 1;
            while (File.Exists(project.GetAbsolutePath(path))) path = folder.TrimEnd('/') + "/New " + DisplayName + " " + suffix++ + extension;
            AnimationAssetJson.Write(project.GetAbsolutePath(path), asset); project.RefreshAssetSource(path);
            return pipeline.TryGetPrimaryNodeIdForRelativeSourcePath(project.ProjectRootPath!, path, out var id) ? EditorAssetCreationResult.Succeeded(id) : EditorAssetCreationResult.Failed("Animation asset was not indexed.");
        }
        catch (Exception exception) { return EditorAssetCreationResult.Failed(exception.Message); }
    }
    private static AnimationGraph RestGraph()
    {
        var rest = new AnimationNode { Kind = AnimationNodeKind.BindPose, Name = "Bind Pose" };
        var output = new AnimationNode { Kind = AnimationNodeKind.Output, Name = "Output", Inputs = [rest.Id] };
        return new() { Nodes = [rest, output], Output = output.Id, Layout = [new() { NodeId = rest.Id, X = 20, Y = 20 }, new() { NodeId = output.Id, X = 250, Y = 20 }] };
    }
}
