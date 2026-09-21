using System.Numerics;
using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class MaterialPreviewSceneTests
{
	[Test]
	public void UsesMeshMaterialAndDirectionalLightOnlyWhileVisible()
	{
		var view = RenderViewId.FromIndex(1);
		var host = Substitute.For<IRenderViewHost>();
		host.CreateView(Arg.Any<RenderViewDescriptor>()).Returns(view);
		var worlds = Substitute.For<IWorldManager>();
		var sources = new EditorRenderViews();
		var states = new EditorViewportStateBus();
		var sphere = new DebugPrimitiveMeshFactory().GetMesh(DebugPrimitiveType.Sphere);
		using var preview = new MaterialPreviewScene(host, worlds, sources, states, sphere);
		var material = new Material("gbuffer.slang");
		preview.SetMaterial(material);

		var meshCount = 0;
		foreach (var entry in preview.World.View<MeshRenderer>())
		{
			meshCount++;
			Assert.That(entry.First.Mesh, Is.SameAs(sphere));
			Assert.That(entry.First.Material, Is.SameAs(material));
		}
		Assert.That(meshCount, Is.EqualTo(1));
		var lightCount = 0;
		foreach (var entry in preview.World.View<Light>()) lightCount++;
		Assert.That(lightCount, Is.EqualTo(1));
		var cameraCount = 0;
		foreach (var entry in preview.World.View<Camera>()) cameraCount++;
		Assert.That(cameraCount, Is.EqualTo(1));
		Assert.That(preview.PrepareSubmission(0, out _), Is.False);

		states.PublishUiState(view, new SceneViewportUiState(
			true, new Int2(256, 256), 1, SceneDebugViewIds.FinalColor,
			false, false, false, false, false, Vector2.Zero, new Vector2(256, 256)));
		Assert.That(preview.PrepareSubmission(0, out var submission), Is.True);
		Assert.That(submission.View, Is.EqualTo(view));
		preview.Dispose();
		Assert.That(preview.PrepareSubmission(0, out _), Is.False);
		host.Received(1).DestroyView(view);
		worlds.Received(1).RemoveWorld(preview.World);
	}
}
