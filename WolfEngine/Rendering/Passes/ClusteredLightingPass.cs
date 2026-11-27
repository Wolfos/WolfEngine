using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class ClusteredLightingPass
{
	public enum Stage
	{
		BuildClusters,
		WriteLightIndices
	}

	private readonly ComputeKernel _buildClusters;
	private readonly ComputeKernel _writeLightIndices;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly Action<RenderGraphContext> _buildExecute;
	private readonly Action<RenderGraphContext> _writeExecute;

	public ClusteredLightingPass(ShaderSource shaderSource, GpuDrawResources gpuDrawResources)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_buildClusters = new ComputeKernel(shaderSource, EngineShaderPrograms.ClusteredLighting, "CSBuildClusters");
		_writeLightIndices = new ComputeKernel(shaderSource, EngineShaderPrograms.ClusteredLighting, "CSWriteLightIndices");
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_buildExecute = context => Execute(context, Stage.BuildClusters);
		_writeExecute = context => Execute(context, Stage.WriteLightIndices);
	}

	/// <summary>
	/// Adds the cluster build and the light index write. Their buffers live in the draw resources, outside the
	/// graph's tracking, so neither node declares anything.
	/// </summary>
	internal void AddNodes(RenderGraph graph)
	{
		graph.AddPass("Clustered Lighting Build", PassKind.Compute)
			.SetExecute(_buildExecute);
		graph.AddPass("Clustered Lighting Write", PassKind.Compute)
			.SetExecute(_writeExecute);
	}

	private void Execute(RenderGraphContext context, Stage stage)
	{
		var config = BuildConfig(
			context.Device,
			_gpuDrawResources,
			context.ViewState.FrameResources.SceneFramebufferSize);
		Record(context, in config, context.SceneData, stage);
	}

	private ClusteredLightingPassConfig BuildConfig(
		IGfxDevice device,
		GpuDrawResources gpuDrawResources,
		Int2 framebufferSize)
	{
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);

		gpuDrawResources.EnsureCreated(device);
		gpuDrawResources.EnsureClusteredLightingCapacity(device, framebufferSize);

		return new ClusteredLightingPassConfig
		{
			BuildClustersPipeline = _buildClusters.GetPipeline(device),
			WriteLightIndicesPipeline = _writeLightIndices.GetPipeline(device),
			PointLightBuffer = gpuDrawResources.ClusterPointLightBuffer ?? throw new InvalidOperationException("Cluster point-light buffer missing."),
			ClusterAabbBuffer = gpuDrawResources.ClusterAabbBuffer ?? throw new InvalidOperationException("Cluster AABB buffer missing."),
			ClusterHeaderBuffer = gpuDrawResources.ClusterHeaderBuffer ?? throw new InvalidOperationException("Cluster header buffer missing."),
			ClusterLightIndexBuffer = gpuDrawResources.ClusterLightIndexBuffer ?? throw new InvalidOperationException("Cluster index buffer missing."),
			ClusterWriteCursorBuffer = gpuDrawResources.ClusterWriteCursorBuffer ?? throw new InvalidOperationException("Cluster cursor buffer missing."),
			ClusterOverflowBuffer = gpuDrawResources.ClusterOverflowBuffer ?? throw new InvalidOperationException("Cluster overflow buffer missing."),
			Grid = gpuDrawResources.ClusteredLightingLayout.Grid,
			FramebufferSize = framebufferSize,
			ClusterCount = gpuDrawResources.ClusteredLightingLayout.ClusterCount
		};
	}

	private void Record(RenderGraphContext context, in ClusteredLightingPassConfig config, SceneDrawData sceneData, Stage stage)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var kernel = stage switch
		{
			Stage.BuildClusters => _buildClusters,
			Stage.WriteLightIndices => _writeLightIndices,
			_ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
		};
		var commandList = context.CommandList;
		var cameraWriter = kernel.Writer("CameraParams");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("invProjection", sceneData.InverseProjection);
		cameraWriter.SetMatrix4x4("viewMatrix", sceneData.ViewMatrix);

		var clusterWriter = kernel.Writer("ClusterParams");
		clusterWriter.Clear();
		clusterWriter.SetUInt("clusterCountX", (uint)config.Grid.X);
		clusterWriter.SetUInt("clusterCountY", (uint)config.Grid.Y);
		clusterWriter.SetUInt("clusterCountZ", (uint)config.Grid.Z);
		clusterWriter.SetUInt("clusterCount", (uint)config.ClusterCount);
		clusterWriter.SetUInt("framebufferWidth", (uint)Math.Max(config.FramebufferSize.X, 1));
		clusterWriter.SetUInt("framebufferHeight", (uint)Math.Max(config.FramebufferSize.Y, 1));
		clusterWriter.SetFloat("nearPlane", Math.Max(sceneData.NearPlane, 0.0001f));
		clusterWriter.SetFloat("farPlane", Math.Max(sceneData.FarPlane, sceneData.NearPlane + 0.001f));
		clusterWriter.SetUInt("pointLightCount", (uint)CountPointLights(sceneData));

		if (stage == Stage.BuildClusters)
		{
			UploadPointLights(config.PointLightBuffer, sceneData);
			ClearOverflowBuffer(config.ClusterOverflowBuffer);
		}

		commandList.BindPipeline(stage switch
		{
			Stage.BuildClusters => config.BuildClustersPipeline,
			Stage.WriteLightIndices => config.WriteLightIndicesPipeline,
			_ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
		});
		commandList.SetComputeConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());
		commandList.SetComputeConstants(clusterWriter.RegisterIndex, clusterWriter.AsBytes());
		BindComputeBuffers(commandList, config);
		var threadGroupSize = kernel.ThreadGroupSize;
		var workItemCount = (uint)Math.Max(config.ClusterCount, 1);
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(workItemCount);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private int UploadPointLights(IGfxBuffer buffer, SceneDrawData sceneData)
	{
		if (buffer is not IWritableGpuBuffer writableBuffer)
		{
			return 0;
		}

		Span<PointLightGpuData> points = stackalloc PointLightGpuData[ClusteredLightingShared.MaxPointLights];
		var pointLightCount = 0;
		for (var i = 0; i < sceneData.Lights.Count && pointLightCount < points.Length; i++)
		{
			var packet = sceneData.Lights[i];
			if (packet.Light.Type != LightType.Point)
			{
				continue;
			}

			var position = packet.Transform.Translation;
			var viewPosition = Vector3.Transform(position, sceneData.ViewMatrix);
			var range = MathF.Max(packet.Light.Range, 0.001f);
			points[pointLightCount] = new PointLightGpuData
			{
				ColorIntensity = new Vector4(
					packet.Light.Color.R,
					packet.Light.Color.G,
					packet.Light.Color.B,
					packet.Light.Intensity),
				WorldPositionRange = new Vector4(position, range),
				ViewPositionRange = new Vector4(viewPosition, range)
			};
			pointLightCount++;
		}

		if (pointLightCount > 0)
		{
			writableBuffer.Write<PointLightGpuData>(points[..pointLightCount]);
		}

		return pointLightCount;
	}

	private static int CountPointLights(SceneDrawData sceneData)
	{
		var count = 0;
		for (var i = 0; i < sceneData.Lights.Count && count < ClusteredLightingShared.MaxPointLights; i++)
		{
			if (sceneData.Lights[i].Light.Type == LightType.Point)
			{
				count++;
			}
		}

		return count;
	}

	private static void ClearOverflowBuffer(IGfxBuffer overflowBuffer)
	{
		if (overflowBuffer is not IWritableGpuBuffer writableBuffer)
		{
			return;
		}

		Span<uint> clear = stackalloc uint[2];
		clear.Clear();
		writableBuffer.Write<uint>(clear);
	}

	private static void BindComputeBuffers(IGfxCommandList commandList, in ClusteredLightingPassConfig config)
	{
		commandList.SetComputeBuffer(0, config.PointLightBuffer);
		commandList.SetComputeBuffer(1, config.ClusterAabbBuffer);
		commandList.SetComputeBuffer(2, config.ClusterHeaderBuffer);
		commandList.SetComputeBuffer(3, config.ClusterLightIndexBuffer);
		commandList.SetComputeBuffer(4, config.ClusterWriteCursorBuffer);
		commandList.SetComputeBuffer(5, config.ClusterOverflowBuffer);
	}

}
