using System.Numerics;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

internal sealed record FoliageBrushSettings
{
	public const float DefaultRadiusMeters = 12.0f;
	public const float DefaultDensity = 1.0f;

	public float RadiusMeters = DefaultRadiusMeters;
	public float Density = DefaultDensity;
	public float MinimumSpacingMeters = 1.0f;
	public float MinScale = 0.8f;
	public float MaxScale = 1.2f;
	public bool RandomYaw = true;
	public float NormalAlignment;

	public void Validate()
	{
		if (float.IsFinite(RadiusMeters) == false || RadiusMeters <= 0.0f ||
		    float.IsFinite(Density) == false || Density < 0.0f ||
		    float.IsFinite(MinimumSpacingMeters) == false || MinimumSpacingMeters < 0.0f ||
		    float.IsFinite(MinScale) == false || MinScale <= 0.0f ||
		    float.IsFinite(MaxScale) == false || MaxScale < MinScale || MaxScale > (float)Half.MaxValue ||
		    float.IsFinite(NormalAlignment) == false || NormalAlignment < 0.0f || NormalAlignment > 1.0f ||
		    Math.PI * RadiusMeters * RadiusMeters * Density / 100.0 > int.MaxValue)
		{
			throw new ArgumentException("Invalid foliage brush settings.");
		}
	}
}

internal readonly record struct FoliagePaletteEntry(AssetRef<FoliageType> Type, float Weight = 1.0f, bool Enabled = true);
internal readonly record struct FoliageStampResult(int Added, int Removed);

internal sealed class FoliageBrush
{
	private readonly TerrainFoliage _foliage;
	private readonly FoliageBrushSettings _settings;
	private readonly FoliagePaletteEntry[] _palette;
	private readonly Random _random;

	public FoliageBrush(
		TerrainFoliage foliage,
		FoliageBrushSettings settings,
		IReadOnlyList<FoliagePaletteEntry> palette,
		Random random)
	{
		_foliage = foliage;
		_settings = settings;
		_palette = palette.Where(entry => entry.Enabled && entry.Type.IsValid).ToArray();
		_random = random;
	}

	public Dictionary<(int X, int Z), List<FoliageInstance>> Cells { get; } = new();
	public List<AssetRef<FoliageType>> AddedTypes { get; } = [];

	public FoliageStampResult Paint(Vector2 center, TerrainRuntimeData surface)
	{
		var paintTypes = _palette.Where(entry => float.IsFinite(entry.Weight) && entry.Weight > 0.0f).ToArray();
		if (paintTypes.Length == 0 || _settings.Density == 0.0f) return default;

		var typeIndices = new Dictionary<Guid, int>();
		for (var i = 0; i < _foliage.Types.Count; i++)
		{
			typeIndices.TryAdd(_foliage.Types[i].NodeId, i);
		}

		var availableTypeSlots = FoliageInstancePacking.MaxTypeIndex + 1 - _foliage.Types.Count;
		var weightedTypes = new List<(AssetRef<FoliageType> Type, double CumulativeWeight)>();
		var totalWeight = 0.0;
		foreach (var entry in paintTypes)
		{
			if (typeIndices.ContainsKey(entry.Type.NodeId) == false && availableTypeSlots <= 0) continue;

			totalWeight += entry.Weight;
			weightedTypes.Add((entry.Type, totalWeight));
		}

		if (weightedTypes.Count == 0) return default;

		var radius = _settings.RadiusMeters;
		var radiusSquared = (double)radius * radius;
		var halfSize = surface.ResolvedWorldSize * 0.5f;
		var extent = new Vector2(radius);
		var minOffset = Vector2.Max(-halfSize - center, -extent);
		var maxOffset = Vector2.Min(halfSize - center, extent);
		var expectedCount = GetBrushArea(radius, minOffset, maxOffset) * _settings.Density / 100.0;
		var target = (int)Math.Floor(expectedCount);
		if (_random.NextDouble() < expectedCount - target)
		{
			target++;
		}

		if (target == 0) return default;

		// Repacking can round each position by half a quantisation step on each horizontal axis.
		var spacing = _settings.MinimumSpacingMeters > 0.0f
			? _settings.MinimumSpacingMeters + MathF.Sqrt(2.0f) * _foliage.ResolvedCellSizeMeters / ushort.MaxValue
			: 0.0f;
		var spacingIndex = new Dictionary<(long X, long Z), List<Vector2>>();
		var searchRadius = (double)radius + spacing;
		var searchRadiusSquared = searchRadius * searchRadius;
		var neighborhood = FindCells(center, (float)searchRadius);
		foreach (var cell in neighborhood)
		{
			for (var i = 0; i < cell.InstanceCount; i++)
			{
				var point = Horizontal(cell.GetInstance(i).Position);
				var distanceSquared = Vector2.DistanceSquared(point, center);
				if (distanceSquared <= radiusSquared)
				{
					target--;
				}

				if (spacing > 0.0f && distanceSquared <= searchRadiusSquared)
				{
					IndexPoint(spacingIndex, point, spacing);
				}
			}
		}

		if (target <= 0) return default;

		var added = 0;
		var attempts = (long)target * 8;
		for (long attempt = 0; attempt < attempts && added < target; attempt++)
		{
			var offset = new Vector2(
				float.Lerp(minOffset.X, maxOffset.X, _random.NextSingle()),
				float.Lerp(minOffset.Y, maxOffset.Y, _random.NextSingle()));
			var point = center + offset;
			if (offset.LengthSquared() > radiusSquared ||
			    (spacing > 0.0f && HasNeighbor(spacingIndex, point, spacing)) ||
			    surface.TrySampleLocalSurface(point.X, point.Y, out var position, out var normal) == false)
			{
				continue;
			}

			var choice = _random.NextDouble() * totalWeight;
			var selection = 0;
			while (selection < weightedTypes.Count - 1 && choice >= weightedTypes[selection].CumulativeWeight)
			{
				selection++;
			}

			var type = weightedTypes[selection].Type;
			if (typeIndices.TryGetValue(type.NodeId, out var typeIndex) == false)
			{
				typeIndex = _foliage.Types.Count + AddedTypes.Count;
				if (typeIndex > FoliageInstancePacking.MaxTypeIndex) continue;

				typeIndices.Add(type.NodeId, typeIndex);
				AddedTypes.Add(type);
			}

			var yaw = _settings.RandomYaw ? _random.NextSingle() * MathF.Tau : 0.0f;
			var alignment = Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitY, normal), 1.0f + normal.Y));
			var tilt = Quaternion.Slerp(Quaternion.Identity, alignment, _settings.NormalAlignment);
			var rotation = tilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
			var scale = _settings.MinScale + _random.NextSingle() * (_settings.MaxScale - _settings.MinScale);
			var key = _foliage.GetCellCoordinates(point.X, point.Y);
			if (Cells.TryGetValue(key, out var instances) == false)
			{
				instances = ReadCell(key);
				Cells.Add(key, instances);
			}

			instances.Add(new FoliageInstance(position, rotation, scale, typeIndex));
			if (spacing > 0.0f)
			{
				IndexPoint(spacingIndex, point, spacing);
			}

			added++;
		}

		return new FoliageStampResult(added, 0);
	}

	private static double GetBrushArea(float radius, Vector2 minOffset, Vector2 maxOffset)
	{
		var radiusSquared = (double)radius * radius;
		if (minOffset == new Vector2(-radius) && maxOffset == new Vector2(radius))
		{
			return Math.PI * radiusSquared;
		}

		// Integrate clipped vertical slices only when the circle crosses a terrain edge.
		const int sliceCount = 32;
		var width = ((double)maxOffset.X - minOffset.X) / sliceCount;
		var area = 0.0;
		for (var i = 0; i < sliceCount; i++)
		{
			var x = minOffset.X + (i + 0.5) * width;
			var extent = Math.Sqrt(Math.Max(0.0, radiusSquared - x * x));
			var depth = Math.Min(maxOffset.Y, extent) - Math.Max(minOffset.Y, -extent);
			area += Math.Max(0.0, depth) * width;
		}

		return Math.Min(area, Math.PI * radiusSquared);
	}

	public FoliageStampResult Erase(Vector2 center)
	{
		var enabled = _palette.Select(entry => entry.Type.NodeId).ToHashSet();
		var radiusSquared = (double)_settings.RadiusMeters * _settings.RadiusMeters;
		var removed = 0;
		foreach (var cell in FindCells(center, _settings.RadiusMeters))
		{
			List<FoliageInstance>? kept = null;
			for (var i = 0; i < cell.InstanceCount; i++)
			{
				var instance = cell.GetInstance(i);
				if (Vector2.DistanceSquared(Horizontal(instance.Position), center) <= radiusSquared &&
				    (enabled.Count == 0 ||
				     ((uint)instance.TypeIndex < (uint)_foliage.Types.Count && enabled.Contains(_foliage.Types[instance.TypeIndex].NodeId))))
				{
					if (kept is null)
					{
						kept = new List<FoliageInstance>(cell.InstanceCount);
						for (var before = 0; before < i; before++)
						{
							kept.Add(cell.GetInstance(before));
						}
					}

					removed++;
				}
				else
				{
					kept?.Add(instance);
				}
			}

			if (kept is not null)
			{
				Cells.Add((cell.X, cell.Z), kept);
			}
		}

		return new FoliageStampResult(0, removed);
	}

	private IEnumerable<TerrainFoliageCell> FindCells(Vector2 center, float radius)
	{
		var (minX, minZ) = _foliage.GetCellCoordinates(center.X - radius, center.Y - radius);
		var (maxX, maxZ) = _foliage.GetCellCoordinates(center.X + radius, center.Y + radius);
		return _foliage.GetCellsInRange(minX, minZ, maxX, maxZ);
	}

	private List<FoliageInstance> ReadCell((int X, int Z) key)
	{
		var cell = _foliage.GetCell(key.X, key.Z);
		if (cell is null) return [];

		var instances = new List<FoliageInstance>(cell.InstanceCount + 1);
		for (var i = 0; i < cell.InstanceCount; i++)
		{
			instances.Add(cell.GetInstance(i));
		}

		return instances;
	}

	private static Vector2 Horizontal(Vector3 position) => new(position.X, position.Z);

	private static (long X, long Z) SpacingKey(Vector2 point, float spacing) =>
		((long)Math.Floor((double)point.X / spacing), (long)Math.Floor((double)point.Y / spacing));

	private static void IndexPoint(Dictionary<(long X, long Z), List<Vector2>> index, Vector2 point, float spacing)
	{
		var key = SpacingKey(point, spacing);
		if (index.TryGetValue(key, out var bucket) == false)
		{
			bucket = [];
			index.Add(key, bucket);
		}

		bucket.Add(point);
	}

	private static bool HasNeighbor(Dictionary<(long X, long Z), List<Vector2>> index, Vector2 point, float spacing)
	{
		var (x, z) = SpacingKey(point, spacing);
		for (var dz = -1; dz <= 1; dz++)
		{
			for (var dx = -1; dx <= 1; dx++)
			{
				if (index.TryGetValue((x + dx, z + dz), out var bucket) == false) continue;

				foreach (var neighbor in bucket)
				{
					if (Vector2.DistanceSquared(point, neighbor) < (double)spacing * spacing) return true;
				}
			}
		}

		return false;
	}
}
