using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class ClusteredLightingPass
{
	public enum Stage
	{
		BuildClusters,
		CountLights,
		PrefixOffsets,
		WriteLightIndices
	}

	private readonly IShaderCompiler _shaderCompiler;
	private GraphicsBackendKind? _compiledBackendKind;
	private IGfxPipeline? _buildClustersPipeline;
	private IGfxPipeline? _countLightsPipeline;
	private IGfxPipeline? _prefixOffsetsPipeline;
	private IGfxPipeline? _writeLightIndicesPipeline;
	private ComputeThreadGroupSize? _buildClustersThreadGroupSize;
	private ComputeThreadGroupSize? _countLightsThreadGroupSize;
	private ComputeThreadGroupSize? _prefixOffsetsThreadGroupSize;
	private ComputeThreadGroupSize? _writeLightIndicesThreadGroupSize;
	private ShaderPropertyWriter? _cameraWriter;
	private ShaderPropertyWriter? _clusterWriter;

	public ClusteredLightingPass(IShaderCompiler shaderCompiler)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
	}

	public ClusteredLightingPassConfig BuildConfig(
		IGfxDevice device,
		GpuDrawResources gpuDrawResources,
		Int2 framebufferSize)
	{
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);

		gpuDrawResources.EnsureCreated(device);
		gpuDrawResources.EnsureClusteredLightingCapacity(device, framebufferSize);
		EnsurePipelines(device);

		return new ClusteredLightingPassConfig
		{
			BuildClustersPipeline = _buildClustersPipeline ?? throw new InvalidOperationException("Cluster build pipeline missing."),
			CountLightsPipeline = _countLightsPipeline ?? throw new InvalidOperationException("Cluster count pipeline missing."),
			PrefixOffsetsPipeline = _prefixOffsetsPipeline ?? throw new InvalidOperationException("Cluster prefix pipeline missing."),
			WriteLightIndicesPipeline = _writeLightIndicesPipeline ?? throw new InvalidOperationException("Cluster write pipeline missing."),
			PointLightBuffer = gpuDrawResources.ClusterPointLightBuffer ?? throw new InvalidOperationException("Cluster point-light buffer missing."),
			ClusterAabbBuffer = gpuDrawResources.ClusterAabbBuffer ?? throw new InvalidOperationException("Cluster AABB buffer missing."),
			ClusterHeaderBuffer = gpuDrawResources.ClusterHeaderBuffer ?? throw new InvalidOperationException("Cluster header buffer missing."),
			ClusterLightIndexBuffer = gpuDrawResources.ClusterLightIndexBuffer ?? throw new InvalidOperationException("Cluster index buffer missing."),
			ClusterWriteCursorBuffer = gpuDrawResources.ClusterWriteCursorBuffer ?? throw new InvalidOperationException("Cluster cursor buffer missing."),
			ClusterOverflowBuffer = gpuDrawResources.ClusterOverflowBuffer ?? throw new InvalidOperationException("Cluster overflow buffer missing."),
			Grid = gpuDrawResources.ClusteredLightingLayout.Grid,
			FramebufferSize = framebufferSize,
			ClusterCount = gpuDrawResources.ClusteredLightingLayout.ClusterCount,
			LightIndexCapacity = gpuDrawResources.ClusteredLightingLayout.LightIndexCapacity
		};
	}

	public void Record(RenderGraphContext context, in ClusteredLightingPassConfig config, SceneDrawData sceneData, Stage stage)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		var cameraWriter = _cameraWriter
			?? throw new InvalidOperationException("Clustered lighting camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("invProjection", sceneData.InverseProjection);
		cameraWriter.SetMatrix4x4("viewMatrix", sceneData.ViewMatrix);

		var clusterWriter = _clusterWriter
			?? throw new InvalidOperationException("Clustered lighting params writer was not initialized.");
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
		clusterWriter.SetUInt("lightIndexCapacity", (uint)Math.Max(config.LightIndexCapacity, 1));

		if (stage == Stage.BuildClusters)
		{
			UploadPointLights(config.PointLightBuffer, sceneData);
			ClearOverflowBuffer(config.ClusterOverflowBuffer);
		}

		commandList.BindPipeline(stage switch
		{
			Stage.BuildClusters => config.BuildClustersPipeline,
			Stage.CountLights => config.CountLightsPipeline,
			Stage.PrefixOffsets => config.PrefixOffsetsPipeline,
			Stage.WriteLightIndices => config.WriteLightIndicesPipeline,
			_ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
		});
		commandList.SetComputeConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());
		commandList.SetComputeConstants(clusterWriter.RegisterIndex, clusterWriter.AsBytes());
		BindComputeBuffers(commandList, config);
		var threadGroupSize = GetThreadGroupSize(stage);
		var workItemCount = stage == Stage.PrefixOffsets ? 1u : (uint)Math.Max(config.ClusterCount, 1);
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(workItemCount);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void EnsurePipelines(IGfxDevice device)
	{
		var backendKind = device.BackendKind;
		if (_compiledBackendKind == backendKind &&
		    _buildClustersPipeline is not null &&
		    _countLightsPipeline is not null &&
		    _prefixOffsetsPipeline is not null &&
		    _writeLightIndicesPipeline is not null &&
		    _buildClustersThreadGroupSize.HasValue &&
		    _countLightsThreadGroupSize.HasValue &&
		    _prefixOffsetsThreadGroupSize.HasValue &&
		    _writeLightIndicesThreadGroupSize.HasValue &&
		    _cameraWriter is not null &&
		    _clusterWriter is not null)
		{
			return;
		}

		var build = _shaderCompiler.GetComputeShaderWithReflection(
			"clustered_lighting.compute.slang",
			"CSBuildClusters",
			backendKind);
		var count = _shaderCompiler.GetComputeShaderWithReflection(
			"clustered_lighting.compute.slang",
			"CSCountLights",
			backendKind);
		var prefix = _shaderCompiler.GetComputeShaderWithReflection(
			"clustered_lighting.compute.slang",
			"CSPrefixOffsets",
			backendKind);
		var write = _shaderCompiler.GetComputeShaderWithReflection(
			"clustered_lighting.compute.slang",
			"CSWriteLightIndices",
			backendKind);
		_buildClustersThreadGroupSize = build.ThreadGroupSize;
		_countLightsThreadGroupSize = count.ThreadGroupSize;
		_prefixOffsetsThreadGroupSize = prefix.ThreadGroupSize;
		_writeLightIndicesThreadGroupSize = write.ThreadGroupSize;

		_buildClustersPipeline = device.GetOrCreatePipeline(
			new PipelineKey(PassKind.Compute, null, null, "CSBuildClusters", default, default, default, shaderVariant: "clustered_lighting.compute.slang"),
			new ShaderBytecodeSet(compute: build.Bytecode, computeThreadGroupSize: _buildClustersThreadGroupSize));
		_countLightsPipeline = device.GetOrCreatePipeline(
			new PipelineKey(PassKind.Compute, null, null, "CSCountLights", default, default, default, shaderVariant: "clustered_lighting.compute.slang"),
			new ShaderBytecodeSet(compute: count.Bytecode, computeThreadGroupSize: _countLightsThreadGroupSize));
		_prefixOffsetsPipeline = device.GetOrCreatePipeline(
			new PipelineKey(PassKind.Compute, null, null, "CSPrefixOffsets", default, default, default, shaderVariant: "clustered_lighting.compute.slang"),
			new ShaderBytecodeSet(compute: prefix.Bytecode, computeThreadGroupSize: _prefixOffsetsThreadGroupSize));
		_writeLightIndicesPipeline = device.GetOrCreatePipeline(
			new PipelineKey(PassKind.Compute, null, null, "CSWriteLightIndices", default, default, default, shaderVariant: "clustered_lighting.compute.slang"),
			new ShaderBytecodeSet(compute: write.Bytecode, computeThreadGroupSize: _writeLightIndicesThreadGroupSize));
		_cameraWriter = new ShaderPropertyWriter(build.ReflectionLayout.GetConstantBuffer("CameraParams"));
		_clusterWriter = new ShaderPropertyWriter(build.ReflectionLayout.GetConstantBuffer("ClusterParams"));
		_compiledBackendKind = backendKind;
	}

	private ComputeThreadGroupSize GetThreadGroupSize(Stage stage)
	{
		return stage switch
		{
			Stage.BuildClusters => _buildClustersThreadGroupSize
				?? throw new InvalidOperationException("Cluster build threadgroup size was not initialized."),
			Stage.CountLights => _countLightsThreadGroupSize
				?? throw new InvalidOperationException("Cluster count threadgroup size was not initialized."),
			Stage.PrefixOffsets => _prefixOffsetsThreadGroupSize
				?? throw new InvalidOperationException("Cluster prefix threadgroup size was not initialized."),
			Stage.WriteLightIndices => _writeLightIndicesThreadGroupSize
				?? throw new InvalidOperationException("Cluster write threadgroup size was not initialized."),
			_ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
		};
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
