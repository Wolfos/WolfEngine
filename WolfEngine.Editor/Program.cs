using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;
using WolfEngine.Editor.UI;
using WolfEngine.Physics;

namespace WolfEngine.Editor;

public static class Program
{
	public static void Main()
	{
		var services = new ServiceCollection();
		WolfEngine.ConfigureServices(services);
		ConfigureServices(services);
		
		var provider = services.BuildServiceProvider();

		var worldManager = provider.GetRequiredService<IWorldManager>();
		worldManager.AddSystem(new VehicleSystem(), SystemExecutionGroup.Gameplay);
		worldManager.AddSystem(provider.GetRequiredService<RigidbodySystem>(), SystemExecutionGroup.Gameplay);
		
		AssetDatabase.SetInstanceRegistry(provider.GetRequiredService<IAssetInstanceRegistry>());
		provider.GetRequiredService<IUiFrameProvider>();
		provider.GetRequiredService<IIconManager>();
		EditorPreferences.Load();
		var lastProjectPath = EditorPreferences.GetLastProjectPath();
		
		var editor = provider.GetRequiredService<WolfEngineEditor>();
		var editorThread = new Thread(editor.Run) { IsBackground = true, Name = "EditorThread" };
		editorThread.Start();
		
		var renderPipeline = provider.GetRequiredService<IRenderPipeline>();
		renderPipeline.Run(() =>
		{
			if (string.IsNullOrWhiteSpace(lastProjectPath))
			{
				return;
			}

			var projectService = provider.GetRequiredService<IEditorProjectService>();
			projectService.OpenProject(lastProjectPath, out _);

			var gameplayAssemblyHost = provider.GetRequiredService<IGameplayAssemblyHost>();
			gameplayAssemblyHost.EnsureLoaded();
		});
		
		editor.Stop();
		editorThread.Join();
		AssetDatabase.ClearInstanceRegistry();
	}

	private static void ConfigureServices(IServiceCollection services)
	{
		services.AddSingleton<WolfEngineEditor>();
		services.AddSingleton<RigidbodySystem>();
		services.AddSingleton<IEditorPlaySession, EditorPlaySession>();
		services.AddSingleton<EditorCameraContext>();
		services.AddSingleton<FramerateTool>();
		services.AddSingleton<IEditorNotificationService, EditorNotificationService>();
		services.AddSingleton<IEditorInteractionState, EditorInteractionState>();
		services.AddSingleton<IEditorAssetRefreshService, EditorAssetRefreshService>();
		services.AddSingleton<IEditorSceneSnapshotService, EditorSceneSnapshotService>();
		services.AddSingleton<IEditorAssetSnapshotService, EditorAssetSnapshotService>();
		services.AddSingleton<ITerrainTexturePreviewRegistry, TerrainTexturePreviewRegistry>();
		services.AddSingleton<ITerrainAssetPersistenceService, TerrainAssetPersistenceService>();
		services.AddSingleton<IEditorUndoRedoService, EditorUndoRedoService>();
		services.AddSingleton<IEditorCommandService, EditorCommandService>();
		services.AddSingleton<IMaterialTypeRegistry, MaterialTypeRegistry>();
		services.AddSingleton<IGameplayAssemblyHost>(provider => new GameplayAssemblyHost(() => provider.GetRequiredService<IEditorProjectService>()));
		services.AddSingleton<IProjectTypeCatalog>(provider => new ProjectTypeCatalog(
			() => provider.GetRequiredService<IEditorProjectService>(),
			provider.GetRequiredService<IGameplayAssemblyHost>()));
		services.AddSingleton<IProjectTypeResolver>(provider => (IProjectTypeResolver)provider.GetRequiredService<IProjectTypeCatalog>());
		services.AddSingleton<IDataAssetTypeRegistry, DataAssetTypeRegistry>();
		services.AddSingleton<ITextureGpuCompressionService, TextureGpuCompressionService>();
		services.AddSingleton<IProjectAssetPipelineService, ProjectAssetPipelineService>();
		services.AddSingleton<IProjectSceneImporter, ProjectSceneImporter>();
		services.AddSingleton<IEditorSceneFactory, EditorSceneFactory>();
		services.AddSingleton<IEditorSceneWorkspace, EditorSceneWorkspace>();
		services.AddSingleton<IEditorSceneReloadService, EditorSceneReloadService>();
		services.AddSingleton<IAssetInstanceRegistry, EditorAssetInstanceRegistry>();
		services.AddSingleton<IEditorProjectService, EditorProjectService>();
		services.AddSingleton<IAssetSelectionService, AssetSelectionService>();
		services.AddSingleton<ITextureAssetStore, TextureAssetStore>();
		services.AddSingleton<IMaterialAssetStore, MaterialAssetStore>();
		services.AddSingleton<IDataAssetStore, DataAssetStore>();
		services.AddSingleton<IMaterialAssetCreator, MaterialAssetCreator>();
		services.AddSingleton<IDataAssetCreator, DataAssetCreator>();
		services.AddSingleton<ITerrainAssetCreator, TerrainAssetCreator>();
		services.AddSingleton<IPrefabAssetCreator, PrefabAssetCreator>();
		services.AddSingleton<ITextureAssetImporter, TextureAssetImporter>();
		services.AddSingleton<IMaterialImporter, MaterialImporter>();
		services.AddSingleton<IDataAssetRuntimeResolver, DataAssetRuntimeResolver>();
		services.AddSingleton<ITerrainAssetRuntimeResolver, TerrainAssetRuntimeResolver>();
		services.AddSingleton<IMaterialRuntimeAssetResolver, MaterialRuntimeAssetResolver>();
		services.AddSingleton<ITextureRuntimeAssetResolver, TextureRuntimeAssetResolver>();
		services.AddSingleton<IMeshRuntimeAssetResolver, MeshRuntimeAssetResolver>();
		services.AddSingleton<IPropertyDrawerRegistry, PropertyDrawerRegistry>();
		services.AddSingleton<TextureAssetEditor>();
		services.AddSingleton<TerrainAssetEditor>();
		services.AddSingleton<MaterialAssetEditor>();
		services.AddSingleton<DataAssetEditor>();
		services.AddSingleton<SceneAssetEditor>();
		services.AddSingleton<PrefabAssetEditor>();
		services.AddSingleton<IEditorAssetHandler, TextureEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, MaterialEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, DataEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, TerrainEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, SceneEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, PrefabEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandlerRegistry, EditorAssetHandlerRegistry>();
		services.AddSingleton<IEditorModeState, EditorModeState>();
		services.AddSingleton<IMenuBar, MenuBar>();
		services.AddSingleton<IImageLoader, ImageLoader>();
		services.AddSingleton<IAssetThumbnailLoader, AssetThumbnailLoader>();
		services.AddSingleton<IIconManager, IconManager>();
		services.AddSingleton<IGizmoLineRenderer, GizmoLineRenderer>();
		services.AddSingleton<TerrainToolSettingsOverlay>();
		services.AddSingleton<TerrainBrushPreviewDecalController>();
		services.AddSingleton<ITerrainBrushGpuExecutor, TerrainBrushGpuExecutor>();
		services.AddSingleton<ITerrainAuthoringService, TerrainAuthoringService>();
		services.AddSingleton<TerrainToolController>();
		services.AddSingleton<BoxColliderGizmoDrawer>();
		services.AddSingleton<CapsuleColliderGizmoDrawer>();
		services.AddSingleton<TransformGizmoController>();
		services.AddSingleton<EditorGui>();
		
		ConfigureEditorWindows(services);
	}

	private static void ConfigureEditorWindows(IServiceCollection services)
	{
		services.AddSingleton<ComponentsWindow>();
		services.AddSingleton<IComponentEditor>(provider => provider.GetRequiredService<ComponentsWindow>());
		services.AddTransient<EntitiesWindow>();
		services.AddTransient<AssetsWindow>();
		services.AddTransient<AssetEditorWindow>();
		services.AddSingleton<MaterialImporterWindow>();
		services.AddTransient<ProfilerWindow>();
		services.AddTransient<SceneWindow>();
	}
}
