using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;
using WolfEngine.Editor.UI;
using WolfEngine.Physics;
using WolfEngine.Editor.Automation;
using WolfEngine.Audio;

namespace WolfEngine.Editor;

public static class Program
{
	public static void Main(string[] args)
	{
		using var application = EditorApplication.Create();

		if (EditorAutomationOptions.TryParse(args, out var automationOptions, out var parseError) == false)
		{
			Console.Out.WriteLine($"Args not recognised {parseError}");
			application.Run();
			return;
		}

		var captureController = automationOptions is null ? null : application.CreateCaptureController(automationOptions);
		application.Run(captureController);
		if (captureController is not null) Environment.ExitCode = captureController.ExitCode;
	}

	public static void ConfigureServices(IServiceCollection services)
	{
		services.AddSingleton<WolfEngineEditor>();
		services.AddSingleton<RigidbodySystem>();
		services.AddSingleton<IEditorPlaySession, EditorPlaySession>();
		services.AddSingleton<EditorCameraContext>();
		services.AddSingleton<EditorCameraSystem>();
		services.AddSingleton<FramerateTool>();
		services.AddSingleton<IEditorNotificationService, EditorNotificationService>();
		services.AddSingleton<IEditorOperationService, EditorOperationService>();
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
		services.AddSingleton<IGameBuildService, GameBuildService>();
		services.AddSingleton<IAssetSelectionService, AssetSelectionService>();
		services.AddSingleton<ITextureAssetStore, TextureAssetStore>();
		services.AddSingleton<IMaterialAssetStore, MaterialAssetStore>();
		services.AddSingleton<IDataAssetStore, DataAssetStore>();
		services.AddSingleton<IMaterialAssetCreator, MaterialAssetCreator>();
		services.AddSingleton<IDataAssetCreator, DataAssetCreator>();
		services.AddSingleton<ITerrainAssetCreator, TerrainAssetCreator>();
		services.AddSingleton<IPrefabAssetCreator, PrefabAssetCreator>();
		services.AddSingleton<ITextureAssetImporter, TextureAssetImporter>();
		services.AddSingleton<IAudioAssetImporter, AudioAssetImporter>();
		services.AddSingleton<EditorAudioContentProvider>();
		services.AddSingleton<IAudioContentProvider>(provider => provider.GetRequiredService<EditorAudioContentProvider>());
		services.AddSingleton<AudioService>();
		services.AddSingleton<IAudioService>(provider => provider.GetRequiredService<AudioService>());
		services.AddSingleton<IAudioRuntime>(provider => provider.GetRequiredService<AudioService>());
		services.AddSingleton<IAudioClipRuntimeResolver, AudioClipRuntimeResolver>();
		services.AddSingleton<IMaterialImporter, MaterialImporter>();
		services.AddSingleton<IDataAssetRuntimeResolver, DataAssetRuntimeResolver>();
		services.AddSingleton<ITerrainAssetRuntimeResolver, TerrainAssetRuntimeResolver>();
		services.AddSingleton<IMaterialRuntimeAssetResolver, MaterialRuntimeAssetResolver>();
		services.AddSingleton<ITextureRuntimeAssetResolver, TextureRuntimeAssetResolver>();
		services.AddSingleton<IMeshRuntimeAssetResolver, MeshRuntimeAssetResolver>();
		services.AddSingleton<ISkeletonRuntimeAssetResolver, SkeletonRuntimeAssetResolver>();
		services.AddSingleton<IAnimationClipRuntimeAssetResolver, AnimationClipRuntimeAssetResolver>();
		services.AddSingleton<IPropertyDrawerRegistry, PropertyDrawerRegistry>();
		services.AddSingleton<TextureAssetEditor>();
		services.AddSingleton<AudioAssetEditor>();
		services.AddSingleton<TerrainAssetEditor>();
		services.AddSingleton<MaterialAssetEditor>();
		services.AddSingleton<DataAssetEditor>();
		services.AddSingleton<SceneAssetEditor>();
		services.AddSingleton<PrefabAssetEditor>();
		services.AddSingleton<ModelAssetEditor>();
		services.AddSingleton<IEditorAssetHandler, TextureEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, AudioEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, MaterialEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, DataEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, TerrainEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, SceneEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, PrefabEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, ModelEditorAssetHandler>();
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
		services.AddSingleton<SphereColliderGizmoDrawer>();
		services.AddSingleton<CapsuleColliderGizmoDrawer>();
		services.AddSingleton<TransformGizmoController>();
		services.AddSingleton<EditorGui>();
		services.AddSingleton<ProjectSettingsWindow>();
		
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
