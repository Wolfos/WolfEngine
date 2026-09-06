using WolfEngine.Animation;
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
	// Both snapshot databases consume the change, then one additional snapshot settles motion history.
	private const int DirtyWorldTransformSyncCount = 3;
	private const int DdgiDebugProbeEntityBaseIndex = -2_000_000_000;
	private const float DdgiDebugProbeInstanceMarker = 1.0f;
	private readonly RenderGraph _renderGraph;
	private readonly TerrainRuntimeCache _terrainRuntimeCache = new();
	private readonly DebugPrimitiveMeshFactory _debugPrimitiveMeshFactory = new();
	private readonly Dictionary<GpuDrawDatabase, List<World>> _renderWorldsByDatabase = new();
	private readonly List<Entity> _dirtyWorldTransformRemovalScratch = new();
	private int _stressFrame;
	private bool _gpuHardeningStressWasEnabled;

	public RenderPipeline(RenderGraph renderGraph)
	{
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
	}

	public void Run(Action? startup = null)
	{
		try
		{
			_renderGraph.Startup(startup ?? (() => { }), static _ => { });
		}
		finally
		{
			_renderGraph.CompleteSnapshotPublishing();
		}
	}

	public void PublishSnapshot(Camera camera, WorldTransform cameraWorldTransform, RenderConfig config,
		IReadOnlyList<World> worlds)
	{
		using (FrameProfiler.Instance.Measure("Build Snapshot"))
		{
			FrameSnapshot snapshot;
			using (FrameProfiler.Instance.Measure("Wait for snapshot"))
			{
				if (_renderGraph.TryBeginSnapshotWrite(out snapshot) == false)
				{
					return;
				}

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
			var renderWorldListChanged = HasRenderWorldListChanged(gpuDrawDatabase, worlds);
			var gpuHardeningStressEnabled = GraphicsConfig.GpuHardeningStressEnabled;
			var reconcilePersistentMeshes = renderWorldListChanged ||
			                                gpuHardeningStressEnabled ||
			                                (_gpuHardeningStressWasEnabled && gpuHardeningStressEnabled == false);
			_gpuHardeningStressWasEnabled = gpuHardeningStressEnabled;
			using (FrameProfiler.Instance.Measure("Begin Sync"))
			{
				gpuDrawDatabase.BeginSync(reconcilePersistentMeshes);
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
					if (reconcilePersistentMeshes)
					{
						foreach (var entry in world.View<MeshRenderer>())
						{
							if (world.HasComponent<DirtyWorldTransform>(entry.Entity))
							{
								world.GetComponent<DirtyWorldTransform>(entry.Entity).Consumed = 0;
							}
							else
							{
								world.AddComponent<DirtyWorldTransform>(entry.Entity);
							}
						}
					}

					foreach (var entry in world.View<WorldTransform, MeshRenderer, DirtyWorldTransform>())
					{
						ref var transform = ref entry.First;
						ref var meshRenderer = ref entry.Second;

						ref var dirty = ref entry.Third;
						dirty.Consumed++;
						if (world.IsEnabled(entry.Entity) == false)
						{
							gpuDrawDatabase.RemovePersistentMesh(entry.Entity);
							continue;
						}

						if (meshRenderer.TryValidate() == false) continue;

						var mesh = meshRenderer.Mesh;
						var material = meshRenderer.Material;

						if (gpuHardeningStressEnabled)
						{
							var churnKey = entry.Entity.Index + _stressFrame;
							if ((churnKey % 7) == 0)
							{
								// Force structural remove/add churn by skipping this entity for the frame.
								continue;
							}

							if ((churnKey % 5) == 0)
							{
								var toggled = ((_stressFrame / 30) & 1) == 0;
								material.AlphaMode = toggled ? AlphaMode.AlphaTest : AlphaMode.Opaque;
								material.AlphaCutoff = toggled ? 0.4f : 0.0f;
							}
						}


						var transformMatrix = transform.LocalToWorld;
						gpuDrawDatabase.TouchPersistentMesh(entry.Entity, mesh, material, transformMatrix);
					}
				}

				using (FrameProfiler.Instance.Measure("Gather skinned meshes"))
				{
					foreach (var entry in world.View<WorldTransform, SkinnedMeshRenderer>())
					{
						if (world.IsEnabled(entry.Entity) == false) continue;

						ref var transform = ref entry.First;
						ref var skinnedRenderer = ref entry.Second;

						if (skinnedRenderer.TryValidate() == false) continue;

						var sourceMesh = skinnedRenderer.Mesh;
						var material = skinnedRenderer.Material;

						// Every instance owns a copy of the mesh so the skinning pass has somewhere
						// private to write. It is a distinct Mesh reference, which is also what earns
						// it its own draw handle and its own bottom-level acceleration structure.
						skinnedRenderer.SkinnedInstance ??= sourceMesh.CreateSkinnedInstance(skinnedRenderer.BoundsExpansion);
						var instanceMesh = skinnedRenderer.SkinnedInstance;

						var animatorEntity = skinnedRenderer.AnimatorEntity.IsValid
							? skinnedRenderer.AnimatorEntity
							: entry.Entity;
						if (world.HasComponent<Animator>(animatorEntity) == false) continue;

						ref var animator = ref world.GetComponent<Animator>(animatorEntity);
						if (animator.SkinningMatrices is not { Length: > 0 } skinningMatrices) continue;
						if (animator.PreviousSkinningMatrices is not { Length: > 0 } previousSkinningMatrices) continue;

						snapshot.AddSkinning(sourceMesh, instanceMesh, skinningMatrices, previousSkinningMatrices);
						gpuDrawDatabase.TouchMesh(entry.Entity, instanceMesh, material, transform.LocalToWorld);
					}
				}

				using (FrameProfiler.Instance.Measure("Clean used dirties"))
				{
					_dirtyWorldTransformRemovalScratch.Clear();
					foreach (var entry in world.View<DirtyWorldTransform>())
					{
						ref var dirty = ref entry.First;
						if (dirty.Consumed >= DirtyWorldTransformSyncCount ||
						    (dirty.Consumed == 0 && world.HasComponent<MeshRenderer>(entry.Entity) == false))
						{
							_dirtyWorldTransformRemovalScratch.Add(entry.Entity);
						}
					}

					for (var dirtyIndex = 0; dirtyIndex < _dirtyWorldTransformRemovalScratch.Count; dirtyIndex++)
					{
						world.RemoveComponent<DirtyWorldTransform>(_dirtyWorldTransformRemovalScratch[dirtyIndex]);
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
			if (_renderGraph.TryPublishSnapshot() == false)
			{
				return;
			}


			_stressFrame++;
		}
	}

	private bool HasRenderWorldListChanged(GpuDrawDatabase database, IReadOnlyList<World> worlds)
	{
		if (_renderWorldsByDatabase.TryGetValue(database, out var previousWorlds) == false)
		{
			previousWorlds = new List<World>();
			_renderWorldsByDatabase.Add(database, previousWorlds);
		}

		var worldIndex = 0;
		var changed = false;
		for (var i = 0; i < (worlds?.Count ?? 0); i++)
		{
			var world = worlds![i];
			if (world is null)
			{
				continue;
			}

			if (worldIndex >= previousWorlds.Count || ReferenceEquals(previousWorlds[worldIndex], world) == false)
			{
				changed = true;
			}
			worldIndex++;
		}

		if (worldIndex != previousWorlds.Count)
		{
			changed = true;
		}
		if (changed == false)
		{
			return false;
		}

		previousWorlds.Clear();
		for (var i = 0; i < (worlds?.Count ?? 0); i++)
		{
			var world = worlds![i];
			if (world is not null)
			{
				previousWorlds.Add(world);
			}
		}
		return true;
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
						transform,
						new TerrainChunkInstanceData(
							new Vector4(probeIndex, DdgiDebugProbeInstanceMarker, 0.0f, 0.0f),
							Vector4.Zero));
					probeIndex++;
				}
			}
		}
	}

	internal static void CollectDecalProjectors(
		FrameSnapshot snapshot,
		World world,
		IRenderResourceScheduler resourceScheduler)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(resourceScheduler);

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

			projector.EnsureTextureResources(resourceScheduler);
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

			previewProjector.EnsureTextureResources(resourceScheduler);
			snapshot.AddDecal(
				previewProjector,
				terrain.AuthoringBrushPreviewLocalTransform * terrainTransform.LocalToWorld);
		}
	}
}
