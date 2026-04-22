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
			}


			gpuDrawDatabase.EndSync();
			_renderGraph.PublishSnapshot();


			_stressFrame++;
		}
	}
}
