using System;
using System.Collections.Generic;
using WolfEngine.ECS;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using System.Numerics;
using WolfEngine.Rendering.Passes;

namespace WolfEngine;

public interface IRenderPipeline
{
	void Run(Action? startup = null);

	void PublishSnapshot(Camera camera, WorldTransform cameraWorldTransform, RenderConfig config,
		IReadOnlyList<World> worlds);
}

public class RenderPipeline : IRenderPipeline
{
	private const int DdgiDebugProbeEntityBaseIndex = -2_000_000_000;
	private readonly RenderGraph _renderGraph;
	private readonly TerrainRuntimeCache _terrainRuntimeCache = new();
	private readonly DebugPrimitiveMeshFactory _debugPrimitiveMeshFactory = new();
	private int _stressFrame;

	public RenderPipeline(RenderGraph renderGraph)
	{
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
	}

	public void Run(Action? startup = null)
	{
		_renderGraph.Startup(startup ?? (() => { }), static _ => { });
	}

	public void PublishSnapshot(Camera camera, WorldTransform cameraWorldTransform, RenderConfig config,
		IReadOnlyList<World> worlds)
	{
		using (FrameProfiler.Instance.Measure("Build Snapshot"))
		{
			FrameSnapshot snapshot;
			using (FrameProfiler.Instance.Measure("Wait for snapshot"))
			{
				snapshot = _renderGraph.BeginSnapshotWrite();
				snapshot.SetCamera(camera, cameraWorldTransform);
				snapshot.SetConfig(config);
			}

			var sunDirection = Vector3.Normalize(new Vector3(0.2f, 0.9f, 0.3f));
			var sunIntensityScale = 1.0f;
			var hasSunDirection = false;
			var gpuDrawDatabase = snapshot.GpuDrawDatabase;
			var cameraOrigin = Vector3.Zero;
			if (Matrix4x4.Decompose(cameraWorldTransform.LocalToWorld, out _, out _, out cameraOrigin))
			{
			}
			using (FrameProfiler.Instance.Measure("Begin Sync"))
			{
				gpuDrawDatabase.BeginSync();
			}

			for (var i = 0; i < (worlds?.Count ?? 0); i++)
			{
				var world = worlds![i];
				if (world is null)
				{
					continue;
				}

				using (FrameProfiler.Instance.Measure("Gather meshes"))
				{
					foreach (var entry in world.View<WorldTransform, MeshRenderer>())
					{
						if (world.IsEnabled(entry.Entity) == false) continue;
						
						ref var transform = ref entry.First;
						ref var meshRenderer = ref entry.Second;

						if (meshRenderer.TryValidate() == false) continue;

						if (GraphicsConfig.GpuHardeningStressEnabled)
						{
							var churnKey = entry.Entity.Index + _stressFrame;
							if ((churnKey % 7) == 0)
							{
								// Force structural remove/add churn by skipping this entity for the frame.
								continue;
							}

							if (meshRenderer.Material is not null && (churnKey % 5) == 0)
							{
								var toggled = ((_stressFrame / 30) & 1) == 0;
								meshRenderer.Material.AlphaMode = toggled ? AlphaMode.AlphaTest : AlphaMode.Opaque;
								meshRenderer.Material.AlphaCutoff = toggled ? 0.4f : 0.0f;
							}
						}


						var transformMatrix = transform.LocalToWorld;
						gpuDrawDatabase.TouchMesh(entry.Entity, meshRenderer.Mesh, meshRenderer.Material, transformMatrix);
					}
				}

				using (FrameProfiler.Instance.Measure("Gather debug primitives"))
				{
					foreach (var entry in world.View<WorldTransform, DebugPrimitiveRenderer>())
					{
						if (world.IsEnabled(entry.Entity) == false)
						{
							continue;
						}

						ref var transform = ref entry.First;
						ref var debugPrimitive = ref entry.Second;
						var primitiveMesh = _debugPrimitiveMeshFactory.GetMesh(debugPrimitive.GetResolvedPrimitiveType());
						gpuDrawDatabase.TouchDebugPrimitive(
							entry.Entity,
							primitiveMesh,
							debugPrimitive.Tint,
							debugPrimitive.GetResolvedAlphaMode(),
							transform.LocalToWorld);
					}
				}

				using (FrameProfiler.Instance.Measure("Gather terrain"))
				{
					foreach (var entry in world.View<WorldTransform, TerrainComponent>())
					{
						if (world.IsEnabled(entry.Entity) == false)
						{
							continue;
						}

						_terrainRuntimeCache.CollectSharedTerrain(
							_renderGraph,
							world,
							entry.Entity,
							ref entry.Second,
							entry.First,
							cameraOrigin,
							gpuDrawDatabase);
					}
				}

				using (FrameProfiler.Instance.Measure("Gather lights"))
				{
					foreach (var entry in world.View<WorldTransform, Light>())
					{
						ref var transform = ref entry.First;
						ref var light = ref entry.Second;
						snapshot.AddLight(light, transform.LocalToWorld);
						if (hasSunDirection == false && light.Type == LightType.Directional)
						{
							var forward = Vector3.TransformNormal(Vector3.UnitZ, transform.LocalToWorld);
							if (forward == Vector3.Zero)
							{
								forward = new Vector3(0, -1, 0);
							}

							sunDirection = Vector3.Normalize(forward);
							sunIntensityScale = DirectionalLightUtility.GetIntensityScale(light, forward);
							hasSunDirection = true;
						}
					}

					snapshot.SetSun(sunDirection, sunIntensityScale);
				}

				using (FrameProfiler.Instance.Measure("Gather decals"))
				{
					CollectDecalProjectors(snapshot, world, _renderGraph);
				}
			}

			using (FrameProfiler.Instance.Measure("Gather DDGI probe debug primitives"))
			{
				CollectDdgiProbeDebugPrimitives(
					config,
					cameraOrigin,
					gpuDrawDatabase,
					_debugPrimitiveMeshFactory);
			}

			gpuDrawDatabase.EndSync();
			_renderGraph.PublishSnapshot();


			_stressFrame++;
		}
	}

	internal static void CollectDdgiProbeDebugPrimitives(
		RenderConfig config,
		Vector3 cameraPosition,
		GpuDrawDatabase gpuDrawDatabase,
		DebugPrimitiveMeshFactory debugPrimitiveMeshFactory)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(gpuDrawDatabase);
		ArgumentNullException.ThrowIfNull(debugPrimitiveMeshFactory);

		var ddgi = config.DiffuseGlobalIllumination;
		if (DdgiUtilities.IsRayTracedDdgiEnabled(config) == false ||
		    ddgi.DebugProbeSpheres == false)
		{
			return;
		}

		var shape = DdgiUtilities.GetGridShape(ddgi);
		var sphereMesh = debugPrimitiveMeshFactory.GetMesh(DebugPrimitiveType.Sphere);
		var radius = MathF.Max(ddgi.DebugProbeSphereRadius, 0.01f);
		var diameter = radius * 2.0f;
		var spacing = MathF.Max(ddgi.ProbeSpacing, 0.001f);
		var runtimeOrigin = DdgiUtilities.GetRuntimeOrigin(
			ddgi.Origin,
			shape,
			spacing,
			cameraPosition);
		var probeIndex = 0;
		for (var z = 0; z < shape.CountZ; z++)
		{
			for (var y = 0; y < shape.CountY; y++)
			{
				for (var x = 0; x < shape.CountX; x++)
				{
					var position = runtimeOrigin + new Vector3(x * spacing, y * spacing, z * spacing);
					var transform = Matrix4x4.CreateScale(diameter) * Matrix4x4.CreateTranslation(position);
					gpuDrawDatabase.TouchDebugPrimitive(
						new Entity(DdgiDebugProbeEntityBaseIndex + probeIndex, 1),
						sphereMesh,
						ColorRGBA.White,
						AlphaMode.AlphaBlend,
						transform);
					probeIndex++;
				}
			}
		}
	}

	internal static void CollectDecalProjectors(FrameSnapshot snapshot, World world, RenderGraph renderGraph)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(renderGraph);

		foreach (var entry in world.View<WorldTransform, DecalProjector>())
		{
			if (world.IsEnabled(entry.Entity) == false)
			{
				continue;
			}

			ref var transform = ref entry.First;
			ref var projector = ref entry.Second;
			if (projector.IsValid == false)
			{
				continue;
			}

			projector.EnsureTextureResources(renderGraph);
			snapshot.AddDecal(projector, transform.LocalToWorld);
		}

		foreach (var entry in world.View<WorldTransform, TerrainComponent>())
		{
			if (world.IsEnabled(entry.Entity) == false)
			{
				continue;
			}

			ref var terrainTransform = ref entry.First;
			ref var terrain = ref entry.Second;
			if (terrain.AuthoringBrushPreviewDecal is not { } previewProjector ||
			    previewProjector.IsValid == false)
			{
				continue;
			}

			previewProjector.EnsureTextureResources(renderGraph);
			snapshot.AddDecal(
				previewProjector,
				terrain.AuthoringBrushPreviewLocalTransform * terrainTransform.LocalToWorld);
		}
	}
}
