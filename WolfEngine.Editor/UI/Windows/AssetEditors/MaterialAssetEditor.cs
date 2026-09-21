using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class MaterialAssetEditor
{
	private readonly IEditorProjectService _projectService;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IMaterialTypeRegistry _materialTypeRegistry;
	private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
	private readonly IEditorAssetSnapshotService _assetSnapshotService;
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly IIconManager _icons;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IMaterialFactory _materialFactory;
	private readonly ITextureFactory _textureFactory;
	private readonly IRenderViewHost _viewHost;
	private readonly RenderGraph _renderGraph;
	private readonly IWorldManager _worldManager;
	private readonly EditorRenderViews _renderViews;
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly Dictionary<string, Material> _previewMaterials = new(StringComparer.Ordinal);
	private MaterialPreviewScene? _preview;
	private bool _hostVisible = true;
	private bool _readOnly;
	private MaterialAsset? _loadedMaterialAsset;
	private Guid? _loadedMaterialAssetId;
	private long _loadedAssetDatabaseRevision = -1;
	private AssetDatabaseEntry? _loadedAssetEntry;
	private EditorAssetFileSnapshot? _pendingBeforeSnapshot;
	private bool _hasPendingChanges;

	public MaterialAssetEditor(
		IEditorProjectService projectService,
		IMaterialAssetStore materialAssetStore,
		IMaterialTypeRegistry materialTypeRegistry,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		IEditorAssetSnapshotService assetSnapshotService,
		IEditorUndoRedoService undoRedoService,
		IIconManager icons,
		IAssetSelectionService assetSelectionService,
		IMaterialFactory materialFactory,
		ITextureFactory textureFactory,
		IRenderViewHost viewHost,
		RenderGraph renderGraph,
		IWorldManager worldManager,
		EditorRenderViews renderViews,
		EditorViewportStateBus viewportStateBus)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_materialTypeRegistry = materialTypeRegistry ?? throw new ArgumentNullException(nameof(materialTypeRegistry));
		_propertyDrawerRegistry = propertyDrawerRegistry ?? throw new ArgumentNullException(nameof(propertyDrawerRegistry));
		_assetSnapshotService = assetSnapshotService ?? throw new ArgumentNullException(nameof(assetSnapshotService));
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
		_icons = icons ?? throw new ArgumentNullException(nameof(icons));
		_assetSelectionService = assetSelectionService ?? throw new ArgumentNullException(nameof(assetSelectionService));
		_materialFactory = materialFactory ?? throw new ArgumentNullException(nameof(materialFactory));
		_textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
		_viewHost = viewHost ?? throw new ArgumentNullException(nameof(viewHost));
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
		_worldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
		_renderViews = renderViews ?? throw new ArgumentNullException(nameof(renderViews));
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
	}

	public void SetHostVisible(bool visible)
	{
		_hostVisible = visible;
		if (!visible && _preview is { } preview)
			_viewportStateBus.PublishUiState(preview.View, SceneViewportUiState.Hidden);
	}

	public void SetReadOnly(bool readOnly) => _readOnly = readOnly;

	public void HidePreview()
	{
		_preview?.Dispose();
		_preview = null;
	}

	public void Draw(AssetDatabaseEntry asset)
	{
		if (_loadedMaterialAssetId.HasValue && _loadedMaterialAssetId.Value != asset.Id)
		{
			CommitPendingChanges();
		}
		if (asset.IsGenerated)
		{
			var runtimeMaterial = new AssetRef<Material> { NodeId = asset.Id }.Asset;
			if (runtimeMaterial is not null) DrawPreview(runtimeMaterial);
			else
			{
				HidePreview();
				ImGui.TextDisabled("Material preview unavailable.");
			}
			ImGui.TextUnformatted("Generated material");
			ImGui.TextDisabled("This material was produced from an imported 3D source and is read-only.");
			return;
		}

		var materialAsset = EnsureMaterialAssetLoaded(asset);
		if (materialAsset is null)
		{
			HidePreview();
			ImGui.TextUnformatted("Failed to load material asset.");
			return;
		}
		var previewMaterial = SyncPreviewMaterial(materialAsset);
		DrawPreview(previewMaterial);
		if (_readOnly) ImGui.BeginDisabled();

		var descriptors = _materialTypeRegistry.GetAll();
		EditorUIUtility.Combo("Material Type", materialAsset.MaterialType.ToString(), () =>
		{
			for (var i = 0; i < descriptors.Count; i++)
			{
				var descriptor = descriptors[i];
				var isSelected = descriptor.Type == materialAsset.MaterialType;
				if (ImGui.Selectable(descriptor.DisplayName, isSelected))
				{
					BeginPendingChange(asset);
					materialAsset.MaterialType = descriptor.Type;
					_hasPendingChanges = true;
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		var properties = materialAsset.GetActiveProperties();
		var propertyDefinitions = _materialTypeRegistry.GetPropertiesForMaterialType(materialAsset.MaterialType);
		DrawBaseColorEditor(asset, materialAsset, properties);
		DrawFloatEditor("Metallic", properties.MetallicFactor, value =>
		{
			BeginPendingChange(asset);
			properties.MetallicFactor = value;
			_hasPendingChanges = true;
		});
		DrawFloatEditor("Roughness", properties.RoughnessFactor, value =>
		{
			BeginPendingChange(asset);
			properties.RoughnessFactor = value;
			_hasPendingChanges = true;
		});
		DrawFloatEditor("Normal Strength", properties.NormalScale, value =>
		{
			BeginPendingChange(asset);
			properties.NormalScale = value;
			_hasPendingChanges = true;
		});
		DrawEmissiveFactorEditor(asset, properties);
		DrawFloatEditor("Emissive Intensity", properties.EmissiveIntensity, value =>
		{
			BeginPendingChange(asset);
			properties.EmissiveIntensity = MathF.Max(0.0f, value);
			_hasPendingChanges = true;
		});

		if (HasProperty(propertyDefinitions, MaterialPropertyKind.AlphaCutoff))
		{
			var alphaCutoff = materialAsset.AlphaCutoff;
			DrawFloatEditor("Alpha Cutoff", alphaCutoff, value =>
			{
				BeginPendingChange(asset);
				materialAsset.AlphaCutoff = value;
				_hasPendingChanges = true;
			});
		}

		ImGui.Separator();
		ImGui.TextUnformatted("Textures");
		DrawTextureAssignmentEditor(asset, properties.Textures, nameof(MaterialTextureAssignments.Albedo), "Albedo", properties.Textures.Albedo);
		DrawTextureAssignmentEditor(asset, properties.Textures, nameof(MaterialTextureAssignments.Orm), "ORM", properties.Textures.Orm);
		DrawTextureAssignmentEditor(asset, properties.Textures, nameof(MaterialTextureAssignments.Normal), "Normal", properties.Textures.Normal);
		DrawTextureAssignmentEditor(asset, properties.Textures, nameof(MaterialTextureAssignments.Emissive), "Emissive", properties.Textures.Emissive);
		SyncPreviewMaterial(materialAsset);

		if (_hasPendingChanges && ImGui.IsAnyItemActive() == false)
		{
			CommitPendingChanges();
		}
		if (_readOnly) ImGui.EndDisabled();
	}

	private Material SyncPreviewMaterial(MaterialAsset asset)
	{
		var descriptor = _materialTypeRegistry.GetDescriptor(asset.MaterialType);
		if (!_previewMaterials.TryGetValue(descriptor.ShaderPath, out var material))
		{
			// The ordinary material factory allocates the same GPU resources as a scene MeshRenderer.
			material = _materialFactory.GetMaterial(descriptor.ShaderPath, ColorRGBA.White);
			_previewMaterials.Add(descriptor.ShaderPath, material);
		}
		var properties = asset.GetActiveProperties();
		var albedo = ResolveTexture(properties.Textures.Albedo, _textureFactory.GetWhiteTexture());
		var orm = ResolveTexture(properties.Textures.Orm, _textureFactory.GetWhiteTexture());
		var normal = ResolveTexture(properties.Textures.Normal, _textureFactory.GetNeutralNormalTexture());
		var emissive = ResolveTexture(properties.Textures.Emissive, _textureFactory.GetWhiteTexture());
		var changed = !material.Color.Equals(properties.BaseColor) ||
		              material.MetallicFactor != properties.MetallicFactor ||
		              material.RoughnessFactor != properties.RoughnessFactor ||
		              material.NormalScale != properties.NormalScale ||
		              material.EmissiveFactor != properties.EmissiveFactor ||
		              material.EmissiveIntensity != properties.EmissiveIntensity ||
		              !ReferenceEquals(material.AlbedoTexture, albedo) ||
		              !ReferenceEquals(material.OrmTexture, orm) ||
		              !ReferenceEquals(material.NormalTexture, normal) ||
		              !ReferenceEquals(material.EmissiveTexture, emissive) ||
		              material.AlphaMode != descriptor.RuntimeAlphaMode ||
		              material.AlphaCutoff != asset.AlphaCutoff;
		material.Color = properties.BaseColor;
		material.MetallicFactor = properties.MetallicFactor;
		material.RoughnessFactor = properties.RoughnessFactor;
		material.NormalScale = properties.NormalScale;
		material.EmissiveFactor = properties.EmissiveFactor;
		material.EmissiveIntensity = properties.EmissiveIntensity;
		material.AlbedoTexture = albedo;
		material.OrmTexture = orm;
		material.NormalTexture = normal;
		material.EmissiveTexture = emissive;
		material.AlphaMode = descriptor.RuntimeAlphaMode;
		material.AlphaCutoff = asset.AlphaCutoff;
		if (changed) _renderGraph.EnsureMaterialResources(material);
		return material;
	}

	private static Texture ResolveTexture(AssetRef<Texture> reference, Texture fallback) =>
		reference.NodeId == Guid.Empty ? fallback : reference.Asset ?? fallback;

	private void DrawPreview(Material material)
	{
		_preview ??= new MaterialPreviewScene(_viewHost, _worldManager, _renderViews, _viewportStateBus,
			new DebugPrimitiveMeshFactory().GetMesh(DebugPrimitiveType.Sphere));
		_preview.SetMaterial(material);
		var availableWidth = MathF.Max(0.0f, ImGui.GetContentRegionAvail().X);
		var size = MathF.Min(availableWidth, 256.0f);
		if (size <= 0.0f)
		{
			_viewportStateBus.PublishUiState(_preview.View, SceneViewportUiState.Hidden);
			return;
		}
		if (availableWidth > size) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availableWidth - size) * 0.5f);
		var imageMin = ImGui.GetCursorScreenPos();
		ImGui.Image(UiTextureIds.Viewport(_preview.View), new Vector2(size, size));
		var scale = ImGui.GetIO().DisplayFramebufferScale;
		var pixels = new Int2(Math.Max(0, (int)MathF.Round(size * scale.X)),
			Math.Max(0, (int)MathF.Round(size * scale.Y)));
		_viewportStateBus.PublishUiState(_preview.View, new SceneViewportUiState(
			_hostVisible && !ImGui.IsWindowCollapsed() && pixels.X > 0 && pixels.Y > 0,
			pixels, 1.0f, SceneDebugViewIds.FinalColor,
			ImGui.IsItemHovered(), ImGui.IsWindowFocused(),
			pointerAvailable: false, pointerCaptured: false, rightMousePressStartedHere: false,
			imageMin, imageMin + new Vector2(size, size)));
	}

	private MaterialAsset? EnsureMaterialAssetLoaded(AssetDatabaseEntry asset)
	{
		if (_loadedMaterialAssetId == asset.Id && _loadedMaterialAsset is not null &&
		    _loadedAssetDatabaseRevision == _projectService.AssetDatabaseRevision)
		{
			_loadedAssetEntry = asset;
			return _loadedMaterialAsset;
		}

		try
		{
			_hasPendingChanges = false;
			_pendingBeforeSnapshot = null;
			_loadedMaterialAssetId = asset.Id;
			_loadedAssetEntry = asset;
			_loadedMaterialAsset = _materialAssetStore.LoadAsset(
				_projectService.GetAbsoluteAssetPath(asset.Id, asset.RelativeAssetPath));
			_loadedAssetDatabaseRevision = _projectService.AssetDatabaseRevision;
			return _loadedMaterialAsset;
		}
		catch
		{
			_loadedMaterialAssetId = asset.Id;
			_loadedAssetEntry = asset;
			_loadedMaterialAsset = null;
			_loadedAssetDatabaseRevision = _projectService.AssetDatabaseRevision;
			return null;
		}
	}

	private void DrawBaseColorEditor(AssetDatabaseEntry asset, MaterialAsset materialAsset, MaterialSurfaceProperties properties)
	{
		var drawResult = _propertyDrawerRegistry.Draw(CreatePropertyDrawerContext(
			"Base Color",
			typeof(ColorRGBA),
			properties.BaseColor));
		if (drawResult.Changed && drawResult.Value is ColorRGBA color)
		{
			BeginPendingChange(asset);
			properties.BaseColor = color;
			_hasPendingChanges = true;
		}
	}

	private void DrawEmissiveFactorEditor(AssetDatabaseEntry asset, MaterialSurfaceProperties properties)
	{
		var emissiveColor = properties.EmissiveFactor;
		if (EditorUIUtility.ColorEdit3("Emissive Factor", ref emissiveColor))
		{
			BeginPendingChange(asset);
			properties.EmissiveFactor = emissiveColor;
			_hasPendingChanges = true;
		}
	}

	private void DrawFloatEditor(string label, float currentValue, Action<float> setter)
	{
		var drawResult = _propertyDrawerRegistry.Draw(CreatePropertyDrawerContext(label, typeof(float), currentValue));
		if (drawResult.Changed && drawResult.Value is float value)
		{
			setter(value);
		}
	}

	private void DrawTextureAssignmentEditor(
		AssetDatabaseEntry materialEntry,
		MaterialTextureAssignments assignments,
		string propertyName,
		string label,
		AssetRef<Texture> currentValue)
	{
		var drawResult = _propertyDrawerRegistry.Draw(CreatePropertyDrawerContext(label, typeof(AssetRef<Texture>), currentValue));
		if (drawResult.Changed && drawResult.Value is AssetRef<Texture> textureReference)
		{
			BeginPendingChange(materialEntry);
			SetTextureAssignment(assignments, propertyName, textureReference.NodeId);
			_hasPendingChanges = true;
		}
	}

	private PropertyDrawerContext CreatePropertyDrawerContext(string label, Type valueType, object? value)
	{
		return new PropertyDrawerContext(
			label,
			valueType,
			value,
			AssetLinkSelectionButton: new AssetLinkSelectionButton(
				_icons.Get("search"),
				assetId => _assetSelectionService.Select(assetId)));
	}

	private static void SetTextureAssignment(MaterialTextureAssignments assignments, string propertyName, Guid value)
	{
		var reference = new AssetRef<Texture> { NodeId = value };
		switch (propertyName)
		{
			case nameof(MaterialTextureAssignments.Albedo):
				assignments.Albedo = reference;
				break;
			case nameof(MaterialTextureAssignments.Orm):
				assignments.Orm = reference;
				break;
			case nameof(MaterialTextureAssignments.Normal):
				assignments.Normal = reference;
				break;
			case nameof(MaterialTextureAssignments.Emissive):
				assignments.Emissive = reference;
				break;
			default:
				throw new InvalidOperationException($"Unknown material texture assignment '{propertyName}'.");
		}
	}

	private static bool HasProperty(IReadOnlyList<MaterialPropertyDefinition> definitions, MaterialPropertyKind kind)
	{
		for (var i = 0; i < definitions.Count; i++)
		{
			if (definitions[i].Kind == kind)
			{
				return true;
			}
		}

		return false;
	}

	private void BeginPendingChange(AssetDatabaseEntry asset)
	{
		if (_pendingBeforeSnapshot.HasValue)
		{
			return;
		}

		_pendingBeforeSnapshot = _assetSnapshotService.CaptureMaterialAssetSnapshot(asset);
	}

	private void CommitPendingChanges()
	{
		if (_hasPendingChanges == false || _pendingBeforeSnapshot is not { } before || _loadedAssetEntry is null || _loadedMaterialAsset is null)
		{
			_hasPendingChanges = false;
			_pendingBeforeSnapshot = null;
			return;
		}

		var after = _assetSnapshotService.CaptureMaterialAssetSnapshot(_loadedAssetEntry, _loadedMaterialAsset);
		if (string.Equals(before.Json, after.Json, StringComparison.Ordinal))
		{
			_hasPendingChanges = false;
			_pendingBeforeSnapshot = null;
			return;
		}

		_assetSnapshotService.SaveMaterialAsset(_loadedAssetEntry, _loadedMaterialAsset);
		_undoRedoService.BeginCapture("Edit Material Asset");
		_undoRedoService.CommitCapture(new MaterialAssetEditUndoRedoEntry("Edit Material Asset", before, after));
		_hasPendingChanges = false;
		_pendingBeforeSnapshot = null;
	}
}
