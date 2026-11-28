using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

/// <summary>
/// Up to <see cref="TerrainFoliageRuntime.ChunkSize"/> instances of one cell, as the cull reads them: one thread group
/// each. <c>QuantizationMin</c> and <c>QuantizationMax</c> are the cell's bounds, which packed positions are quantised
/// across; <c>CullMin</c> and <c>CullMax</c> enclose the chunk's instances' bounds, with the longest cull distance of
/// any type in it in <c>CullMin.W</c> and the longest shadow distance in <c>CullMax.W</c>. Mirrored by
/// <c>GpuFoliageChunk</c> in foliage_cull.compute.slang.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct GpuFoliageChunk
{
	public GpuFoliageChunk(
		Vector4 quantizationMin,
		Vector4 quantizationMax,
		Vector4 cullMin,
		Vector4 cullMax,
		uint instanceStart,
		uint instanceCount)
	{
		QuantizationMin = quantizationMin;
		QuantizationMax = quantizationMax;
		CullMin = cullMin;
		CullMax = cullMax;
		InstanceStart = instanceStart;
		InstanceCount = instanceCount;
		Pad0 = 0;
		Pad1 = 0;
	}

	public readonly Vector4 QuantizationMin;
	public readonly Vector4 QuantizationMax;
	public readonly Vector4 CullMin;
	public readonly Vector4 CullMax;
	public readonly uint InstanceStart;
	public readonly uint InstanceCount;
	public readonly uint Pad0;
	public readonly uint Pad1;
}

/// <summary>
/// One terrain's placed foliage, resolved for drawing: each type flattened, every instance in one packed array for
/// upload, and the chunks the cull runs over. Within a cell, instances are reordered along a Z-order curve before they
/// are chunked, so each chunk covers a small area and culls tightly. Immutable once built; <see cref="FoliageRuntimeCache"/>
/// builds a new one when the asset or any of its types changes. See docs/foliage.md.
/// </summary>
internal sealed class TerrainFoliageRuntime : IFoliageCullSource
{
	/// <summary>Instances per chunk: one per thread of a cull thread group.</summary>
	public const int ChunkSize = 64;

	public TerrainFoliageRuntime(TerrainFoliage source, IReadOnlyList<FoliageTypeRuntime?> types)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(types);

		Source = source;
		SourceRevision = source.Revision;
		Types = types.ToArray();
		SourceCells = source.Cells.ToArray();

		var instanceCount = 0;
		var chunkCount = 0;
		foreach (var cell in SourceCells)
		{
			instanceCount += cell.InstanceCount;
			chunkCount += (cell.InstanceCount + ChunkSize - 1) / ChunkSize;
		}

		PackedInstances = new uint[instanceCount * 4];
		Chunks = new GpuFoliageChunk[chunkCount];
		CellBounds = new Box3[SourceCells.Length];
		CellInstanceStarts = new int[SourceCells.Length];
		TypeInstanceCounts = new int[Types.Length];

		var instanceStart = 0;
		var chunkIndex = 0;
		for (var cellIndex = 0; cellIndex < SourceCells.Length; cellIndex++)
		{
			var cell = SourceCells[cellIndex];
			var packed = MemoryMarshal.Cast<byte, uint>(
				cell.Instances.AsSpan(0, cell.InstanceCount * FoliageInstancePacking.SizeInBytes));
			var order = CreateZOrder(packed, cell.InstanceCount);
			for (var i = 0; i < order.Length; i++)
			{
				packed.Slice(order[i] * 4, 4).CopyTo(PackedInstances.AsSpan((instanceStart + i) * 4));
			}

			var cellCullMin = new Vector3(float.MaxValue);
			var cellCullMax = new Vector3(float.MinValue);
			for (var chunkStart = 0; chunkStart < order.Length; chunkStart += ChunkSize)
			{
				var chunkLength = Math.Min(ChunkSize, order.Length - chunkStart);
				var cullMin = new Vector3(float.MaxValue);
				var cullMax = new Vector3(float.MinValue);
				var cullDistance = 0.0f;
				var shadowDistance = 0.0f;
				for (var i = chunkStart; i < chunkStart + chunkLength; i++)
				{
					var instance = cell.GetInstance(order[i]);
					if ((uint)instance.TypeIndex >= (uint)Types.Length || Types[instance.TypeIndex] is not { } type)
					{
						continue;
					}

					TypeInstanceCounts[instance.TypeIndex]++;
					var bounds = GetInstanceBounds(type, instance);
					cullMin = Vector3.Min(cullMin, bounds.Center - new Vector3(bounds.Radius));
					cullMax = Vector3.Max(cullMax, bounds.Center + new Vector3(bounds.Radius));
					cullDistance = MathF.Max(cullDistance, type.CullDistance);
					shadowDistance = MathF.Max(shadowDistance, type.ShadowDistance);
				}

				if (cullMin.X > cullMax.X)
				{
					cullMin = cell.BoundsMin;
					cullMax = cell.BoundsMax;
				}

				Chunks[chunkIndex] = new GpuFoliageChunk(
					new Vector4(cell.BoundsMin, 0.0f),
					new Vector4(cell.BoundsMax, 0.0f),
					new Vector4(cullMin, cullDistance),
					new Vector4(cullMax, shadowDistance),
					(uint)(instanceStart + chunkStart),
					(uint)chunkLength);
				chunkIndex++;
				cellCullMin = Vector3.Min(cellCullMin, cullMin);
				cellCullMax = Vector3.Max(cellCullMax, cullMax);
			}

			if (cellCullMin.X > cellCullMax.X)
			{
				cellCullMin = cell.BoundsMin;
				cellCullMax = cell.BoundsMax;
			}

			CellBounds[cellIndex] = new Box3(cellCullMin, cellCullMax);
			CellInstanceStarts[cellIndex] = instanceStart;
			instanceStart += cell.InstanceCount;
		}

		InstanceCount = instanceCount;
		for (var typeIndex = 0; typeIndex < Types.Length; typeIndex++)
		{
			if (Types[typeIndex] is not { } type)
			{
				continue;
			}

			MaxRayTracingDistance = MathF.Max(MaxRayTracingDistance, type.RayTracingDistance);
			RendererInstanceCount += TypeInstanceCounts[typeIndex] * type.Renderers.Count;
		}
	}

	/// <summary>
	/// The cell's instances sorted along a Z-order curve over their quantised X and Z, so neighbours in the order are
	/// neighbours on the ground.
	/// </summary>
	private static int[] CreateZOrder(ReadOnlySpan<uint> packed, int instanceCount)
	{
		var keys = new ulong[instanceCount];
		var order = new int[instanceCount];
		for (var i = 0; i < instanceCount; i++)
		{
			var x = packed[i * 4] & 0xFFFFu;
			var z = packed[(i * 4) + 1] & 0xFFFFu;
			keys[i] = ((ulong)(SpreadBits(x) | (SpreadBits(z) << 1)) << 32) | (uint)i;
			order[i] = i;
		}

		Array.Sort(keys, order);

		return order;
	}

	/// <summary>Spreads the low 16 bits so a zero bit sits between each, for interleaving two coordinates.</summary>
	private static uint SpreadBits(uint value)
	{
		value &= 0xFFFF;
		value = (value | (value << 8)) & 0x00FF00FF;
		value = (value | (value << 4)) & 0x0F0F0F0F;
		value = (value | (value << 2)) & 0x33333333;
		value = (value | (value << 1)) & 0x55555555;

		return value;
	}

	public TerrainFoliage Source { get; }

	public int SourceRevision { get; }

	/// <summary>Indexed like <see cref="TerrainFoliage.Types"/>; null for a type that could not be resolved.</summary>
	public FoliageTypeRuntime?[] Types { get; }

	/// <summary>The asset's cells, as they were when this was built.</summary>
	public TerrainFoliageCell[] SourceCells { get; }

	/// <summary>
	/// Every instance in its packed form, four words each, cell by cell, and within a cell in Z-order rather than the
	/// asset's order.
	/// </summary>
	public uint[] PackedInstances { get; }

	public GpuFoliageChunk[] Chunks { get; }

	/// <summary>Per cell, the box enclosing every instance's bounds, in the terrain's local space.</summary>
	public Box3[] CellBounds { get; }

	/// <summary>
	/// Per cell, the index of its first instance in <see cref="PackedInstances"/>. The cell's instances take the indices
	/// after it, though not in the cell's own order.
	/// </summary>
	public int[] CellInstanceStarts { get; }

	public int InstanceCount { get; }

	/// <summary>Per type, how many instances name it. Instances of an unresolved type are not counted.</summary>
	public int[] TypeInstanceCounts { get; }

	/// <summary>Every instance's renderers added up: what drawing the whole set at once would expand into.</summary>
	public int RendererInstanceCount { get; }

	public float MaxRayTracingDistance { get; }

	IReadOnlyList<FoliageTypeRuntime?> IFoliageCullSource.Types => Types;

	int IFoliageCullSource.SlotCount => InstanceCount;

	int IFoliageCullSource.ChunkCount => Chunks.Length;

	int IFoliageCullSource.GetTypeSlotCount(int typeIndex) => TypeInstanceCounts[typeIndex];

	/// <summary>The instance's transform in the terrain's local space.</summary>
	public static Matrix4x4 GetInstanceTransform(in FoliageInstance instance)
	{
		return Matrix4x4.CreateScale(instance.Scale) *
		       Matrix4x4.CreateFromQuaternion(instance.Rotation) *
		       Matrix4x4.CreateTranslation(instance.Position);
	}

	public static BoundingSphere GetInstanceBounds(FoliageTypeRuntime type, in FoliageInstance instance)
	{
		var center = instance.Position + Vector3.Transform(type.Bounds.Center * instance.Scale, instance.Rotation);

		return new BoundingSphere(center, type.Bounds.Radius * instance.MaxScale);
	}
}

/// <summary>An axis-aligned box given by its corners.</summary>
internal readonly record struct Box3(Vector3 Min, Vector3 Max)
{
	/// <summary>Distance from a point to the nearest point of the box; zero inside it.</summary>
	public float DistanceTo(Vector3 point)
	{
		var nearest = Vector3.Clamp(point, Min, Max);

		return Vector3.Distance(point, nearest);
	}
}

/// <summary>
/// Keeps one <see cref="TerrainFoliageRuntime"/> per foliage asset, rebuilt when the asset's instances, a type's
/// settings or a type's prefab change. Main thread only.
/// </summary>
internal sealed class FoliageRuntimeCache
{
	private readonly ConditionalWeakTable<TerrainFoliage, Entry> _entries = new();

	public TerrainFoliageRuntime? Resolve(TerrainFoliage foliage, RenderGraph? renderGraph)
	{
		ArgumentNullException.ThrowIfNull(foliage);

		var signature = CreateSignature(foliage);
		if (_entries.TryGetValue(foliage, out var entry) &&
		    entry.Runtime.SourceRevision == foliage.Revision &&
		    entry.Signature.AsSpan().SequenceEqual(signature))
		{
			return entry.Runtime;
		}

		var types = new FoliageTypeRuntime?[foliage.Types.Count];
		for (var i = 0; i < types.Length; i++)
		{
			types[i] = foliage.Types[i].Asset is { } type
				? FoliageTypeRuntime.TryCreate(type, renderGraph)
				: null;
		}

		var runtime = new TerrainFoliageRuntime(foliage, types);
		_entries.AddOrUpdate(foliage, new Entry(runtime, signature));

		return runtime;
	}

	/// <summary>Everything a rebuild depends on besides the instances, compared by reference and value.</summary>
	private static object?[] CreateSignature(TerrainFoliage foliage)
	{
		var signature = new object?[foliage.Types.Count * 5 + 1];
		signature[0] = foliage.Cells.Count;
		for (var i = 0; i < foliage.Types.Count; i++)
		{
			var type = foliage.Types[i].Asset;
			var offset = 1 + i * 5;
			signature[offset] = type;
			signature[offset + 1] = type?.Prefab.Asset;
			signature[offset + 2] = type?.CullDistance;
			signature[offset + 3] = type?.ShadowDistance;
			signature[offset + 4] = type?.RayTracingDistance;
		}

		return signature;
	}

	private sealed record Entry(TerrainFoliageRuntime Runtime, object?[] Signature);
}
