using System.Text.Json;
using System.Text.Json.Nodes;
using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationAuthoringTests
{
    private string _root = "";
    [SetUp] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "animation-authoring-" + Guid.NewGuid()); Directory.CreateDirectory(Path.Combine(_root,"Assets")); }
    [TearDown] public void Cleanup() { Directory.Delete(_root,true); }
    [Test] public void Document_UndoRedoSaveReloadPreservesNodeIdentityAndLayout()
    {
        var node = new AnimationNode { Kind=AnimationNodeKind.BindPose };
        var path=Path.Combine(_root,"Assets","test.animgraph.json"); AnimationAssetJson.Write(path,new AnimationGraph {Nodes=[node],Layout=[new(){NodeId=node.Id,X=20,Y=30}]});
        var document=new AnimationDocument(path);document.Edit(()=>((AnimationGraph)document.Asset).Nodes[0].Name="Changed");
        Assert.That(document.Dirty,Is.True);document.Undo();Assert.That(((AnimationGraph)document.Asset).Nodes[0].Name,Is.Empty);document.Redo();document.Save();
        var restored=(AnimationGraph)new AnimationDocument(path).Asset;
        Assert.That(restored.Nodes[0].Id,Is.EqualTo(node.Id));Assert.That(restored.Nodes[0].Name,Is.EqualTo("Changed"));Assert.That(restored.Layout[0].X,Is.EqualTo(20));Assert.That(document.Dirty,Is.False);
    }
    [Test] public void Document_SaveFollowsRenamedGraphSource()
    {
        var original=Path.Combine(_root,"Assets","Original.animgraph.json");
        var renamed=Path.Combine(_root,"Assets","Renamed.animgraph.json");
        AnimationAssetJson.Write(original,new AnimationGraph());
        var document=new AnimationDocument(original);
        File.Move(original,renamed);
        document.Relocate(renamed);
        document.Edit(()=>((AnimationGraph)document.Asset).Parameters.Add(new(){Name="Speed"}));
        document.Save();
        Assert.That(File.Exists(original),Is.False);
        Assert.That(AnimationAssetJson.Read<AnimationGraph>(renamed).Parameters.Single().Name,Is.EqualTo("Speed"));
    }
    [Test] public void LegacyMigration_CreatesGraphAssetsPreservesPlaybackAndIsIdempotent()
    {
        var clip=Guid.NewGuid();var rig=Guid.NewGuid();var path=Path.Combine(_root,"Assets","unit.prefab.json");
        var data=new JsonObject { ["SkeletonAsset"]=new JsonObject{["NodeId"]=rig},["ClipAsset"]=new JsonObject{["NodeId"]=clip},["Playing"]=false,["Loop"]=false,["Speed"]=2.5,["Time"]=.8 };
        File.WriteAllText(path,new JsonObject{["Entities"]=new JsonArray(new JsonObject{["Components"]=new JsonArray(new JsonObject{["Type"]=typeof(Animator).AssemblyQualifiedName,["Data"]=data})})}.ToJsonString());
        Assert.That(AnimationLegacyMigration.UpgradeProject(_root),Is.EqualTo(1));Assert.That(AnimationLegacyMigration.UpgradeProject(_root),Is.Zero);
        var graph=AnimationAssetJson.Read<AnimationGraph>(Directory.GetFiles(Path.Combine(_root,"Assets","Animation","Migrated"),"*.animgraph.json").Single());
        var player=graph.Nodes.Single(n=>n.Kind==AnimationNodeKind.Clip);
        Assert.That(player.Loop,Is.False);Assert.That(player.Speed,Is.EqualTo(2.5));Assert.That(player.StartTime,Is.EqualTo(.8f));Assert.That(graph.Parameters.Single().Default,Is.Zero);
        var source=File.ReadAllText(path);Assert.That(source,Does.Not.Contain("ClipAsset"));Assert.That(source,Does.Contain("GraphAsset"));
    }
    [Test] public void WorkspaceSeed_PreservesCustomLayoutAndActiveWorkspace_AndDoesNotRespawnDeletion()
    {
        var id=Guid.NewGuid();var prefs=new EditorWorkspacePreferences{Version=1,ImGuiSettings="custom layout",ActiveWorkspaceId=id,Workspaces=[new(){Id=id,Name="Custom",OpenWindowIds=[EditorWindowIds.Log]}]};
        var service=new EditorWorkspaceService(prefs);Assert.That(service.ActiveWorkspace.Id,Is.EqualTo(id));Assert.That(EditorPreferences.GetWorkspaceSettings()!.ImGuiSettings,Is.EqualTo("custom layout"));
        Assert.That(service.Workspaces.Count(w=>w.Id==EditorWorkspaceService.AnimationWorkspaceId),Is.EqualTo(1));
        service.Delete(EditorWorkspaceService.AnimationWorkspaceId);
        var restored=new EditorWorkspaceService(EditorPreferences.GetWorkspaceSettings());Assert.That(restored.Workspaces.Any(w=>w.Id==EditorWorkspaceService.AnimationWorkspaceId),Is.False);
    }
    [Test] public void AnimationWorkspace_MigratesOldDefaultButPreservesCustomPanels()
    {
        var old=new EditorWorkspacePreferences { Version=1, AnimationWorkspaceSeeded=true,
            ActiveWorkspaceId=EditorWorkspaceService.AnimationWorkspaceId,
            Workspaces=[new(){Id=EditorWorkspaceService.AnimationWorkspaceId,Name="Animation",
                OpenWindowIds=[EditorWindowIds.Assets,EditorWindowIds.Animation,EditorWindowIds.AssetEditor,EditorWindowIds.Log]}]};
        var migrated=new EditorWorkspaceService(old);
        Assert.That(migrated.ActiveWorkspace.OpenWindows,Is.EquivalentTo(new[]{EditorWindowIds.Animation}));
        Assert.That(migrated.ConsumeAnimationDockLayoutReset(),Is.True);
        Assert.That(migrated.ConsumeAnimationDockLayoutReset(),Is.False);
        old.Workspaces[0].OpenWindowIds=[EditorWindowIds.Animation,EditorWindowIds.Profiler];
        var custom=new EditorWorkspaceService(old);
        Assert.That(custom.ActiveWorkspace.OpenWindows,Is.EquivalentTo(new[]{EditorWindowIds.Animation,EditorWindowIds.Profiler}));
        Assert.That(custom.ConsumeAnimationDockLayoutReset(),Is.False);
    }
    [Test] public void GraphContextActions_CreateAtPointerAndDeleteConnectedNode()
    {
        var graph=new AnimationGraph();
        var clip=AnimationWindow.CreateNode(graph,AnimationNodeKind.Clip,new Vector2(120,80));
        var output=AnimationWindow.CreateNode(graph,AnimationNodeKind.Output,new Vector2(340,80));
        output.Inputs[0]=clip.Id;
        Assert.That(graph.Layout.Single(layout=>layout.NodeId==clip.Id).X,Is.EqualTo(120));
        Assert.That(graph.Output,Is.EqualTo(output.Id));
        AnimationWindow.DeleteNode(graph,clip.Id);
        Assert.That(output.Inputs[0],Is.EqualTo(Guid.Empty));
        Assert.That(graph.Layout.Any(layout=>layout.NodeId==clip.Id),Is.False);
        AnimationWindow.DeleteNode(graph,output.Id);
        Assert.That(graph.Output,Is.EqualTo(Guid.Empty));
    }
}
