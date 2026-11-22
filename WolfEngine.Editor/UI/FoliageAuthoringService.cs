using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class FoliageAuthoringService
{
	private readonly TerrainFoliageAssetService _assets;
	private readonly IEditorUndoRedoService _history;
	private readonly IEditorInteractionState _interactionState;
	private Stroke? _stroke;

	public FoliageAuthoringService(
		TerrainFoliageAssetService assets,
		IEditorUndoRedoService history,
		IEditorInteractionState interactionState)
	{
		_assets = assets;
		_history = history;
		_interactionState = interactionState;
	}

	internal bool IsStrokeOn(EditorScene scene, Entity entity) => _stroke?.Scene == scene && _stroke.Entity == entity;
	internal Guid? AssetId => _stroke?.AssetId;
	internal int InstanceCount => _stroke?.Foliage.InstanceCount ?? 0;

	internal bool BeginStroke(
		EditorScene scene,
		Entity entity,
		FoliageBrushSettings settings,
		IReadOnlyList<FoliagePaletteEntry> palette,
		int? seed = null)
	{
		EndStroke();
		settings.Validate();

		if (scene.World.IsAlive(entity) == false ||
		    scene.World.HasComponent<TerrainComponent>(entity) == false ||
		    scene.EntityIds.TryGetValue(entity, out var entityId) == false)
		{
			return false;
		}

		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(entity);
		var foliage = terrain.Foliage.Asset;
		if (terrain.Foliage.IsValid && (foliage is null || _assets.CanEdit(terrain.Foliage.NodeId) == false))
		{
			return false;
		}

		var surface = TerrainRuntimeRegistry.GetOrCreateRuntime(scene.World, entity);
		if (surface.EnsureSampling(terrain) == false) return false;

		_stroke = new Stroke(
			scene,
			entity,
			entityId,
			terrain.Foliage.NodeId,
			foliage ?? new TerrainFoliage(),
			surface,
			settings with { },
			palette.ToArray(),
			seed.HasValue ? new Random(seed.Value) : new Random());

		return true;
	}

	internal FoliageStampResult AppendStamp(Vector2 localPosition, TerrainBrushModifierState modifiers)
	{
		var stroke = _stroke;
		if (stroke is null ||
		    float.IsFinite(localPosition.X) == false ||
		    float.IsFinite(localPosition.Y) == false)
		{
			return default;
		}

		var world = stroke.Scene.World;
		if (world.IsAlive(stroke.Entity) == false || world.HasComponent<TerrainComponent>(stroke.Entity) == false)
		{
			EndStroke();
			return default;
		}

		ref var terrain = ref world.GetComponent<TerrainComponent>(stroke.Entity);
		if (terrain.Foliage.NodeId != stroke.AssetId ||
		    stroke.Surface.TrySampleLocalSurface(localPosition.X, localPosition.Y, out _, out _) == false)
		{
			return default;
		}

		var brush = new FoliageBrush(stroke.Foliage, stroke.Settings, stroke.Palette, stroke.Random);
		var result = modifiers.Invert ? brush.Erase(localPosition) : brush.Paint(localPosition, stroke.Surface);
		if (brush.Cells.Count == 0) return result;

		if (stroke.AssetId == Guid.Empty)
		{
			var created = _assets.Create(terrain.TerrainAsset.NodeId);
			var reference = new AssetRef<TerrainFoliage> { NodeId = created.Id };
			try
			{
				stroke.Foliage = reference.Asset
					?? throw new InvalidOperationException("The created foliage asset could not be loaded.");
			}
			catch
			{
				_assets.Delete(created);
				throw;
			}

			stroke.CreatedAsset = created;
			stroke.AssetId = created.Id;
			terrain.Foliage = reference;
		}

		if (brush.AddedTypes.Count > 0)
		{
			stroke.BeforeTypes ??= stroke.Foliage.Types.ToArray();
			stroke.Foliage.Types.AddRange(brush.AddedTypes);
		}

		foreach (var (key, instances) in brush.Cells)
		{
			if (stroke.BeforeCells.ContainsKey(key) == false)
			{
				stroke.BeforeCells.Add(key, stroke.Foliage.GetCell(key.X, key.Z));
			}

			stroke.Foliage.ReplaceCell(key.X, key.Z, instances);
		}

		stroke.Foliage.MarkChanged();
		return result;
	}

	internal bool EndStroke()
	{
		var stroke = _stroke;
		_stroke = null;
		if (stroke is null || stroke.BeforeCells.Count == 0) return false;

		var afterCells = stroke.BeforeCells.Keys.ToDictionary(key => key, key => stroke.Foliage.GetCell(key.X, key.Z));
		var before = new FoliageSnapshot(stroke.BeforeCells, stroke.BeforeTypes);
		var after = new FoliageSnapshot(afterCells, stroke.BeforeTypes is null ? null : stroke.Foliage.Types.ToArray());
		var edit = new FoliageStrokeUndoRedoEntry(_assets, stroke.AssetId, stroke.EntityId, stroke.CreatedAsset, before, after);
		_history.BeginCapture(edit.Description);
		_history.CommitCapture(edit);
		_assets.MarkDirty(stroke.AssetId, stroke.Foliage);
		_interactionState.MarkSceneDirty();

		return true;
	}

	private sealed class Stroke(
		EditorScene scene,
		Entity entity,
		Guid entityId,
		Guid assetId,
		TerrainFoliage foliage,
		TerrainRuntimeData surface,
		FoliageBrushSettings settings,
		FoliagePaletteEntry[] palette,
		Random random)
	{
		public EditorScene Scene { get; } = scene;
		public Entity Entity { get; } = entity;
		public Guid EntityId { get; } = entityId;
		public Guid AssetId { get; set; } = assetId;
		public TerrainFoliage Foliage { get; set; } = foliage;
		public TerrainRuntimeData Surface { get; } = surface;
		public FoliageBrushSettings Settings { get; } = settings;
		public FoliagePaletteEntry[] Palette { get; } = palette;
		public Random Random { get; } = random;
		public CreatedTerrainFoliageAsset? CreatedAsset { get; set; }
		public Dictionary<(int X, int Z), TerrainFoliageCell?> BeforeCells { get; } = new();
		public AssetRef<FoliageType>[]? BeforeTypes { get; set; }
	}
}

internal sealed record FoliageSnapshot(
	IReadOnlyDictionary<(int X, int Z), TerrainFoliageCell?> Cells,
	AssetRef<FoliageType>[]? Types)
{
	public void Apply(TerrainFoliage foliage)
	{
		if (Types is not null)
		{
			foliage.Types = [.. Types];
		}

		foreach (var (key, cell) in Cells)
		{
			foliage.RestoreCell(key.X, key.Z, cell);
		}

		foliage.MarkChanged();
	}
}

internal sealed class FoliageStrokeUndoRedoEntry(
	TerrainFoliageAssetService assets,
	Guid assetId,
	Guid entityId,
	CreatedTerrainFoliageAsset? created,
	FoliageSnapshot before,
	FoliageSnapshot after) : IEditorUndoRedoEntry
{
	public string Description => "Foliage Stroke";
	public EditorUndoRedoScope Scope => EditorUndoRedoScope.AuthoringScene;

	public void Undo(EditorUndoRedoContext context)
	{
		if (created is null)
		{
			Apply(before);
		}
		else
		{
			ref var terrain = ref FindTerrain(context);
			assets.Delete(created);
			terrain.Foliage = default;
		}

		context.InteractionState.MarkSceneDirty();
	}

	public void Redo(EditorUndoRedoContext context)
	{
		if (created is null)
		{
			Apply(after);
		}
		else
		{
			ref var terrain = ref FindTerrain(context);
			assets.Restore(created);
			Apply(after);
			terrain.Foliage = new AssetRef<TerrainFoliage> { NodeId = assetId };
		}

		context.InteractionState.MarkSceneDirty();
	}

	private void Apply(FoliageSnapshot snapshot)
	{
		var foliage = AssetDatabase.GetInstance<TerrainFoliage>(assetId)
			?? throw new InvalidOperationException($"Foliage asset '{assetId:D}' was not found.");

		snapshot.Apply(foliage);
		assets.MarkDirty(assetId, foliage);
	}

	private ref TerrainComponent FindTerrain(EditorUndoRedoContext context)
	{
		var scene = context.SceneWorkspace.CurrentScene;
		var entity = scene.EntityIds.FirstOrDefault(pair => pair.Value == entityId).Key;
		if (scene.World.IsAlive(entity) == false || scene.World.HasComponent<TerrainComponent>(entity) == false)
		{
			throw new InvalidOperationException($"Terrain entity '{entityId:D}' was not found.");
		}

		return ref scene.World.GetComponent<TerrainComponent>(entity);
	}
}
