using System;
using System.Collections.Generic;
using System.Numerics;
using NSubstitute;
using WolfEngine;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorAssetRefreshServiceTests
{
	[TearDown]
	public void TearDown()
	{
		EditorGui.ClearEntitySelection();
		AssetDatabase.ClearInstanceRegistry();
	}

	[Test]
	public void RefreshOpenSceneAssets_RebindsRuntimeAssetsAndPreservesSelectionAndUnsavedState()
	{
		var worldManager = new WorldManager();
		var workspace = new EditorSceneWorkspace(Substitute.For<IEditorSceneFactory>(), worldManager);
		var reloadService = new EditorSceneReloadService(new TestTypeResolver());
		var registry = new TestAssetInstanceRegistry();
		AssetDatabase.SetInstanceRegistry(registry);

		var meshAssetId = Guid.NewGuid();
		var initialMesh = CreateMesh(new Vector4(0, 0, 0, 1));
		var refreshedMesh = CreateMesh(new Vector4(2, 0, 0, 1));
		registry.Register(meshAssetId, initialMesh);

		var scene = CreateScene(worldManager, meshAssetId, initialMesh, out var entity, out var entityId);
		workspace.Initialize(scene);
		EditorGui.SelectEntity(entity, scene.World, requestFocus: false);

		var projectService = new RefreshTrackingProjectService(() => registry.Register(meshAssetId, refreshedMesh));
		var service = new EditorAssetRefreshService(projectService, workspace, reloadService);

		service.RefreshOpenSceneAssets();

		Assert.That(projectService.ReloadCalls, Is.EqualTo(1));
		Assert.That(workspace.CurrentScene.World, Is.Not.SameAs(scene.World));
		Assert.That(workspace.CurrentScene.World.IsAlive(EditorGui.SelectedEntity), Is.True);
		Assert.That(workspace.CurrentScene.EntityIds[EditorGui.SelectedEntity], Is.EqualTo(entityId));

		var reloadedEntity = EditorGui.SelectedEntity;
		var meshRenderer = workspace.CurrentScene.World.GetComponent<MeshRenderer>(reloadedEntity);
		var refreshComponent = workspace.CurrentScene.World.GetComponent<RefreshTestComponent>(reloadedEntity);
		Assert.That(meshRenderer.Mesh, Is.SameAs(refreshedMesh));
		Assert.That(meshRenderer.MeshAsset.NodeId, Is.EqualTo(meshAssetId));
		Assert.That(refreshComponent.Count, Is.EqualTo(7));
	}

	private static EditorScene CreateScene(
		WorldManager worldManager,
		Guid meshAssetId,
		Mesh initialMesh,
		out Entity entity,
		out Guid entityId)
	{
		var world = worldManager.CreateWorld(WorldTag.Authoring);
		var scene = new EditorScene
		{
			World = world,
			EntityIcons = new Dictionary<Entity, string>(),
			EntityIds = new Dictionary<Entity, Guid>(),
			EntityCellKeys = new Dictionary<Entity, SceneCellKey>(),
			SpatialCells = new Dictionary<Int2, Cell>(),
			GlobalCell = new Cell(),
			EntityPrefabSourcePaths = new Dictionary<Entity, List<SavedPrefabLink>>()
		};

		entity = world.CreateEntity("Mesh Entity");
		world.AddComponent(entity, new MeshRenderer
		{
			MeshAsset = new AssetRef<Mesh> { NodeId = meshAssetId },
			Mesh = initialMesh
		});
		world.AddComponent(entity, new RefreshTestComponent { Count = 7 });

		entityId = Guid.NewGuid();
		scene.EntityIds[entity] = entityId;
		scene.EntityCellKeys[entity] = SceneCellKey.Global;
		return scene;
	}

	private static Mesh CreateMesh(Vector4 offset)
	{
		return new Mesh(
			[
				new Vector4(0, 0, 0, 1) + offset,
				new Vector4(1, 0, 0, 1) + offset,
				new Vector4(0, 1, 0, 1) + offset
			],
			[0u, 1u, 2u]);
	}

	private struct RefreshTestComponent : IEntityComponent
	{
		public int Count;
	}

	private sealed class RefreshTrackingProjectService : IEditorProjectService
	{
		private readonly Action _beforeReload;

		public RefreshTrackingProjectService(Action beforeReload)
		{
			_beforeReload = beforeReload;
		}

		public int ReloadCalls { get; private set; }
		public bool HasOpenProject => true;
		public string? ProjectRootPath => string.Empty;
		public string? AssetsPath => string.Empty;
		public string? LibraryPath => string.Empty;
		public string? DatabasePath => string.Empty;
		public string? GameplayProjectRelativePath => string.Empty;
		public string? GameplayProjectPath => string.Empty;
		public AssetDatabase CurrentAssetDatabase { get; } = new();

		public bool CreateProject(string parentFolder, string projectName, out string errorMessage) => throw new NotSupportedException();
		public bool OpenProject(string projectRoot, out string errorMessage) => throw new NotSupportedException();
		public void CloseProject() => throw new NotSupportedException();

		public AssetDatabaseRefreshResult ReloadAssetDatabase()
		{
			ReloadCalls++;
			_beforeReload();
			return AssetDatabaseRefreshResult.Empty;
		}

		public void ReloadAssetDatabaseFromIndex() => throw new NotSupportedException();
		public void RefreshAssetSource(string relativeSourcePath) => throw new NotSupportedException();
		public void SaveAssetDatabase(AssetDatabase database) => throw new NotSupportedException();
		public AssetDatabase CloneCurrentAssetDatabase() => throw new NotSupportedException();
		public bool TryGetAsset(Guid assetId, out AssetDatabaseEntry asset) => throw new NotSupportedException();
		public string GetAbsolutePath(string relativePath) => throw new NotSupportedException();
		public void DeleteAssetSource(string relativeSourcePath) => throw new NotSupportedException();
		public void DeleteFolder(string relativeFolderPath) => throw new NotSupportedException();
	}

	private sealed class TestAssetInstanceRegistry : IAssetInstanceRegistry
	{
		private readonly Dictionary<Guid, object> _instances = new();

		public void Register(Guid assetId, object instance)
		{
			_instances[assetId] = instance;
		}

		public object? GetInstance(Guid assetId, Type expectedType)
		{
			if (_instances.TryGetValue(assetId, out var instance) == false)
			{
				return null;
			}

			return expectedType.IsInstanceOfType(instance) ? instance : null;
		}

		public void RefreshProject(string projectRootPath, AssetDatabase database)
		{
		}

		public void InvalidateAssets(IEnumerable<Guid> assetIds)
		{
			foreach (var assetId in assetIds)
			{
				_instances.Remove(assetId);
			}
		}

		public void ClearCachedInstances()
		{
			_instances.Clear();
		}

		public void Clear()
		{
			_instances.Clear();
		}
	}

	private sealed class TestTypeResolver : IProjectTypeResolver
	{
		public string GetTypeName(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

		public string GetStableTypeId(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

		public bool TryResolveType(string typeName, out Type type)
		{
			type = Type.GetType(typeName, throwOnError: false)!;
			return type is not null;
		}

		public bool TryResolveStableTypeId(string stableTypeId, out Type type)
		{
			type = Type.GetType(stableTypeId, throwOnError: false)!;
			return type is not null;
		}
	}
}
