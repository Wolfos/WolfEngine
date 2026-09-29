using System.Numerics;
using NSubstitute;
using Microsoft.Data.Sqlite;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AnimationAssetPipelineTests
{
    private string _root = "";
    [SetUp] public void Setup() { _root=Path.Combine(Path.GetTempPath(),"animation-pipeline-"+Guid.NewGuid()); }
    [TearDown] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(_root,true); }
    [Test] public void RigReimport_RebuildsBoundClips_PreservesIndependentSequenceMetadata()
    {
        var importer=Substitute.For<IThreeDFileImporter>();
        var rig=new ImportedSkeleton("rig",["root"],[-1],[BoneTransform.Identity],[Matrix4x4.Identity]);
        importer.Import(Arg.Is<string>(p=>p.EndsWith("Rig.FBX")),Arg.Any<ModelImportSettings>()).Returns(new ImportedScene("rig",[],[],[new("root",Matrix4x4.Identity,[],-1)],[rig],[]));
        importer.Import(Arg.Is<string>(p=>p.EndsWith("Take.FBX")),Arg.Any<ModelImportSettings>()).Returns(new ImportedScene("take",[],[],[new("root",Matrix4x4.Identity,[],-1)],[],[new("take",1,30,-1,[new(AnimationBinding.ForProperty("root",""),Vector3Curve.Empty,QuaternionCurve.Empty,Vector3Curve.Empty)],[])]));
        var index=new AssetPipelineIndex(); var metadata=new AssetMetadataStore();
        var pipeline=new ProjectAssetPipelineService(index,metadata,Substitute.For<IImageLoader>(),new DataAssetStore(),new MaterialAssetStore(),importer,null!);
        pipeline.InitializeProject(_root);
        File.WriteAllText(Path.Combine(_root,"Assets/Rig.FBX"),"model"); pipeline.ReimportSource(_root,"Assets/Rig.FBX");
        var rigId=metadata.Load(Path.Combine(_root,"Assets/Rig.FBX.meta")).SubAssets.Single(a=>a.Type==AssetType.Skeleton).NodeId;
        File.WriteAllText(Path.Combine(_root,"Assets/Take.FBX"),"clip");
        var meta=new AssetSourceMetaFile{SourceId=Guid.NewGuid(),ImporterId=AssetImporterIds.ThreeDScene,ImporterVersion=12};meta.ImportSettingsJson=System.Text.Json.JsonSerializer.Serialize(new ModelImportSettings{AnimationSkeletonId=rigId},AssetJson.SerializerOptions);metadata.Save(Path.Combine(_root,"Assets/Take.FBX.meta"),meta);
        pipeline.ReimportSource(_root,"Assets/Take.FBX");
        var clipId=metadata.Load(Path.Combine(_root,"Assets/Take.FBX.meta")).SubAssets.Single(a=>a.Type==AssetType.AnimationClip).NodeId;
        var sequencePath=Path.Combine(_root,"Assets/Action.animclip.json");
        AnimationAssetJson.Write(sequencePath,new AnimationSequence{ClipId=clipId,PlaybackSpeed=2,Markers=[new(){Name="Release",Time=.5f}],Curves=[new(){Name="Intensity",Times=[0,1],Values=[0,1]}]});
        pipeline.ReimportSource(_root,"Assets/Action.animclip.json");
        var sequenceId=metadata.Load(sequencePath+".meta").SubAssets.Single().NodeId;
        var before=File.ReadAllText(sequencePath);importer.ClearReceivedCalls();
        pipeline.ReimportSource(_root,"Assets/Rig.FBX");
        importer.Received().Import(Arg.Is<string>(p=>p.EndsWith("Take.FBX")),Arg.Any<ModelImportSettings>());
        Assert.That(File.ReadAllText(sequencePath),Is.EqualTo(before));
        Assert.That(metadata.Load(Path.Combine(_root,"Assets/Take.FBX.meta")).SubAssets.Single(a=>a.Type==AssetType.AnimationClip).NodeId,Is.EqualTo(clipId));
        Assert.That(pipeline.ExpandInvalidationClosure(_root,[rigId]),Does.Contain(clipId).And.Contain(sequenceId));
        Assert.That(index.GetDependencies(_root).Any(d=>d.FromNodeId==clipId && d.ToNodeId==rigId),Is.True);
    }
}
