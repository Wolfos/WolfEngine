using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public enum EntityCreationPreset
{
	Empty,
	PointLight,
	DirectionalLight,
	Cube,
	Sphere
}

internal static class EntityCreationOperations
{
	// Lights shine along local +Z. Pitching down keeps a new directional light clear of the horizon fade.
	private static readonly Quaternion DirectionalLightRotation =
		Quaternion.CreateFromAxisAngle(Vector3.UnitX, 50.0f * MathF.PI / 180.0f);

	public static string GetDisplayName(EntityCreationPreset preset) => preset switch
	{
		EntityCreationPreset.Empty => "Entity",
		EntityCreationPreset.PointLight => "Point Light",
		EntityCreationPreset.DirectionalLight => "Directional Light",
		EntityCreationPreset.Cube => "Cube",
		EntityCreationPreset.Sphere => "Sphere",
		_ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
	};

	public static Entity CreateEntity(
		EditorScene scene,
		EntityCreationPreset preset,
		Vector3 position,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(sceneSnapshotService);
		ArgumentNullException.ThrowIfNull(undoRedoService);
		ArgumentNullException.ThrowIfNull(interactionState);

		var world = scene.World;
		var name = GetDisplayName(preset);
		var rotation = preset == EntityCreationPreset.DirectionalLight ? DirectionalLightRotation : Quaternion.Identity;
		var entity = world.CreateEntity(name, position, rotation, Vector3.One);

		switch (preset)
		{
			case EntityCreationPreset.Empty:
				break;
			case EntityCreationPreset.PointLight:
				AddLight(scene, entity, new Light
				{
					Type = LightType.Point, Color = ColorRGBA.White, Intensity = 1.0f, Range = 10.0f
				});
				break;
			case EntityCreationPreset.DirectionalLight:
				AddLight(scene, entity, new Light
				{
					Type = LightType.Directional, Color = ColorRGBA.White, Intensity = 1.0f, Range = 25.0f, HorizonFade = true
				});
				break;
			case EntityCreationPreset.Cube:
				AddMeshRenderer(world, entity, BuiltInEngineAssets.CubeMesh);
				break;
			case EntityCreationPreset.Sphere:
				AddMeshRenderer(world, entity, BuiltInEngineAssets.SphereMesh);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(preset), preset, null);
		}

		EditorGui.SelectEntity(entity, world);
		var snapshots = sceneSnapshotService.CaptureDeletedEntities(scene, [entity]);
		if (snapshots.Count > 0)
		{
			var description = $"Create {name}";
			undoRedoService.BeginCapture(description);
			undoRedoService.CommitCapture(new EntityCreationUndoRedoEntry(description, snapshots));
		}

		interactionState.MarkSceneDirty(world);
		return entity;
	}

	private static void AddLight(EditorScene scene, Entity entity, Light light)
	{
		scene.World.AddComponent(entity, light);
		scene.EntityIcons[entity] = "light";
	}

	private static void AddMeshRenderer(World world, Entity entity, AssetRef<Mesh> mesh)
	{
		// Only the references are stored; the renderer resolves the assets when it validates the component.
		world.AddComponent(entity, new MeshRenderer
		{
			MeshAsset = mesh,
			MaterialAsset = BuiltInEngineAssets.DefaultMaterial
		});
	}
}
