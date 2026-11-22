using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public sealed class FoliageAuthoringService
{
	private readonly TerrainFoliageAssetService _assets;
	private readonly IEditorUndoRedoService _history;
	private readonly IEditorInteractionState _interactionState;
	/// <summary>How far off its surface an instance may be before <see cref="SnapToTerrain"/> moves it.</summary>
	internal const float SnapToleranceMeters = 0.01f;

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

	/// <summary>
	/// Moves the placed foliage over a heightmap edit up or down by as much as the surface under each instance moved, so
	/// it stays on the ground and keeps any height it was deliberately set at. Called by the terrain stroke, which commits
	/// the returned edit together with its own so one undo reverts both.
	/// </summary>
	/// <returns>The foliage edit, or null when no instance moved.</returns>
	internal IEditorUndoRedoEntry? FollowHeightmapEdit(
		EditorScene scene,
		Entity entity,
		Texture before,
		Texture after,
		in TerrainHeightmapDirtyRegion region)
	{
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);

		if (region.IsEmpty ||
		    TryGetEditableFoliage(scene, entity, out var foliage, out var assetId, out var entityId) == false ||
		    TryGetHeightTop(before, out var beforeTop) == false ||
		    TryGetHeightTop(after, out var afterTop) == false ||
		    beforeTop.Width != afterTop.Width ||
		    beforeTop.Height != afterTop.Height)
		{
			return null;
		}

		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(entity);
		var worldSize = terrain.GetResolvedWorldSize();
		var heightScale = terrain.GetResolvedHeightScale();

		// A heightmap pixel moves the surface up to one pixel past it, so widen the region by one.
		var pixelSize = new Vector2(
			worldSize.X / Math.Max(beforeTop.Width - 1, 1),
			worldSize.Y / Math.Max(beforeTop.Height - 1, 1));
		var regionMin = new Vector2(region.X - 1, region.Y - 1) * pixelSize - (worldSize * 0.5f);
		var regionMax = new Vector2(region.X + region.Width, region.Y + region.Height) * pixelSize - (worldSize * 0.5f);

		var beforeCells = MoveInstances(
			foliage,
			regionMin,
			regionMax,
			position =>
			{
				var beforeHeight = SampleNormalizedHeight(beforeTop, worldSize, position.X, position.Z);
				var afterHeight = SampleNormalizedHeight(afterTop, worldSize, position.X, position.Z);

				return position.Y + ((afterHeight - beforeHeight) * heightScale);
			});
		if (beforeCells.Count == 0) return null;

		_assets.MarkDirty(assetId, foliage);

		return CreateMoveEdit(foliage, assetId, entityId, beforeCells);
	}

	/// <summary>
	/// Puts every placed foliage instance of the terrain on its surface, for foliage placed before the terrain was shaped
	/// or whose heightmap was replaced. Undoable.
	/// </summary>
	/// <returns>How many instances moved.</returns>
	internal int SnapToTerrain(EditorScene scene, Entity entity)
	{
		EndStroke();

		if (TryGetEditableFoliage(scene, entity, out var foliage, out var assetId, out var entityId) == false)
		{
			return 0;
		}

		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(entity);
		var surface = TerrainRuntimeRegistry.GetOrCreateRuntime(scene.World, entity);
		if (surface.EnsureSampling(terrain) == false) return 0;

		var moved = 0;
		var beforeCells = MoveInstances(
			foliage,
			new Vector2(float.MinValue),
			new Vector2(float.MaxValue),
			position =>
			{
				if (surface.TrySampleLocalSurface(position.X, position.Z, out var surfacePoint, out _) == false)
				{
					return position.Y;
				}

				// Packed heights are quantised across their cell, so an instance already on the surface reads back a little off.
				if (MathF.Abs(surfacePoint.Y - position.Y) <= SnapToleranceMeters)
				{
					return position.Y;
				}

				moved++;

				return surfacePoint.Y;
			});
		if (beforeCells.Count == 0) return 0;

		var edit = CreateMoveEdit(foliage, assetId, entityId, beforeCells);
		_history.BeginCapture(edit.Description);
		_history.CommitCapture(edit);
		_assets.MarkDirty(assetId, foliage);
		_interactionState.MarkSceneDirty();

		return moved;
	}

	private bool TryGetEditableFoliage(
		EditorScene scene,
		Entity entity,
		out TerrainFoliage foliage,
		out Guid assetId,
		out Guid entityId)
	{
		foliage = null!;
		assetId = Guid.Empty;
		entityId = Guid.Empty;
		if (scene.World.IsAlive(entity) == false ||
		    scene.World.HasComponent<TerrainComponent>(entity) == false ||
		    scene.EntityIds.TryGetValue(entity, out entityId) == false)
		{
			return false;
		}

		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(entity);
		if (terrain.Foliage.IsValid == false ||
		    _assets.CanEdit(terrain.Foliage.NodeId) == false ||
		    terrain.Foliage.Asset is not { } asset)
		{
			return false;
		}

		foliage = asset;
		assetId = terrain.Foliage.NodeId;

		return true;
	}

	/// <summary>
	/// Gives every instance in the cells over a local XZ rectangle the height <paramref name="getHeight"/> returns for
	/// its position, rewriting only the cells where one changed.
	/// </summary>
	/// <returns>The changed cells as they were before.</returns>
	private static Dictionary<(int X, int Z), TerrainFoliageCell?> MoveInstances(
		TerrainFoliage foliage,
		Vector2 regionMin,
		Vector2 regionMax,
		Func<Vector3, float> getHeight)
	{
		var beforeCells = new Dictionary<(int X, int Z), TerrainFoliageCell?>();
		var minCell = foliage.GetCellCoordinates(regionMin.X, regionMin.Y);
		var maxCell = foliage.GetCellCoordinates(regionMax.X, regionMax.Y);
		var cells = foliage.GetCellsInRange(minCell.X, minCell.Z, maxCell.X, maxCell.Z).ToArray();
		var instances = new List<FoliageInstance>();
		foreach (var cell in cells)
		{
			instances.Clear();
			var changed = false;
			for (var i = 0; i < cell.InstanceCount; i++)
			{
				var instance = cell.GetInstance(i);
				var position = instance.Position;
				if (position.X >= regionMin.X &&
				    position.X <= regionMax.X &&
				    position.Z >= regionMin.Y &&
				    position.Z <= regionMax.Y)
				{
					var height = getHeight(position);
					if (float.IsFinite(height) && height != position.Y)
					{
						instance = instance with { Position = position with { Y = height } };
						changed = true;
					}
				}

				instances.Add(instance);
			}

			if (changed == false) continue;

			beforeCells.Add((cell.X, cell.Z), cell);
			foliage.ReplaceCell(cell.X, cell.Z, instances);
		}

		if (beforeCells.Count > 0)
		{
			foliage.MarkChanged();
		}

		return beforeCells;
	}

	private FoliageStrokeUndoRedoEntry CreateMoveEdit(
		TerrainFoliage foliage,
		Guid assetId,
		Guid entityId,
		Dictionary<(int X, int Z), TerrainFoliageCell?> beforeCells)
	{
		var afterCells = beforeCells.Keys.ToDictionary(key => key, key => foliage.GetCell(key.X, key.Z));

		return new FoliageStrokeUndoRedoEntry(
			_assets,
			assetId,
			entityId,
			null,
			new FoliageSnapshot(beforeCells, null),
			new FoliageSnapshot(afterCells, null));
	}

	private static bool TryGetHeightTop(Texture texture, out TextureMipData top)
	{
		top = default;
		if (texture.Format != TextureFormat.R16Unorm || texture.MipLevels.Length == 0) return false;

		top = texture.MipLevels[0];

		return top.Width >= 2 && top.Height >= 2 && top.Data.Length >= top.Width * top.Height * 2;
	}

	/// <summary>
	/// Height of an R16 heightmap at a local XZ, from 0 to 1, interpolated over the same triangles
	/// <see cref="TerrainRuntimeData"/> samples the surface on.
	/// </summary>
	private static float SampleNormalizedHeight(TextureMipData top, Vector2 worldSize, float localX, float localZ)
	{
		var sampleX = Math.Clamp(((localX / worldSize.X) + 0.5f) * (top.Width - 1), 0.0f, top.Width - 1);
		var sampleZ = Math.Clamp(((localZ / worldSize.Y) + 0.5f) * (top.Height - 1), 0.0f, top.Height - 1);
		var cellX = Math.Min((int)MathF.Floor(sampleX), top.Width - 2);
		var cellZ = Math.Min((int)MathF.Floor(sampleZ), top.Height - 2);
		var tx = Math.Clamp(sampleX - cellX, 0.0f, 1.0f);
		var tz = Math.Clamp(sampleZ - cellZ, 0.0f, 1.0f);

		var h00 = ReadHeight(top, cellX, cellZ);
		var h10 = ReadHeight(top, cellX + 1, cellZ);
		var h01 = ReadHeight(top, cellX, cellZ + 1);
		var h11 = ReadHeight(top, cellX + 1, cellZ + 1);
		if (tx + tz <= 1.0f)
		{
			return (h00 * (1.0f - tx - tz)) + (h01 * tz) + (h10 * tx);
		}

		return (h10 * (1.0f - tz)) + (h01 * (1.0f - tx)) + (h11 * (tx + tz - 1.0f));
	}

	private static float ReadHeight(TextureMipData top, int x, int z)
	{
		var offset = ((z * top.Width) + x) * 2;

		return (top.Data[offset] | (top.Data[offset + 1] << 8)) / 65535.0f;
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

	/// <summary>An edit that created the asset also assigned it to the terrain entity; any other edits the asset alone.</summary>
	public EditorUndoRedoScope Scope => created is null ? EditorUndoRedoScope.Asset : EditorUndoRedoScope.AuthoringScene;

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
