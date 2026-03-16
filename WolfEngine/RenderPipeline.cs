using WolfEngine.ECS;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using System.Numerics;
using WolfEngine.Rendering.Passes;

namespace WolfEngine;

public interface IRenderPipeline
{
	void Run();

	void PublishSnapshot(Camera camera, WorldTransform cameraWorldTransform, RenderConfig config,
		IReadOnlyList<World> worlds);
}

public class RenderPipeline : IRenderPipeline
{
	private readonly RenderGraph _renderGraph;
	private readonly GpuDrawDatabase _drawDatabase;
	private int _stressFrame;

	public RenderPipeline(
		RenderGraph renderGraph,
		GpuDrawDatabase drawDatabase)
	{
		_renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
		_drawDatabase = drawDatabase ?? throw new ArgumentNullException(nameof(drawDatabase));
	}

	public void Run()
	{
		_renderGraph.Startup(static () => { }, static _ => { });
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
			using (FrameProfiler.Instance.Measure("Begin Sync"))
			{
				_drawDatabase.BeginSync();
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
						_drawDatabase.Touch(entry.Entity, meshRenderer.Mesh, meshRenderer.Material, transformMatrix);
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


			_drawDatabase.EndSync();
			_renderGraph.PublishSnapshot();


			_stressFrame++;
		}
	}
}
