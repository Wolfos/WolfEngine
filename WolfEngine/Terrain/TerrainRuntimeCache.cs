using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine;

internal sealed class TerrainRuntimeCache
{
	public void CollectSharedTerrain(
		RenderGraph renderGraph,
		World world,
		Entity entity,
		ref TerrainComponent component,
		in WorldTransform transform,
		Vector3 cameraOrigin,
		GpuDrawDatabase gpuDrawDatabase)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(gpuDrawDatabase);
		var runtime = TerrainRuntimeRegistry.GetOrCreateRuntime(world, entity);
		var built = runtime.EnsureBuilt(component);
		runtime.ReleasePendingMeshResources(renderGraph);
		if (built == false)
		{
			return;
		}

		var material = ResolveTerrainMaterial(ref component, renderGraph);
		var records = new List<TerrainChunkDrawRecord>(runtime.Chunks.Count);
		runtime.CollectChunkDrawRecords(renderGraph, material, cameraOrigin, transform.LocalToWorld, records);
		for (var i = 0; i < records.Count; i++)
		{
			var record = records[i];
			gpuDrawDatabase.TouchTerrainChunk(
				entity,
				record.ChunkIndex,
				record.Mesh,
				record.Material,
				record.LocalBounds,
				record.InstanceData,
				record.Surface,
				record.RayTracingChunk,
				record.WorldTransform);
		}
	}

	private static Material ResolveTerrainMaterial(ref TerrainComponent component, RenderGraph renderGraph)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);

		component.Material ??= CreateTerrainMaterial();
		renderGraph.EnsureMaterialResources(component.Material);
		return component.Material;
	}

	private static Material CreateTerrainMaterial()
	{
		return new Material("__terrain__")
		{
			Color = ColorRGBA.White,
			AlphaMode = AlphaMode.Opaque,
			AlphaCutoff = 0.0f,
			MetallicFactor = 0.0f,
			RoughnessFactor = 1.0f,
			EmissiveFactor = Vector3.Zero,
			EmissiveIntensity = 0.0f
		};
	}
}
