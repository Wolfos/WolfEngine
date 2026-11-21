using System.Numerics;
using System.Reflection;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EntityGizmoDrawerTests
{
	[TearDown]
	public void TearDown()
	{
		EditorGui.ClearEntitySelection();
		EditorPreferences.SetShowEntityIconGizmos(true);
	}

	[Test]
	public void LightGizmoDrawer_WithoutSelection_DoesNotDrawLines()
	{
		var world = new World(WorldTag.Authoring);
		CreateLight(world, LightType.Point, range: 5.0f);
		var renderer = new RecordingGizmoRenderer();

		new LightGizmoDrawer(renderer).OnDrawGizmos(world);

		Assert.That(renderer.Lines, Is.Empty);
	}

	[Test]
	public void LightGizmoDrawer_PointLight_DrawsThreeCirclesAtRange()
	{
		var world = new World(WorldTag.Authoring);
		var light = CreateLight(world, LightType.Point, range: 5.0f);
		EditorGui.SelectEntity(light, world, requestFocus: false);
		var renderer = new RecordingGizmoRenderer();

		new LightGizmoDrawer(renderer).OnDrawGizmos(world);

		Assert.That(renderer.Lines.Count, Is.EqualTo(3 * GizmoShapes.CircleSegments));
		foreach (var line in renderer.Lines)
		{
			Assert.That(line.Start.Length(), Is.EqualTo(5.0f).Within(1e-3f));
		}
	}

	[Test]
	public void LightGizmoDrawer_DirectionalLight_DrawsArrowAlongForward()
	{
		var world = new World(WorldTag.Authoring);
		var light = CreateLight(world, LightType.Directional, range: 25.0f);
		EditorGui.SelectEntity(light, world, requestFocus: false);
		var renderer = new RecordingGizmoRenderer();

		new LightGizmoDrawer(renderer).OnDrawGizmos(world);

		var expectedTip = new Vector3(0.0f, 0.0f, LightGizmoDrawer.DirectionArrowLength);
		Assert.That(
			renderer.Lines.Any(line => line.Start == Vector3.Zero && Vector3.Distance(line.End, expectedTip) < 1e-4f),
			Is.True);
	}

	[Test]
	public void CameraGizmoDrawer_WithSelection_DrawsTwelveFrustumEdges()
	{
		var world = new World(WorldTag.Authoring);
		var entity = CreateCamera(world);
		EditorGui.SelectEntity(entity, world, requestFocus: false);
		var renderer = new RecordingGizmoRenderer();

		new CameraGizmoDrawer(renderer).OnDrawGizmos(world);

		Assert.That(renderer.Lines.Count, Is.EqualTo(12));
	}

	[Test]
	public void CameraGizmoDrawer_CapsFarEndOfFrustum()
	{
		var world = new World(WorldTag.Authoring);
		var entity = CreateCamera(world);
		EditorGui.SelectEntity(entity, world, requestFocus: false);
		var renderer = new RecordingGizmoRenderer();

		new CameraGizmoDrawer(renderer).OnDrawGizmos(world);

		var farthest = renderer.Lines.Max(line => MathF.Max(line.Start.Z, line.End.Z));
		Assert.That(farthest, Is.EqualTo(CameraGizmoDrawer.MaxDrawnFarDistance).Within(1e-4f));
	}

	[Test]
	public void CameraGizmoDrawer_WithoutSelection_DoesNotDrawLines()
	{
		var world = new World(WorldTag.Authoring);
		CreateCamera(world);
		var renderer = new RecordingGizmoRenderer();

		new CameraGizmoDrawer(renderer).OnDrawGizmos(world);

		Assert.That(renderer.Lines, Is.Empty);
	}

	[Test]
	public void EntityIconGizmoDrawer_DrawsEveryEntityWithAnIconWithoutCheckingComponents()
	{
		var scene = new EditorScene();
		var light = CreateLight(scene.World, LightType.Point, range: 5.0f);
		var camera = CreateCamera(scene.World);
		var manual = scene.World.CreateEntity("Manual", Vector3.Zero, Quaternion.Identity, Vector3.One);
		new TransformSystem().PreRender(0.0f, scene.World);
		scene.EntityIcons[light] = "light";
		scene.EntityIcons[camera] = "camera";
		scene.EntityIcons[manual] = "camera";
		var renderer = new RecordingGizmoRenderer();

		new EntityIconGizmoDrawer(renderer, new FakeIconManager()).Draw(scene);

		Assert.That(renderer.IconTextures.Count(texture => texture == FakeIconManager.LightTexture), Is.EqualTo(1));
		Assert.That(renderer.IconTextures.Count(texture => texture == FakeIconManager.CameraTexture), Is.EqualTo(2));
	}

	[Test]
	public void EntityIconGizmoDrawer_SkipsEntitiesWithoutIcon()
	{
		var scene = new EditorScene();
		CreateLight(scene.World, LightType.Point, range: 5.0f);
		var renderer = new RecordingGizmoRenderer();

		new EntityIconGizmoDrawer(renderer, new FakeIconManager()).Draw(scene);

		Assert.That(renderer.IconTextures, Is.Empty);
	}

	[Test]
	public void EntityIconGizmoDrawer_WhenDisabled_DrawsNothing()
	{
		var scene = new EditorScene();
		var light = CreateLight(scene.World, LightType.Point, range: 5.0f);
		scene.EntityIcons[light] = "light";
		EditorPreferences.SetShowEntityIconGizmos(false);
		var renderer = new RecordingGizmoRenderer();

		new EntityIconGizmoDrawer(renderer, new FakeIconManager()).Draw(scene);

		Assert.That(renderer.IconTextures, Is.Empty);
	}

	[Test]
	public void Camera_HasDefaultIconAttribute()
	{
		var attribute = typeof(Camera).GetCustomAttribute<DefaultIconAttribute>();

		Assert.That(attribute?.iconName, Is.EqualTo("camera"));
	}

	private static Entity CreateLight(World world, LightType type, float range)
	{
		var entity = world.CreateEntity("Light", Vector3.Zero, Quaternion.Identity, Vector3.One);
		world.AddComponent(entity, new Light { Type = type, Intensity = 1.0f, Range = range, Color = ColorRGBA.White });
		new TransformSystem().PreRender(0.0f, world);
		return entity;
	}

	private static Entity CreateCamera(World world)
	{
		var entity = world.CreateEntity("Camera", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var camera = new Camera
		{
			ScreenResolution = new Int2(1600, 900),
			NearPlane = 0.1f,
			FarPlane = 10000.0f
		};
		camera.SetPerspective(60.0f);
		world.AddComponent(entity, camera);
		new TransformSystem().PreRender(0.0f, world);
		return entity;
	}

	private sealed class RecordingGizmoRenderer : IGizmoLineRenderer
	{
		public List<(Vector3 Start, Vector3 End)> Lines { get; } = new();
		public List<nint> IconTextures { get; } = new();

		public void BeginFrame()
		{
		}

		public void DrawLine(Vector3 startWorld, Vector3 endWorld, ColorRGBA color, float thickness = 2.0f)
		{
			Lines.Add((startWorld, endWorld));
		}

		public void DrawIcon(Vector3 positionWorld, nint textureId, float sizePixels, ColorRGBA tint)
		{
			IconTextures.Add(textureId);
		}
	}

	private sealed class FakeIconManager : IIconManager
	{
		public const nint LightTexture = 1;
		public const nint CameraTexture = 2;

		public nint Get(string name)
		{
			return TryGet(name, out var textureId) ? textureId : throw new KeyNotFoundException(name);
		}

		public bool TryGet(string name, out nint textureId)
		{
			textureId = name switch
			{
				"light" => LightTexture,
				"camera" => CameraTexture,
				_ => 0
			};

			return textureId != 0;
		}

		public IReadOnlyList<string> GetNames()
		{
			return new[] { "light", "camera" };
		}
	}
}
