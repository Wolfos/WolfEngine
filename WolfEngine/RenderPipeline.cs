using WolfEngine.Animation;
using WolfEngine.ECS;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using System.Numerics;
using WolfEngine.Rendering.Passes;

namespace WolfEngine;

public readonly record struct RenderViewSubmission(
	RenderViewId View,
	Camera Camera,
	WorldTransform CameraWorldTransform,
	RenderConfig Config);

public interface IRenderPipeline
{
	void Run(Action? startup = null);

	void PublishSnapshot(Camera camera, WorldTransform cameraWorldTransform, RenderConfig config,
		IReadOnlyList<World> worlds);

	void PublishSnapshot(IReadOnlyList<RenderViewSubmission> views);
}

public class RenderPipeline : IRenderPipeline
{
	// Both snapshot databases consume the change, then one additional snapshot settles motion history.
	private const int DirtyWorldTransformSyncCount = 3;
	// Both snapshot databases consume the removal; motion history is dropped along with the draw.
	private const int WorldTransformRemovalSyncCount = 2;
	private const int DdgiDebugProbeEntityBaseIndex = -2_000_000_000;
	private const float DdgiDebugProbeInstanceMarker = 1.0f;
	private readonly RenderGraph _renderGraph;
	private readonly TerrainRuntimeCache _terrainRuntimeCache = new();
	private readonly DebugPrimitiveMeshFactory _debugPrimitiveMeshFactory = new();
	private readonly Dictionary<GpuDrawDatabase, List<World>> _renderWorldsByDatabase = new();
	private readonly Dictionary<GpuDrawDatabase, int> _forcedReconcileSnapshotsByDatabase = new();
	private readonly Dictionary<GpuDrawDatabase, bool> _gpuHardeningStressByDatabase = new();
	private readonly List<Entity> _dirtyWorldTransformRemovalScratch = new();
	private int _stressFrame;

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
			FrameSnapshot frameSnapshot;
			RenderViewSnapshot snapshot;
			using (FrameProfiler.Instance.Measure("Wait for snapshot"))
			{
				if (_renderGraph.TryBeginSnapshotWrite(out frameSnapshot) == false)
				{
					return;
				}

				snapshot = frameSnapshot.GetOrCreateView(RenderViewId.Primary);
				PrepareViewSnapshot(snapshot, camera, cameraWorldTransform, config);
			}

			PopulateSnapshot(snapshot, cameraWorldTransform, config, worlds);
			if (_renderGraph.TryPublishSnapshot() == false)
			{
				return;
			}

			_stressFrame++;
		}
	}

	public void PublishSnapshot(IReadOnlyList<RenderViewSubmission> views)
	{
		ArgumentNullException.ThrowIfNull(views);
		if (views.Count == 0)
		{
			throw new ArgumentException("At least one render view submission is required.", nameof(views));
		}

		var worlds = new World[views.Count];
		var bindingGenerations = new long[views.Count];
		var submittedViews = new HashSet<RenderViewId>();
		for (var i = 0; i < views.Count; i++)
		{
			var submission = views[i];
			if (submission.View.IsValid == false)
			{
				throw new ArgumentException($"Submission {i} has an invalid render view id.", nameof(views));
			}
			if (submittedViews.Add(submission.View) == false)
			{
				throw new ArgumentException($"View {submission.View} was submitted more than once.", nameof(views));
			}
			if (_renderGraph.TryGetViewBinding(submission.View, out worlds[i], out bindingGenerations[i]) == false)
			{
				throw new InvalidOperationException(
					$"View {submission.View} is not bound to a world. Create the view before submitting it.");
			}
			ArgumentNullException.ThrowIfNull(submission.Config);
		}

		using (FrameProfiler.Instance.Measure("Build Snapshot"))
		{
			if (_renderGraph.TryBeginSnapshotWrite(out var frameSnapshot) == false)
			{
				return;
			}

			for (var i = 0; i < views.Count; i++)
			{
				var submission = views[i];
				var snapshot = frameSnapshot.BindView(submission.View, worlds[i], bindingGenerations[i]);
				PrepareViewSnapshot(
					snapshot,
					submission.Camera,
					submission.CameraWorldTransform,
					submission.Config);
				PopulateSnapshot(
					snapshot,
					submission.CameraWorldTransform,
					submission.Config,
					[worlds[i]]);
			}

			// Every database copies the process-wide generation tables after all gathers. A later view can
			// allocate a shared draw, mesh or material slot that an earlier view must see this same frame.
			for (var i = 0; i < frameSnapshot.Views.Count; i++)
			{
				frameSnapshot.Views[i].GpuDrawDatabase.RefreshSharedHandleState();
			}

			if (_renderGraph.TryPublishSnapshot() == false)
			{
				return;
			}

			_stressFrame++;
		}
	}

	private void PrepareViewSnapshot(
		RenderViewSnapshot snapshot,
		Camera camera,
		WorldTransform cameraWorldTransform,
		RenderConfig config)
	{
		snapshot.SetCamera(camera, cameraWorldTransform);
		snapshot.SetConfig(config);

		var lookupTableRef = config.ColorGrading.LookupTable;
		var lookupTable = lookupTableRef.IsValid ? lookupTableRef.Asset : null;
		if (lookupTable is not null)
		{
			_renderGraph.EnsureTextureResources(lookupTable.Texture);
		}

		snapshot.SetColorGradingLookupTable(lookupTable);
	}

	private void PopulateSnapshot(
		RenderViewSnapshot snapshot,
		WorldTransform cameraWorldTransform,
		RenderConfig config,
		IReadOnlyList<World> worlds)
	{
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
			if (ConsumeWorldTransformRemovalOverflow(worlds))
			{
				// Removals were dropped; both snapshot databases rebuild from the live worlds.
				_forcedReconcileSnapshotsByDatabase[gpuDrawDatabase] = WorldTransformRemovalSyncCount;
			}
			_forcedReconcileSnapshotsByDatabase.TryGetValue(gpuDrawDatabase, out var forcedReconcileSnapshotCount);
			_gpuHardeningStressByDatabase.TryGetValue(gpuDrawDatabase, out var gpuHardeningStressWasEnabled);
			var reconcilePersistentMeshes = renderWorldListChanged ||
			                                gpuHardeningStressEnabled ||
			                                (gpuHardeningStressWasEnabled && gpuHardeningStressEnabled == false) ||
			                                forcedReconcileSnapshotCount > 0;
			_gpuHardeningStressByDatabase[gpuDrawDatabase] = gpuHardeningStressEnabled;
			if (forcedReconcileSnapshotCount > 1)
			{
				_forcedReconcileSnapshotsByDatabase[gpuDrawDatabase] = forcedReconcileSnapshotCount - 1;
			}
			else if (forcedReconcileSnapshotCount == 1)
			{
				_forcedReconcileSnapshotsByDatabase.Remove(gpuDrawDatabase);
			}
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

				gpuDrawDatabase.BeginWorld(world.Id);

				using (FrameProfiler.Instance.Measure("Remove meshes"))
				{
					RemoveMeshesForWorldTransformRemovals(world, gpuDrawDatabase);
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

				using (FrameProfiler.Instance.Measure("Gather outlines"))
				{
					// Runs after the skinned gather so a skinned entity's per-instance mesh
					// already exists; outlining the source mesh would trace the bind pose.
					foreach (var entry in world.View<WorldTransform, OutlineHighlight>())
					{
						if (world.IsEnabled(entry.Entity) == false)
						{
							continue;
						}

						if (TryResolveOutlineMesh(world, entry.Entity, out var outlineMesh) == false)
						{
							continue;
						}

						ref var outline = ref entry.Second;
						snapshot.AddOutline(
							outlineMesh,
							entry.First.LocalToWorld,
							outline.Color,
							outline.GetResolvedThicknessPixels());
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

				using (FrameProfiler.Instance.Measure("Gather fog volumes"))
				{
					foreach (var entry in world.View<WorldTransform, FogVolume>())
					{
						if (world.IsEnabled(entry.Entity) && entry.Second.IsValid)
						{
							snapshot.AddFogVolume(entry.Second, entry.First.LocalToWorld);
						}
					}
				}
			}

			using (FrameProfiler.Instance.Measure("Gather DDGI probe debug primitives"))
			{
				// Debug probes use synthetic negative entity ids. Their world scope is view-specific too:
				// two views must never share a draw handle just because both show probe number zero.
				gpuDrawDatabase.BeginWorld(-snapshot.View.Index - 1);
				CollectDdgiProbeDebugPrimitives(
					config,
					cameraOrigin,
					gpuDrawDatabase,
					_debugPrimitiveMeshFactory);
			}

			gpuDrawDatabase.EndSync();
	}

	internal static void RemoveMeshesForWorldTransformRemovals(World world, GpuDrawDatabase gpuDrawDatabase)
	{
		var removals = world.WorldTransformRemovals;
		for (var i = 0; i < removals.Length; i++)
		{
			ref var removal = ref removals[i];
			removal.Consumed++;

			var entity = removal.Entity;
			if (world.IsAlive(entity) &&
			    world.HasComponent<WorldTransform>(entity) &&
			    world.HasComponent<MeshRenderer>(entity))
			{
				// Another component was removed, or the renderer was added back; resync instead of removing.
				world.MarkWorldTransformChanged(entity);
				continue;
			}

			gpuDrawDatabase.RemovePersistentMesh(entity);
		}

		world.PruneWorldTransformRemovals(WorldTransformRemovalSyncCount);
	}

	private static bool ConsumeWorldTransformRemovalOverflow(IReadOnlyList<World> worlds)
	{
		var overflowed = false;
		for (var i = 0; i < (worlds?.Count ?? 0); i++)
		{
			if (worlds![i] is { } world && world.ConsumeWorldTransformRemovalOverflow())
			{
				overflowed = true;
			}
		}

		return overflowed;
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
		IRenderResourceScheduler resourceScheduler) =>
		CollectDecalProjectors(snapshot.GetOrCreateView(RenderViewId.Primary), world, resourceScheduler);

	internal static void CollectDecalProjectors(
		RenderViewSnapshot snapshot,
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

	/// <summary>
	/// The mesh whose silhouette an outlined entity should trace. Entities without
	/// renderable geometry -- lights, cameras, bare transforms -- have none, and
	/// terrain is excluded because its silhouette is the whole screen edge.
	/// </summary>
	private static bool TryResolveOutlineMesh(World world, Entity entity, out Mesh mesh)
	{
		mesh = null!;
		if (world.HasComponent<SkinnedMeshRenderer>(entity))
		{
			ref var skinnedRenderer = ref world.GetComponent<SkinnedMeshRenderer>(entity);
			if (skinnedRenderer.SkinnedInstance is not { } skinnedInstance)
			{
				return false;
			}

			mesh = skinnedInstance;
			return true;
		}

		if (world.HasComponent<MeshRenderer>(entity) == false)
		{
			return false;
		}

		ref var meshRenderer = ref world.GetComponent<MeshRenderer>(entity);
		if (meshRenderer.TryValidate() == false)
		{
			return false;
		}

		mesh = meshRenderer.Mesh;
		return true;
	}

}
