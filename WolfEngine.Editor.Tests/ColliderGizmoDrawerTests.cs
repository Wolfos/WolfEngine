using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Physics;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ColliderGizmoDrawerTests
{
	[TearDown]
	public void TearDown()
	{
		EditorGui.ClearEntitySelection();
	}

	[Test]
	public void BoxColliderGizmoDrawer_WithoutSelection_DoesNotDrawLines()
	{
		var world = new World(WorldTag.Authoring);
		var entity = CreateEntityWithTransform(world, "Box");
		world.AddComponent(entity, BoxCollider.CreateDefault());
		UpdateWorldTransforms(world);
		var lineRenderer = new TestGizmoLineRenderer();
		var drawer = new BoxColliderGizmoDrawer(lineRenderer);

		drawer.OnDrawGizmos(world);

		Assert.That(lineRenderer.LineCount, Is.EqualTo(0));
	}

	[Test]
	public void BoxColliderGizmoDrawer_WithDifferentSelection_DoesNotDrawLines()
	{
		var world = new World(WorldTag.Authoring);
		var colliderEntity = CreateEntityWithTransform(world, "Box");
		var selectedEntity = CreateEntityWithTransform(world, "Other");
		world.AddComponent(colliderEntity, BoxCollider.CreateDefault());
		UpdateWorldTransforms(world);
		EditorGui.SelectEntity(selectedEntity, world, requestFocus: false);
		var lineRenderer = new TestGizmoLineRenderer();
		var drawer = new BoxColliderGizmoDrawer(lineRenderer);

		drawer.OnDrawGizmos(world);

		Assert.That(lineRenderer.LineCount, Is.EqualTo(0));
	}

	[Test]
	public void BoxColliderGizmoDrawer_WithMatchingSelection_DrawsWireBox()
	{
		var world = new World(WorldTag.Authoring);
		var entity = CreateEntityWithTransform(world, "Box");
		world.AddComponent(entity, BoxCollider.CreateDefault());
		UpdateWorldTransforms(world);
		EditorGui.SelectEntity(entity, world, requestFocus: false);
		var lineRenderer = new TestGizmoLineRenderer();
		var drawer = new BoxColliderGizmoDrawer(lineRenderer);

		drawer.OnDrawGizmos(world);

		Assert.That(lineRenderer.LineCount, Is.EqualTo(12));
	}

	[Test]
	public void CapsuleColliderGizmoDrawer_WithoutSelection_DoesNotDrawLines()
	{
		var world = new World(WorldTag.Authoring);
		var entity = CreateEntityWithTransform(world, "Capsule");
		world.AddComponent(entity, CapsuleCollider.CreateDefault());
		UpdateWorldTransforms(world);
		var lineRenderer = new TestGizmoLineRenderer();
		var drawer = new CapsuleColliderGizmoDrawer(lineRenderer);

		drawer.OnDrawGizmos(world);

		Assert.That(lineRenderer.LineCount, Is.EqualTo(0));
	}

	[Test]
	public void CapsuleColliderGizmoDrawer_WithDifferentSelection_DoesNotDrawLines()
	{
		var world = new World(WorldTag.Authoring);
		var colliderEntity = CreateEntityWithTransform(world, "Capsule");
		var selectedEntity = CreateEntityWithTransform(world, "Other");
		world.AddComponent(colliderEntity, CapsuleCollider.CreateDefault());
		UpdateWorldTransforms(world);
		EditorGui.SelectEntity(selectedEntity, world, requestFocus: false);
		var lineRenderer = new TestGizmoLineRenderer();
		var drawer = new CapsuleColliderGizmoDrawer(lineRenderer);

		drawer.OnDrawGizmos(world);

		Assert.That(lineRenderer.LineCount, Is.EqualTo(0));
	}

	[Test]
	public void CapsuleColliderGizmoDrawer_WithMatchingSelection_DrawsWireCapsule()
	{
		var world = new World(WorldTag.Authoring);
		var entity = CreateEntityWithTransform(world, "Capsule");
		world.AddComponent(entity, CapsuleCollider.CreateDefault());
		UpdateWorldTransforms(world);
		EditorGui.SelectEntity(entity, world, requestFocus: false);
		var lineRenderer = new TestGizmoLineRenderer();
		var drawer = new CapsuleColliderGizmoDrawer(lineRenderer);

		drawer.OnDrawGizmos(world);

		Assert.That(lineRenderer.LineCount, Is.GreaterThan(0));
	}

	[Test]
	public void SceneWindowShouldDrawGizmos_OnlyReturnsTrueInEditMode()
	{
		Assert.That(SceneWindow.ShouldDrawGizmos(EditorPlayState.Edit), Is.True);
		Assert.That(SceneWindow.ShouldDrawGizmos(EditorPlayState.Playing), Is.False);
		Assert.That(SceneWindow.ShouldDrawGizmos(EditorPlayState.Paused), Is.False);
	}

	private static Entity CreateEntityWithTransform(World world, string name)
	{
		return world.CreateEntity(name, Vector3.Zero, Quaternion.Identity, Vector3.One);
	}

	private static void UpdateWorldTransforms(World world)
	{
		new TransformSystem().PreRender(0.0f, world);
	}

	private sealed class TestGizmoLineRenderer : IGizmoLineRenderer
	{
		public int LineCount { get; private set; }

		public void BeginFrame()
		{
		}

		public void DrawLine(Vector3 startWorld, Vector3 endWorld, ColorRGBA color, float thickness = 2.0f)
		{
			LineCount++;
		}
	}
}
