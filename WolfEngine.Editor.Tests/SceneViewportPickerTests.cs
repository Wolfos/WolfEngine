using System.Numerics;
using WolfEngine.Animation;
using WolfEngine.Editor.UI;
using WolfEngine.ECS;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class SceneViewportPickerTests
{
	[Test]
	public void TryPick_ReturnsEntityUnderRay()
	{
		var world = new World(WorldTag.Authoring);
		var cube = CreateCube(world, "Cube", Matrix4x4.CreateTranslation(0.0f, 0.0f, 10.0f));

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out var hit);

		Assert.That(picked, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(cube));
		Assert.That(hit.Distance, Is.EqualTo(9.5f).Within(1e-3f));
		Assert.That(hit.Point.Z, Is.EqualTo(9.5f).Within(1e-3f));
	}

	[Test]
	public void TryPick_ReturnsFalseWhenRayMissesEveryMesh()
	{
		var world = new World(WorldTag.Authoring);
		CreateCube(world, "Cube", Matrix4x4.CreateTranslation(50.0f, 0.0f, 10.0f));

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out _);

		Assert.That(picked, Is.False);
	}

	[Test]
	public void TryPick_ReturnsNearestOfOverlappingMeshes()
	{
		var world = new World(WorldTag.Authoring);
		var far = CreateCube(world, "Far", Matrix4x4.CreateTranslation(0.0f, 0.0f, 30.0f));
		var near = CreateCube(world, "Near", Matrix4x4.CreateTranslation(0.0f, 0.0f, 10.0f));

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out var hit);

		Assert.That(picked, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(near));
		Assert.That(hit.Entity, Is.Not.EqualTo(far));
	}

	[Test]
	public void TryPick_IgnoresMeshesBeyondMaxDistance()
	{
		var world = new World(WorldTag.Authoring);
		CreateCube(world, "Cube", Matrix4x4.CreateTranslation(0.0f, 0.0f, 100.0f));

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 50.0f, out _);

		Assert.That(picked, Is.False);
	}

	[Test]
	public void TryPick_IgnoresDisabledEntities()
	{
		var world = new World(WorldTag.Authoring);
		var cube = CreateCube(world, "Cube", Matrix4x4.CreateTranslation(0.0f, 0.0f, 10.0f));
		world.SetEnabled(cube, false);

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out _);

		Assert.That(picked, Is.False);
	}

	[Test]
	public void TryPick_IgnoresMeshRenderersWithoutAResolvedMaterial()
	{
		var world = new World(WorldTag.Authoring);
		var entity = world.CreateEntity("Cube", Matrix4x4.Identity);
		SetWorldTransform(world, entity, Matrix4x4.CreateTranslation(0.0f, 0.0f, 10.0f));
		world.AddComponent(entity, new MeshRenderer { Mesh = CreateUnitCubeMesh() });

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out _);

		Assert.That(picked, Is.False);
	}

	[Test]
	public void TryPick_ReportsDistanceInWorldUnitsUnderNonUniformScale()
	{
		var world = new World(WorldTag.Authoring);
		// A cube stretched tenfold along the view axis: its near face sits 5 units in front of the
		// centre, not 0.5. A local-space distance would report the unscaled value.
		var transform = Matrix4x4.CreateScale(1.0f, 1.0f, 10.0f) * Matrix4x4.CreateTranslation(0.0f, 0.0f, 20.0f);
		var cube = CreateCube(world, "Stretched", transform);

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out var hit);

		Assert.That(picked, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(cube));
		Assert.That(hit.Distance, Is.EqualTo(15.0f).Within(1e-2f));
	}

	[Test]
	public void TryPick_PrefersASmallNearMeshOverALargeFarOne()
	{
		var world = new World(WorldTag.Authoring);
		CreateCube(world, "BigFar", Matrix4x4.CreateScale(20.0f) * Matrix4x4.CreateTranslation(0.0f, 0.0f, 40.0f));
		var near = CreateCube(world, "SmallNear", Matrix4x4.CreateScale(0.2f) * Matrix4x4.CreateTranslation(0.0f, 0.0f, 5.0f));

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out var hit);

		Assert.That(picked, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(near));
	}

	[Test]
	public void TryPick_HitsAZeroThicknessQuad()
	{
		var world = new World(WorldTag.Authoring);
		var quad = world.CreateEntity("Ground", Matrix4x4.Identity);
		SetWorldTransform(world, quad, Matrix4x4.CreateTranslation(0.0f, -2.0f, 0.0f));
		world.AddComponent(quad, new MeshRenderer
		{
			Mesh = CreateGroundQuadMesh(),
			Material = new Material("test")
		});

		var ray = new SceneViewportRay(Vector3.Zero, Vector3.Normalize(new Vector3(0.0f, -1.0f, 1.0f)));
		var picked = SceneViewportPicker.TryPick(world, ray, 1000.0f, out var hit);

		Assert.That(picked, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(quad));
		Assert.That(hit.Point.Y, Is.EqualTo(-2.0f).Within(1e-3f));
	}

	[Test]
	public void TryPick_SelectsSkinnedMeshesAgainstTheirBindPose()
	{
		var world = new World(WorldTag.Authoring);
		var entity = world.CreateEntity("Character", Matrix4x4.Identity);
		SetWorldTransform(world, entity, Matrix4x4.CreateTranslation(0.0f, 0.0f, 10.0f));
		world.AddComponent(entity, new SkinnedMeshRenderer
		{
			Mesh = CreateSkinnedUnitCubeMesh(),
			Material = new Material("test"),
			BoundsExpansion = SkinnedMeshRenderer.DefaultBoundsExpansion
		});

		var picked = SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out var hit);

		Assert.That(picked, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(entity));
	}

	[Test]
	public void TryPick_IgnoresEntitiesWithACollapsedTransform()
	{
		var world = new World(WorldTag.Authoring);
		var entity = world.CreateEntity("Collapsed", Matrix4x4.Identity);
		SetWorldTransform(world, entity, Matrix4x4.CreateScale(0.0f) * Matrix4x4.CreateTranslation(0.0f, 0.0f, 10.0f));
		world.AddComponent(entity, new MeshRenderer
		{
			Mesh = CreateUnitCubeMesh(),
			Material = new Material("test")
		});

		Assert.That(SceneViewportPicker.TryPick(world, ForwardRay(), 1000.0f, out _), Is.False);
	}

	[TestCase(false, false, ScenePickSelectionMode.Replace)]
	[TestCase(true, false, ScenePickSelectionMode.Add)]
	[TestCase(false, true, ScenePickSelectionMode.Toggle)]
	[TestCase(true, true, ScenePickSelectionMode.Toggle)]
	public void ResolveSelectionMode_MapsModifiersToSelectionBehaviour(
		bool shiftDown,
		bool primaryModifierDown,
		ScenePickSelectionMode expected)
	{
		Assert.That(
			SceneSelectionController.ResolveSelectionMode(shiftDown, primaryModifierDown),
			Is.EqualTo(expected));
	}

	private static SceneViewportRay ForwardRay() => new(Vector3.Zero, Vector3.UnitZ);

	private static Entity CreateCube(World world, string name, Matrix4x4 localToWorld)
	{
		var entity = world.CreateEntity(name, Matrix4x4.Identity);
		SetWorldTransform(world, entity, localToWorld);
		world.AddComponent(entity, new MeshRenderer
		{
			Mesh = CreateUnitCubeMesh(),
			Material = new Material("test")
		});

		return entity;
	}

	private static void SetWorldTransform(World world, Entity entity, Matrix4x4 localToWorld)
	{
		ref var worldTransform = ref world.GetComponent<WorldTransform>(entity);
		worldTransform.LocalToWorld = localToWorld;
		Matrix4x4.Invert(localToWorld, out worldTransform.WorldToLocal);
	}

	/// <summary>An axis-aligned cube spanning -0.5 to 0.5 on every axis.</summary>
	private static Mesh CreateUnitCubeMesh()
	{
		return new Mesh(CubeVertices(), CubeIndices());
	}

	private static Mesh CreateSkinnedUnitCubeMesh()
	{
		var vertices = CubeVertices();
		var boneIndices = new uint[vertices.Length * Mesh.InfluencesPerVertex];
		var boneWeights = new float[vertices.Length * Mesh.InfluencesPerVertex];
		for (var i = 0; i < vertices.Length; i++)
		{
			boneWeights[i * Mesh.InfluencesPerVertex] = 1.0f;
		}

		return new Mesh(vertices, CubeIndices(), boneIndices: boneIndices, boneWeights: boneWeights);
	}

	/// <summary>A horizontal quad in the XZ plane, so its local bounds have zero thickness in Y.</summary>
	private static Mesh CreateGroundQuadMesh()
	{
		var vertices = new[]
		{
			new Vector4(-50.0f, 0.0f, -50.0f, 1.0f),
			new Vector4(50.0f, 0.0f, -50.0f, 1.0f),
			new Vector4(50.0f, 0.0f, 50.0f, 1.0f),
			new Vector4(-50.0f, 0.0f, 50.0f, 1.0f)
		};

		return new Mesh(vertices, new uint[] { 0, 1, 2, 0, 2, 3 });
	}

	private static Vector4[] CubeVertices()
	{
		return
		[
			new Vector4(-0.5f, -0.5f, -0.5f, 1.0f),
			new Vector4(0.5f, -0.5f, -0.5f, 1.0f),
			new Vector4(0.5f, 0.5f, -0.5f, 1.0f),
			new Vector4(-0.5f, 0.5f, -0.5f, 1.0f),
			new Vector4(-0.5f, -0.5f, 0.5f, 1.0f),
			new Vector4(0.5f, -0.5f, 0.5f, 1.0f),
			new Vector4(0.5f, 0.5f, 0.5f, 1.0f),
			new Vector4(-0.5f, 0.5f, 0.5f, 1.0f)
		];
	}

	private static uint[] CubeIndices()
	{
		return
		[
			0, 2, 1, 0, 3, 2, // -Z
			4, 5, 6, 4, 6, 7, // +Z
			0, 1, 5, 0, 5, 4, // -Y
			3, 7, 6, 3, 6, 2, // +Y
			0, 4, 7, 0, 7, 3, // -X
			1, 2, 6, 1, 6, 5  // +X
		];
	}
}
