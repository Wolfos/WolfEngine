using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.ECS;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AnimationGraphTests
{
    private readonly Skeleton _skeleton = new("rig", ["root", "hand"], [-1, 0], [BoneTransform.Identity, new(Vector3.UnitY, Quaternion.Identity, Vector3.One)], [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)]);
    private static AnimationNode Clip(string slot) => new() { Kind = AnimationNodeKind.Clip, ClipSlot = slot, Loop = true };
    private static AnimationClip Motion(string bone, float end, float duration = 1) => new("motion", duration, 30, true,
        [new(AnimationBinding.ForBone(bone), new Vector3Curve([0, duration], [Vector3.Zero, Vector3.UnitX * end]), QuaternionCurve.Empty, Vector3Curve.Empty)], [], "", []);
    private CompiledAnimationGraph Program(AnimationGraph graph, Dictionary<string, AnimationClip>? clips = null,
        Dictionary<string, AnimationSequence>? sequences = null, BoneMask? mask = null)
    {
        var assets = new Dictionary<Guid, object>(); var set = new AnimationSet();
        foreach (var item in clips ?? [])
        {
            var clipId = Guid.NewGuid(); var sequenceId = Guid.NewGuid(); assets[clipId] = item.Value;
            var sequence = sequences?.GetValueOrDefault(item.Key) ?? new AnimationSequence(); sequence.ClipId = clipId;
            assets[sequenceId] = sequence; set.Clips[item.Key] = sequenceId;
        }
        if (mask is not null) foreach (var node in graph.Nodes.Where(n => n.Kind == AnimationNodeKind.MaskedBlend)) { node.MaskId = Guid.NewGuid(); assets[node.MaskId] = mask; }
        return AnimationGraphCompiler.Compile(graph, _skeleton, set, (id, _) => assets.GetValueOrDefault(id));
    }
    private static AnimationGraph Graph(AnimationNode result, params AnimationNode[] nodes)
    {
        var output = new AnimationNode { Kind = AnimationNodeKind.Output, Inputs = [result.Id] };
        return new() { Nodes = [.. nodes, result, output], Output = output.Id };
    }
    [Test]
    public void AuthoringBindPoseRendersAndPositionsSocketsWithoutEvaluatingGraph()
    {
        var clip = Clip("A");
        var world = new World(WorldTag.Authoring);
        var entity = world.CreateEntity("authoring unit", Matrix4x4.Identity);
        world.AddComponent(entity, new Animator { Skeleton = _skeleton, Graph = Graph(clip) });
        var socket = world.CreateEntity("hand socket", Matrix4x4.Identity);
        world.SetParent(socket, entity); world.AddComponent(socket, new ExposedBone(entity, "hand"));
        var system = new BindPoseAnimationSystem();
        system.Update(0, world);
        ref var animator = ref world.GetComponent<Animator>(entity);
        Assert.That(animator.GraphInstance, Is.Null);
        Assert.That(animator.Pose!.Bones[1].Position, Is.EqualTo(Vector3.UnitY));
        Assert.That(animator.SkinningMatrices, Is.Not.Null);
        Assert.That(animator.PreviousSkinningMatrices, Is.EqualTo(animator.SkinningMatrices));
        Assert.That(world.GetComponent<LocalTransform>(socket).LocalPosition, Is.EqualTo(Vector3.UnitY));
        var generation = animator.PoseGeneration;
        system.Update(10, world);
        Assert.That(animator.GraphInstance, Is.Null);
        Assert.That(animator.PoseGeneration, Is.EqualTo(generation));
    }
    [Test]
    public void RemovedWorldDropsAnimationInstancesPosesAndBindings()
    {
        var world = new World(WorldTag.Game);
        var entity = world.CreateEntity("unit", Matrix4x4.Identity);
        world.AddComponent(entity, new Animator { Skeleton = _skeleton });
        var system = new AnimationSystem();
        var manager = new WorldManager();
        manager.RegisterWorld(world);
        manager.AddSystem(system);
        system.Update(0, world);
        ref var animator = ref world.GetComponent<Animator>(entity);
        Assert.That(animator.GraphInstance, Is.Not.Null);
        Assert.That(animator.Bindings, Is.Not.Null);
        Assert.That(animator.SkinningMatrices, Is.Not.Null);
        manager.RemoveWorld(world);
        Assert.That(animator.GraphInstance, Is.Null);
        Assert.That(animator.Pose, Is.Null);
        Assert.That(animator.Bindings, Is.Null);
        Assert.That(animator.SkinningMatrices, Is.Null);
        Assert.That(animator.PreviousSkinningMatrices, Is.Null);
    }
    [Test]
    public void ExitProgress_UsesTheSelectedPoseSubgraph()
    {
        var a = Clip("A"); var b = Clip("B");
        var select = new AnimationNode { Kind = AnimationNodeKind.Select, Parameter = "Variant", Inputs = [a.Id, b.Id] };
        var rest = new AnimationNode { Kind = AnimationNodeKind.BindPose };
        var state = new AnimationNode { Kind = AnimationNodeKind.StateMachine, Inputs = [select.Id, rest.Id],
            Transitions = [new() { From = 0, To = 1, ExitTime = .05f, Duration = 0 }] };
        var graph = Graph(state, a, b, select, rest);
        graph.Parameters = [new() { Name = "Variant", Type = AnimationParameterType.Integer, Default = 1 }];
        var instance = Program(graph, new() { ["A"] = Motion("root", 1, 10), ["B"] = Motion("root", 1, 1) }).CreateInstance();
        instance.Evaluate(.1f); instance.Evaluate(.1f);
        Assert.That(instance.States.Single().State, Is.EqualTo(1));
    }

    [Test]
    public void CurveOnlyChanges_DoNotScheduleDeformation_AndKeepSocketsStable()
    {
        var rest = new AnimationNode { Kind = AnimationNodeKind.BindPose };
        var a = new AnimationNode { Kind = AnimationNodeKind.CurveRemap, Curve = "Intensity", CurveOffset = 1, Inputs = [rest.Id] };
        var b = new AnimationNode { Kind = AnimationNodeKind.CurveRemap, Curve = "Intensity", CurveOffset = 2, Inputs = [rest.Id] };
        var select = new AnimationNode { Kind = AnimationNodeKind.Select, Parameter = "Mode", Inputs = [a.Id, b.Id] };
        var graph = Graph(select, rest, a, b);
        graph.Parameters = [new() { Name = "Mode", Type = AnimationParameterType.Integer }];
        var world = new World(WorldTag.Game);
        var entity = world.CreateEntity("unit", Matrix4x4.Identity);
        world.AddComponent(entity, new Animator { Skeleton = _skeleton, Graph = graph });
        var socket = world.CreateEntity("hand socket", Matrix4x4.Identity);
        world.SetParent(socket, entity); world.AddComponent(socket, new ExposedBone(entity, "hand"));
        var system = new AnimationSystem(); system.Update(0, world);
        ref var animator = ref world.GetComponent<Animator>(entity);
        var generation = animator.PoseGeneration;
        var matrices = animator.SkinningMatrices!.ToArray();
        animator.Instance!.SetInteger(animator.Instance.Program.GetParameter("Mode", AnimationParameterType.Integer), 1);
        system.Update(.1f, world);
        Assert.That(animator.Instance.GetCurve(animator.Instance.Program.GetCurve("Intensity")), Is.EqualTo(2));
        Assert.That(animator.PoseGeneration, Is.EqualTo(generation));
        Assert.That(animator.SkinningMatrices, Is.EqualTo(matrices));
        Assert.That(world.GetComponent<LocalTransform>(socket).LocalPosition, Is.EqualTo(Vector3.UnitY));
        new TransformSystem().PreRender(0, world);
        animator.Instance.Playing = false; system.Update(10, world);
        Assert.That(animator.PoseGeneration, Is.EqualTo(generation));
        Assert.That(world.HasComponent<DirtyTransformRoot>(socket), Is.False);
    }

    [Test]
    public void SingleClip_MatchesLegacySamplerAndResetsMissingBones()
    {
        var node = Clip("A"); var clip = Motion("root", 4); var instance = Program(Graph(node), new() { ["A"] = clip }).CreateInstance();
        var baseline = new SingleClipPoseSource(clip, _skeleton); var pose = clip.CreatePose(_skeleton);
        foreach (var delta in new[] { 0f, 0.2f, 0.7f, 0.5f })
        {
            baseline.Evaluate(delta, pose); instance.Evaluate(delta);
            Assert.That(instance.Output.Bones[0].Position, Is.EqualTo(pose.Bones[0].Position));
            Assert.That(instance.Output.Bones[1].Position, Is.EqualTo(Vector3.UnitY));
        }
    }
    [Test]
    public void SharedAndMixedTransformTimelinesMatchDirectClipSampling()
    {
        foreach (var mixed in new[] { false, true })
        {
            var common = new[] { 0f, .5f, 1f };
            var rotation = new QuaternionCurve(common, [Quaternion.Identity,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f)]);
            var scale = new Vector3Curve(common, [Vector3.One, new(1.2f), new(1.5f)], CurveInterpolation.CubicHermite,
                [Vector3.Zero, Vector3.Zero, Vector3.Zero], [Vector3.Zero, Vector3.Zero, Vector3.Zero]);
            var first = new TransformTrack(AnimationBinding.ForBone("root"),
                new Vector3Curve(common, [Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2]), rotation, scale);
            var second = new TransformTrack(AnimationBinding.ForBone("hand"),
                mixed ? new Vector3Curve([0, .25f, 1], [Vector3.Zero, Vector3.UnitY, Vector3.UnitY * 2]) :
                    new Vector3Curve(common, [Vector3.Zero, Vector3.UnitY, Vector3.UnitY * 2]), rotation, scale);
            var clip = new AnimationClip("timelines", 1, 30, true, [first, second], [], "", []);
            var instance = Program(Graph(Clip("A")), new() { ["A"] = clip }).CreateInstance();
            var expected = clip.CreatePose(_skeleton);
            var sampler = new SingleClipPoseSource(clip, _skeleton);
            var previous = 0f;
            foreach (var time in new[] { 0f, .2f, .5f, .65f, 1f, 1.2f })
            {
                instance.Evaluate(time - previous); sampler.Evaluate(time - previous, expected); previous = time;
                for (var bone = 0; bone < _skeleton.BoneCount; bone++)
                {
                    Assert.That(instance.Output.Bones[bone].Position, Is.EqualTo(expected.Bones[bone].Position));
                    Assert.That(instance.Output.Bones[bone].Rotation, Is.EqualTo(expected.Bones[bone].Rotation));
                    Assert.That(instance.Output.Bones[bone].Scale, Is.EqualTo(expected.Bones[bone].Scale));
                }
            }
        }
    }
    [Test]
    public void ClipSwitch_ReturnsUnanimatedBonesToBindPose()
    {
        var a = Clip("A"); var b = Clip("B"); var select = new AnimationNode { Kind = AnimationNodeKind.Select, Inputs = [a.Id, b.Id], Parameter = "Select" };
        var graph = Graph(select, a, b); graph.Parameters = [new() { Name = "Select", Type = AnimationParameterType.Integer }];
        var instance = Program(graph, new() { ["A"] = Motion("hand", 4), ["B"] = Motion("root", 8) }).CreateInstance();
        instance.Evaluate(0.5f); instance.SetInteger(instance.Program.GetParameter("Select", AnimationParameterType.Integer), 1); instance.Evaluate(0.25f);
        Assert.That(instance.Output.Bones[1].Position, Is.EqualTo(Vector3.UnitY));
    }
    [Test]
    public void InactiveMaskedActionDoesNotSampleOrDeliverMarkers_ThenActivatesNormally()
    {
        var a = Clip("A"); var b = Clip("B");
        var blend = new AnimationNode { Kind = AnimationNodeKind.MaskedBlend, Inputs = [a.Id, b.Id], Parameter = "Weight" };
        var graph = Graph(blend, a, b); graph.Parameters = [new() { Name = "Weight" }];
        var instance = Program(graph, new() { ["A"] = Motion("root", 2), ["B"] = Motion("hand", 4) },
            new() { ["B"] = new() { Markers = [new() { Name = "Start", Time = .1f }] } },
            new() { Bones = [new() { Bone = "hand" }] }).CreateInstance();
        instance.Evaluate(.5f);
        Assert.That(instance.Output.Bones[0].Position.X, Is.EqualTo(1));
        Assert.That(instance.Output.Bones[1].Position, Is.EqualTo(Vector3.UnitY));
        Assert.That(instance.Markers, Is.Empty);
        Assert.That(instance.Contributions.Select(c => c.ClipSlot), Is.EquivalentTo(new[] { "A" }));
        instance.SetFloat(instance.Program.GetParameter("Weight", AnimationParameterType.Float), 1);
        instance.Evaluate(.25f);
        Assert.That(instance.Output.Bones[0].Position.X, Is.EqualTo(1.5f));
        Assert.That(instance.Output.Bones[1].Position.X, Is.EqualTo(1));
        Assert.That(instance.Markers.Single().Name, Is.EqualTo("Start"));
    }

    [Test]
    public void ConstantChannelCompilationPreservesCubicTangentsAndMissingDefaults()
    {
        var position = new Vector3Curve([0, 1], [Vector3.One, Vector3.One]);
        var scale = new Vector3Curve([0, 1], [Vector3.One, Vector3.One], CurveInterpolation.CubicHermite,
            [Vector3.Zero, Vector3.Zero], [Vector3.UnitX, Vector3.Zero]);
        var clip = new AnimationClip("constant", 1, 30, true,
            [new(AnimationBinding.ForBone("hand"), position, QuaternionCurve.Empty, scale)], [], "", []);
        var node = Clip("A"); var instance = Program(Graph(node), new() { ["A"] = clip }).CreateInstance();
        foreach (var time in new[] { 0f, .25f, .5f, .9f })
        {
            instance.Seek(time); var cursor = 0;
            Assert.That(instance.Output.Bones[1].Position, Is.EqualTo(Vector3.One));
            Assert.That(instance.Output.Bones[1].Scale, Is.EqualTo(scale.Evaluate(time, ref cursor, Vector3.Zero)));
            Assert.That(instance.Output.Bones[0].Position, Is.EqualTo(Vector3.Zero));
        }
    }

    [Test]
    public void MaskedBlend_LeavesLowerBodyUntouched()
    {
        var a = Clip("A"); var b = Clip("B"); var blend = new AnimationNode { Kind = AnimationNodeKind.MaskedBlend, Inputs = [a.Id, b.Id], Value = 1 };
        var instance = Program(Graph(blend, a, b), new() { ["A"] = Motion("root", 2), ["B"] = Motion("hand", 4) },
            mask: new() { Bones = [new() { Bone = "hand" }] }).CreateInstance();
        instance.Evaluate(0.5f);
        Assert.That(instance.Output.Bones[0].Position.X, Is.EqualTo(1)); Assert.That(instance.Output.Bones[1].Position.X, Is.EqualTo(2));
    }
    [Test]
    public void MarkerIntervals_HandleLoopsLargeDeltasAndScrub()
    {
        var node = Clip("A"); var instance = Program(Graph(node), new() { ["A"] = Motion("root", 1) },
            new() { ["A"] = new() { Markers = [new() { Name = "Impact", Time = 0.5f }] } }).CreateInstance();
        instance.Evaluate(2.75f); Assert.That(instance.Markers, Has.Count.EqualTo(3));
        instance.Evaluate(0.01f); Assert.That(instance.Markers, Is.Empty);
        instance.Seek(5); Assert.That(instance.Markers, Is.Empty);
        instance.Playing = false; instance.Evaluate(1); Assert.That(instance.Markers, Is.Empty);
    }
    [Test]
    public void RepeatedExternallyTimedActions_UseSequenceIdentity()
    {
        var node = Clip("A"); node.Loop = false; node.TimeParameter = "Time"; node.SequenceParameter = "Sequence";
        var graph = Graph(node); graph.Parameters = [new() { Name = "Time" }, new() { Name = "Sequence", Type = AnimationParameterType.Integer }];
        var instance = Program(graph, new() { ["A"] = Motion("root", 1) }, new() { ["A"] = new() { Markers = [new() { Name = "Impact", Time = 0.5f }] } }).CreateInstance();
        var time = instance.Program.GetParameter("Time", AnimationParameterType.Float); var sequence = instance.Program.GetParameter("Sequence", AnimationParameterType.Integer);
        instance.SetFloat(time, 0.6f); instance.Evaluate(0.1f); Assert.That(instance.Markers, Has.Count.EqualTo(1));
        instance.Evaluate(0.1f); Assert.That(instance.Markers, Is.Empty);
        instance.SetInteger(sequence, 1); instance.Evaluate(0.1f); Assert.That(instance.Markers, Has.Count.EqualTo(1));
    }
    [Test]
    public void SharedProgram_InstancesRemainIndependent_AndDoNotAllocate()
    {
        var node = Clip("A"); var program = Program(Graph(node), new() { ["A"] = Motion("root", 1) });
        var a = program.CreateInstance(); var b = program.CreateInstance(); a.Evaluate(0.25f); b.Evaluate(0.75f);
        Assert.That(a.Output.Bones[0].Position.X, Is.EqualTo(0.25f)); Assert.That(b.Output.Bones[0].Position.X, Is.EqualTo(0.75f));
        for (var i = 0; i < 100; i++) a.Evaluate(0.01f);
        var before = GC.GetAllocatedBytesForCurrentThread(); for (var i = 0; i < 100; i++) a.Evaluate(0.01f);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
    [Test]
    public void OffscreenSkinnedAnimatorAdvancesClockAndResamplesOnReentry()
    {
        var clip = Clip("A");
        var graph = Graph(clip);
        var instance = Program(graph, new() { ["A"] = Motion("root", 1) },
            new() { ["A"] = new() { Markers = [new() { Name = "Offscreen", Time = .15f }] } }).CreateInstance();
        var world = new World(WorldTag.Game);
        var camera = new Camera { ScreenResolution = new(800, 600) };
        camera.SetPerspective(70);
        var cameraEntity = world.CreateEntity("camera", Matrix4x4.Identity);
        world.AddComponent(cameraEntity, camera);
        var unit = world.CreateEntity("unit", new Vector3(100, 0, 10), Quaternion.Identity, Vector3.One);
        world.AddComponent(unit, new Animator
        {
            Skeleton = _skeleton, Graph = graph, GraphInstance = instance, Pose = instance.Output,
            SkinningMatrices = new Matrix4x4[_skeleton.BoneCount],
            PreviousSkinningMatrices = new Matrix4x4[_skeleton.BoneCount]
        });
        var meshEntity = world.CreateEntity("mesh", Matrix4x4.Identity);
        world.SetParent(meshEntity, unit);
        var mesh = new Mesh(
            [new Vector4(-.5f, 0, 0, 1), new Vector4(.5f, 0, 0, 1), new Vector4(0, 1, 0, 1)],
            [0u, 1u, 2u], boneIndices: new uint[12], boneWeights: new float[12]);
        world.AddComponent(meshEntity, new SkinnedMeshRenderer { AnimatorEntity = unit, Mesh = mesh });
        var system = new AnimationSystem();
        system.Update(.1f, world); // Initialize the pose and skinning palette, even when offscreen.
        var initialPosition = instance.Output.Bones[0].Position;
        system.Update(.1f, world);
        Assert.That(instance.Time, Is.EqualTo(.2f).Within(1e-5f));
        Assert.That(instance.Output.Bones[0].Position, Is.EqualTo(initialPosition));
        Assert.That(instance.Markers, Is.Empty);
        world.SetLocalPosition(unit, new Vector3(0, 0, 10));
        system.Update(.1f, world);
        Assert.That(instance.Output.Bones[0].Position.X, Is.EqualTo(.3f).Within(1e-5f));
        Assert.That(instance.Markers, Is.Empty);
        ref var animator = ref world.GetComponent<Animator>(unit);
        Assert.That(animator.PreviousSkinningMatrices, Is.EqualTo(animator.SkinningMatrices));
        system.Update(.1f, world);
        Assert.That(instance.Output.Bones[0].Position.X, Is.EqualTo(.4f).Within(1e-5f));
    }
    [Test]
    public void InvalidCycleAndMissingSlots_AreDiagnosed()
    {
        var a = new AnimationNode { Kind = AnimationNodeKind.Output }; a.Inputs = [a.Id];
        Assert.Throws<InvalidOperationException>(() => Program(new() { Nodes = [a], Output = a.Id }));
        Assert.Throws<InvalidOperationException>(() => Program(Graph(Clip("Missing"))));
    }
    [Test]
    public void FrozenPose_DoesNotReportRepeatedChanges()
    {
        var node = Clip("A"); var instance = Program(Graph(node), new() { ["A"] = Motion("root", 1) }).CreateInstance();
        instance.Evaluate(0.5f); instance.Playing = false; Assert.That(instance.Evaluate(1), Is.False);
    }
    [Test]
    public void Locomotion_SynchronizesDifferentClipDurations()
    {
        var a = Clip("A"); var b = Clip("B"); var locomotion = new AnimationNode { Kind = AnimationNodeKind.Locomotion1D, Inputs = [a.Id, b.Id], Thresholds = [1, 3], Value = 2 };
        var instance = Program(Graph(locomotion, a, b), new() { ["A"] = Motion("root", 1, 1), ["B"] = Motion("root", 1, 0.5f) }).CreateInstance(); instance.Evaluate(0.2f);
        Assert.That(instance.Contributions[0].NormalizedTime, Is.EqualTo(instance.Contributions[1].NormalizedTime).Within(1e-6));
    }
    [Test]
    public void ZeroWeightLocomotionSamplesOnlyOneClipAndKeepsPhaseWhenSecondActivates()
    {
        var a = Clip("A"); var b = Clip("B");
        var locomotion = new AnimationNode { Kind = AnimationNodeKind.Locomotion1D, Inputs = [a.Id, b.Id], Thresholds = [1, 3], Parameter = "Speed" };
        var graph = Graph(locomotion, a, b); graph.Parameters = [new() { Name = "Speed", Default = 1 }];
        var instance = Program(graph, new() { ["A"] = Motion("root", 1, 1), ["B"] = Motion("root", 2, .5f) }).CreateInstance();
        instance.Evaluate(.2f);
        Assert.That(instance.SampledClipCount, Is.EqualTo(1));
        Assert.That(instance.Contributions.Select(c => c.ClipSlot), Is.EqualTo(new[] { "A" }));
        instance.SetFloat(instance.Program.GetParameter("Speed", AnimationParameterType.Float), 2);
        instance.Evaluate(.2f);
        Assert.That(instance.SampledClipCount, Is.EqualTo(2));
        Assert.That(instance.Contributions[0].NormalizedTime, Is.EqualTo(instance.Contributions[1].NormalizedTime).Within(1e-6));
    }

    [Test]
    public void HeldSampleSkipsKeysButStillDeliversLoopMarkersAndInvalidatesCapturedDefaults()
    {
        var a = Clip("A");
        var tracked = new AnimationClip("held", 1, 30, true,
            [new(AnimationBinding.ForBone("root"), new Vector3Curve([0, 1], [Vector3.Zero, Vector3.UnitX]), QuaternionCurve.Empty, Vector3Curve.Empty)],
            [new(AnimationBinding.ForProperty("Lamp", "Intensity"), FloatCurve.Empty)], "", []);
        var instance = Program(Graph(a), new() { ["A"] = tracked },
            new() { ["A"] = new() { Markers = [new() { Name = "Pulse", Time = .5f }] } }).CreateInstance();
        instance.SetPropertyDefault(0, 2);
        instance.Evaluate(1);
        Assert.That(instance.SampledClipCount, Is.EqualTo(1));
        Assert.That(instance.Markers, Has.Count.EqualTo(1));
        instance.Evaluate(1);
        Assert.That(instance.SampledClipCount, Is.Zero);
        Assert.That(instance.Markers, Has.Count.EqualTo(1));
        Assert.That(instance.Output.Values[0], Is.EqualTo(2));
        instance.SetPropertyDefault(0, 7);
        instance.Evaluate(1);
        Assert.That(instance.SampledClipCount, Is.EqualTo(1));
        Assert.That(instance.Output.Values[0], Is.EqualTo(7));
        instance.Seek(3);
        Assert.That(instance.Markers, Is.Empty);
    }

    [Test]
    public void MaskedBlendSamplesBaseOnlyWhenItsWeightsContribute()
    {
        var a=Clip("A");var b=Clip("B");
        var mask = new BoneMask { Bones = [new() { Bone = "root" }] };
        var blend = new AnimationNode { Kind = AnimationNodeKind.MaskedBlend, Inputs = [a.Id,b.Id], Value = 1 };
        var instance=Program(Graph(blend,a,b),new(){["A"]=Motion("root",1),["B"]=Motion("root",2)}, mask:mask).CreateInstance();
        instance.Evaluate(.25f);
        Assert.That(instance.SampledClipCount, Is.EqualTo(1));
        Assert.That(instance.Contributions.Select(c=>c.ClipSlot), Is.EqualTo(new[]{"B"}));
        Assert.That(instance.Output.Bones[0].Position.X, Is.EqualTo(.5f));
        Assert.That(instance.Output.Bones[1].Position, Is.EqualTo(Vector3.UnitY));
    }

    [Test] public void DeferredSampling_PreservesClockAndMarkers()
    {
        var node=Clip("A");var instance=Program(Graph(node),new(){["A"]=Motion("root",1)},new(){["A"]=new(){Markers=[new(){Name="Foot",Time=.5f}]}}).CreateInstance();
        instance.AdvanceClock(.3f);instance.AdvanceClock(.3f);Assert.That(instance.Time,Is.EqualTo(.6f).Within(1e-6));instance.EvaluatePose();
        Assert.That(instance.Output.Bones[0].Position.X,Is.EqualTo(.6f).Within(1e-6));Assert.That(instance.Markers,Has.Count.EqualTo(1));
    }
    [Test] public void Channels_BlendByIdentity_AndCaptureMissingDefaults()
    {
        AnimationClip clip(string[] paths,float[] values)=>new("channels",1,30,true,paths.Select((path,i)=>new TransformTrack(AnimationBinding.ForProperty(path,""),new([0],[new Vector3(values[i],0,0)]),QuaternionCurve.Empty,Vector3Curve.Empty)).ToArray(),[],"",[]);
        var a=Clip("A");var b=Clip("B");var blend=new AnimationNode{Kind=AnimationNodeKind.Blend,Inputs=[a.Id,b.Id],Value=.5f};
        var instance=Program(Graph(blend,a,b),new(){["A"]=clip(["Door","Turret"],[2,4]),["B"]=clip(["Turret","Door"],[8,6])}).CreateInstance();instance.Evaluate(0);
        Assert.That(instance.Output.Transforms[0].Position.X,Is.EqualTo(4));Assert.That(instance.Output.Transforms[1].Position.X,Is.EqualTo(6));
    }
    [Test] public void Curves_BlendAndRemap_WithMissingCurveDefaults()
    {
        var a=Clip("A");var b=Clip("B");var blend=new AnimationNode{Kind=AnimationNodeKind.Blend,Inputs=[a.Id,b.Id],Value=.5f};var remap=new AnimationNode{Kind=AnimationNodeKind.CurveRemap,Inputs=[blend.Id],Curve="Glow",CurveScale=3,CurveOffset=1};
        var instance=Program(Graph(remap,a,b,blend),new(){["A"]=Motion("root",1),["B"]=Motion("root",1)},new(){["A"]=new(){Curves=[new(){Name="Glow",Times=[0,1],Values=[0,2]}]}}).CreateInstance();instance.Evaluate(.5f);
        Assert.That(instance.GetCurve(instance.Program.GetCurve("Glow")),Is.EqualTo(2.5f));
    }
    [Test] public void StateInterruption_CapturesBlendedPose_AndSuppressesExitedMarkers()
    {
        var a=Clip("A");var b=Clip("B");var c=Clip("C");var state=new AnimationNode{Kind=AnimationNodeKind.StateMachine,Inputs=[a.Id,b.Id,c.Id],Transitions=[new(){To=1,Duration=1,Conditions=[new(){Parameter="State",Value=1}]},new(){To=2,Duration=1,Conditions=[new(){Parameter="State",Value=2}]}]};
        var graph=Graph(state,a,b,c);graph.Parameters=[new(){Name="State",Type=AnimationParameterType.Integer}];var instance=Program(graph,new(){["A"]=Motion("root",0),["B"]=Motion("root",4),["C"]=Motion("root",8)},new(){["B"]=new(){Markers=[new(){Name="Exited",Time=.5f}]}}).CreateInstance();
        var handle=instance.Program.GetParameter("State",AnimationParameterType.Integer);instance.Evaluate(0);instance.SetInteger(handle,1);instance.Evaluate(.25f);var before=instance.Output.Bones[0].Position.X;
        instance.SetInteger(handle,2);instance.Evaluate(0);Assert.That(instance.Output.Bones[0].Position.X,Is.EqualTo(before));instance.Evaluate(.6f);Assert.That(instance.Markers,Is.Empty);
    }
    [Test] public void SelectedBranch_RestartsAfterZeroWeight_AndHoldsNonLoopingFinalPose()
    {
        var a=Clip("A");a.Loop=false;var rest=new AnimationNode{Kind=AnimationNodeKind.BindPose};var blend=new AnimationNode{Kind=AnimationNodeKind.Blend,Inputs=[rest.Id,a.Id],Parameter="Weight"};var graph=Graph(blend,rest,a);graph.Parameters=[new(){Name="Weight"}];
        var instance=Program(graph,new(){["A"]=Motion("root",4)}).CreateInstance();var handle=instance.Program.GetParameter("Weight",AnimationParameterType.Float);
        instance.Evaluate(.7f);instance.SetFloat(handle,1);instance.Evaluate(.2f);Assert.That(instance.Output.Bones[0].Position.X,Is.EqualTo(.8f));instance.Evaluate(3);Assert.That(instance.Output.Bones[0].Position.X,Is.EqualTo(4));Assert.That(instance.Evaluate(1),Is.False);
    }

    [Test] public void MarkerIntervals_DeliverChronologicallyAcrossLoops()
    {
        var node = Clip("A");
        var instance = Program(Graph(node), new() { ["A"] = Motion("root", 1) },
            new() { ["A"] = new() { Markers = [new() { Name="Late", Time=.8f }, new() { Name="Early", Time=.2f }] } }).CreateInstance();
        instance.Evaluate(2);
        Assert.That(instance.Markers.Select(m => m.Name), Is.EqualTo(new[] {"Early", "Late", "Early", "Late"}));
    }
    [Test] public void SeekLocomotion_PreservesRequestedPhase_AndSuppressesMarkers()
    {
        var a=Clip("A"); var b=Clip("B");
        var blend=new AnimationNode {Kind=AnimationNodeKind.Locomotion1D,Inputs=[a.Id,b.Id],Thresholds=[1,2],Value=1};
        var instance=Program(Graph(blend,a,b),new(){["A"]=Motion("root",1),["B"]=Motion("root",2)}).CreateInstance();
        instance.Seek(.75f);
        Assert.That(instance.Output.Bones[0].Position.X,Is.EqualTo(.75f)); Assert.That(instance.Markers,Is.Empty);
    }
    [Test] public void SequencePlaybackSpeed_IsAuthoredSeparatelyFromImportedKeys()
    {
        var node=Clip("A"); var instance=Program(Graph(node),new(){["A"]=Motion("root",1)},new(){["A"]=new(){PlaybackSpeed=2}}).CreateInstance();
        instance.Evaluate(.2f); Assert.That(instance.Output.Bones[0].Position.X,Is.EqualTo(.4f).Within(1e-6));
    }

}
