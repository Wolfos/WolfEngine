using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering.UI;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor;

public static class Program
{
	public static void Main()
	{
		var services = new ServiceCollection();
		WolfEngine.ConfigureServices(services);
		
		ConfigureServices(services);
		
		var provider = services.BuildServiceProvider();
		
		AssetDatabase.SetInstanceRegistry(provider.GetRequiredService<IAssetInstanceRegistry>());
		provider.GetRequiredService<IUiFrameProvider>();
		provider.GetRequiredService<IIconManager>();
		EditorPreferences.Load();
		var projectService = provider.GetRequiredService<IEditorProjectService>();
		var lastProjectPath = EditorPreferences.GetLastProjectPath();
		if (string.IsNullOrWhiteSpace(lastProjectPath) == false)
		{
			projectService.OpenProject(lastProjectPath, out _);
		}
		
		var editor = provider.GetRequiredService<WolfEngineEditor>();
		var editorThread = new Thread(editor.Run) { IsBackground = true, Name = "EditorThread" };
		editorThread.Start();
		
		var renderPipeline = provider.GetRequiredService<IRenderPipeline>();
		renderPipeline.Run();
		
		editor.Stop();
		editorThread.Join();
		AssetDatabase.ClearInstanceRegistry();
	}

	private static void ConfigureServices(IServiceCollection services)
	{
		services.AddSingleton<WolfEngineEditor>();
		services.AddSingleton<IEditorPlaySession, EditorPlaySession>();
		services.AddSingleton<EditorCameraContext>();
		services.AddSingleton<FramerateTool>();
		services.AddSingleton<IEditorNotificationService, EditorNotificationService>();
		services.AddSingleton<IEditorInteractionState, EditorInteractionState>();
		services.AddSingleton<IEditorSceneSnapshotService, EditorSceneSnapshotService>();
		services.AddSingleton<IEditorAssetSnapshotService, EditorAssetSnapshotService>();
		services.AddSingleton<IEditorUndoRedoService, EditorUndoRedoService>();
		services.AddSingleton<IEditorCommandService, EditorCommandService>();
		services.AddSingleton<IMaterialTypeRegistry, MaterialTypeRegistry>();
		services.AddSingleton<IGameplayAssemblyHost>(provider => new GameplayAssemblyHost(() => provider.GetRequiredService<IEditorProjectService>()));
		services.AddSingleton<IProjectTypeCatalog>(provider => new ProjectTypeCatalog(
			() => provider.GetRequiredService<IEditorProjectService>(),
			provider.GetRequiredService<IGameplayAssemblyHost>()));
		services.AddSingleton<IProjectTypeResolver>(provider => (IProjectTypeResolver)provider.GetRequiredService<IProjectTypeCatalog>());
		services.AddSingleton<IDataAssetTypeRegistry, DataAssetTypeRegistry>();
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
		services.AddSingleton<ITextureAssetImporter, TextureAssetImporter>();
		services.AddSingleton<IMaterialImporter, MaterialImporter>();
		services.AddSingleton<IDataAssetRuntimeResolver, DataAssetRuntimeResolver>();
		services.AddSingleton<IMaterialRuntimeAssetResolver, MaterialRuntimeAssetResolver>();
		services.AddSingleton<ITextureRuntimeAssetResolver, TextureRuntimeAssetResolver>();
		services.AddSingleton<IMeshRuntimeAssetResolver, MeshRuntimeAssetResolver>();
		services.AddSingleton<IPropertyDrawerRegistry, PropertyDrawerRegistry>();
		services.AddSingleton<TextureAssetEditor>();
		services.AddSingleton<MaterialAssetEditor>();
		services.AddSingleton<DataAssetEditor>();
		services.AddSingleton<SceneAssetEditor>();
		services.AddSingleton<IEditorAssetHandler, TextureEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, MaterialEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, DataEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandler, SceneEditorAssetHandler>();
		services.AddSingleton<IEditorAssetHandlerRegistry, EditorAssetHandlerRegistry>();
		services.AddSingleton<IEditorModeState, EditorModeState>();
		services.AddSingleton<IMenuBar, MenuBar>();
		services.AddSingleton<IImageLoader, ImageLoader>();
		services.AddSingleton<IIconManager, IconManager>();
		services.AddSingleton<IGizmoLineRenderer, GizmoLineRenderer>();
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
