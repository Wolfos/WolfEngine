#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using WolfEngine.Platform;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;

namespace WolfEngine.Rendering.Passes;

public sealed class GpuDrawPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly GpuDrawDatabase _drawDatabase;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly IRenderer _renderer;
	private IGfxPipeline? _updatePipeline;
	private IGfxPipeline? _cullPipeline;
	private readonly List<GpuDrawUpdate> _updates = new();
	private readonly List<GpuDrawUpdateData> _updateData = new();
	
	public GpuDrawPass(IShaderCompiler shaderCompiler, GpuDrawDatabase drawDatabase,
		BindlessResourceRegistry bindlessRegistry, GpuDrawResources gpuDrawResources, IRenderer renderer)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_drawDatabase = drawDatabase ?? throw new ArgumentNullException(nameof(drawDatabase));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
	}

	public void RecordUpdate(RenderGraphContext context)
	{
		var device = _renderer.GetGfxDevice();
		_bindlessRegistry.EnsureInitialized(device);
		_gpuDrawResources.EnsureCreated(device);

		_drawDatabase.ConsumeUpdates(_updates);
		_updateData.Clear();

		var updateCount = Math.Min(_updates.Count, GpuDrawResources.MaxDrawCount);

		for (var i = 0; i < updateCount; i++)
		{
			var update = _updates[i];
			var mesh = update.Mesh;
			var material = update.Material;

			if (mesh is not null)
			{
				_renderer.EnsureMeshResources(mesh);
			}

			uint vertexHandle = _bindlessRegistry.ErrorBufferHandle.Value;
			uint indexHandle = _bindlessRegistry.ErrorBufferHandle.Value;
			uint indexCount = 0;
			uint indexFormat = 0;
			int baseVertex = 0;

			if (mesh?.VertexBuffer is not null && mesh.IndexBuffer is not null)
			{
				vertexHandle = _bindlessRegistry.RegisterBuffer(mesh.VertexBuffer).Value;
				indexHandle = _bindlessRegistry.RegisterBuffer(mesh.IndexBuffer).Value;
				indexCount = mesh.IndexCount;
				indexFormat = 0;
			}

			uint albedoHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint mrHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint normalHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint occlusionHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint emissiveHandle = _bindlessRegistry.ErrorTextureHandle.Value;
			uint samplerHandle = _bindlessRegistry.ErrorSamplerHandle.Value;
			var baseColor = Vector4.One;
			var metallicRoughness = Vector4.One;

			var materialResources = material?.Resources;
			if (materialResources is not null)
			{
				albedoHandle = materialResources.AlbedoTexture.IsValid
					? materialResources.AlbedoTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				mrHandle = materialResources.MetallicRoughnessTexture.IsValid
					? materialResources.MetallicRoughnessTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				normalHandle = materialResources.NormalTexture.IsValid
					? materialResources.NormalTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				occlusionHandle = materialResources.OcclusionTexture.IsValid
					? materialResources.OcclusionTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				emissiveHandle = materialResources.EmissiveTexture.IsValid
					? materialResources.EmissiveTexture.Value
					: _bindlessRegistry.ErrorTextureHandle.Value;
				samplerHandle = materialResources.Sampler.IsValid
					? materialResources.Sampler.Value
					: _bindlessRegistry.ErrorSamplerHandle.Value;
				baseColor = material!.Color;
				metallicRoughness = new Vector4(material.MetallicFactor, material.RoughnessFactor, 0, 0);
			}

			if ((albedoHandle >> 30) != 0 || (mrHandle >> 30) != 0 ||
			    (normalHandle >> 30) != 0 || (occlusionHandle >> 30) != 0 ||
			    (emissiveHandle >> 30) != 0 || (samplerHandle >> 30) != 3)
			{
				Console.WriteLine($"Bindless handle kind mismatch (drawId {update.DrawId}, matId {update.MaterialId}): " +
				                  $"A={albedoHandle} MR={mrHandle} N={normalHandle} O={occlusionHandle} E={emissiveHandle} S={samplerHandle}");
			}

			_updateData.Add(new GpuDrawUpdateData(
				update.World,
				update.BoundsCenterRadius,
				baseColor,
				metallicRoughness,
				(uint)update.Type,
				(uint)update.DrawId,
				(uint)update.InstanceId,
				(uint)update.MeshId,
				(uint)update.MaterialId,
				vertexHandle,
				indexHandle,
				indexCount,
				indexFormat,
				baseVertex,
				albedoHandle,
				mrHandle,
				normalHandle,
				occlusionHandle,
				emissiveHandle,
				samplerHandle));
		}

		if (_updateData.Count == 0)
		{
			return;
		}

		var updateBuffer = _gpuDrawResources.UpdateBuffer;
		if (updateBuffer is MetalBuffer metalBuffer)
		{
			BufferHelper.CopyToBuffer(_updateData.ToArray(), metalBuffer.Buffer);
		}

		var pipeline = EnsureUpdatePipeline(device);
		var commandList = context.CommandList;
		commandList.BindPipeline(pipeline);

		Span<uint> updateParams = stackalloc uint[4];
		updateParams[0] = (uint)_updateData.Count;
		updateParams[1] = 0;
		updateParams[2] = 0;
		updateParams[3] = 0;
		commandList.SetComputeConstants(5, MemoryMarshal.AsBytes(updateParams));

		commandList.SetComputeBuffer(0, _gpuDrawResources.UpdateBuffer!);
		commandList.SetComputeBuffer(1, _gpuDrawResources.InstanceBuffer!);
		commandList.SetComputeBuffer(2, _gpuDrawResources.MaterialBuffer!);
		commandList.SetComputeBuffer(3, _gpuDrawResources.MeshBuffer!);
		commandList.SetComputeBuffer(4, _gpuDrawResources.DrawCommandBuffer!);

		var groupCount = (uint)((_updateData.Count + 63) / 64);
		commandList.Dispatch(groupCount, 1, 1);
	}

	public void RecordCull(RenderGraphContext context, SceneDrawData sceneData)
	{
		var device = _renderer.GetGfxDevice();
		_bindlessRegistry.EnsureInitialized(device);
		_gpuDrawResources.EnsureCreated(device);

		if (_gpuDrawResources.DrawCountBuffer is MetalBuffer countBuffer)
		{
			Span<uint> reset = stackalloc uint[1];
			reset[0] = 0;
			BufferHelper.CopyToBuffer(reset.ToArray(), countBuffer.Buffer);
		}
		if (_gpuDrawResources.DrawExecutionRangeBuffer is MetalBuffer rangeBuffer)
		{
			Span<uint> resetRange = stackalloc uint[2];
			resetRange[0] = 0;
			resetRange[1] = 0;
			BufferHelper.CopyToBuffer(resetRange.ToArray(), rangeBuffer.Buffer);
		}

		var pipeline = EnsureCullPipeline(device);
		var commandList = context.CommandList;
		commandList.BindPipeline(pipeline);

		Span<Vector4> planes = stackalloc Vector4[6];
		ExtractFrustumPlanes(sceneData.ViewProjection, planes);

		var cullParams = new CullParams
		{
			Plane0 = planes[0],
			Plane1 = planes[1],
			Plane2 = planes[2],
			Plane3 = planes[3],
			Plane4 = planes[4],
			Plane5 = planes[5],
			CameraPositionAndMaxDrawCount = new Vector4(
				sceneData.CameraOrigin,
				GpuDrawResources.MaxDrawCount)
		};

		commandList.SetComputeConstants(7, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref cullParams, 1)));

		commandList.SetComputeBuffer(0, _gpuDrawResources.DrawCommandBuffer!);
		commandList.SetComputeBuffer(1, _gpuDrawResources.InstanceBuffer!);
		commandList.SetComputeBuffer(2, _gpuDrawResources.MeshBuffer!);
		commandList.SetComputeBuffer(3, _gpuDrawResources.DrawArgsBuffer!);
		commandList.SetComputeBuffer(4, _gpuDrawResources.DrawCountBuffer!);
		commandList.SetComputeBuffer(5, _gpuDrawResources.VisibleDrawIdsBuffer!);
		commandList.SetComputeBuffer(6, _gpuDrawResources.DrawExecutionRangeBuffer!);

		var groupCount = (uint)((GpuDrawResources.MaxDrawCount + 63) / 64);
		commandList.Dispatch(groupCount, 1, 1);
	}

	private IGfxPipeline EnsureUpdatePipeline(IGfxDevice device)
	{
		if (_updatePipeline is not null)
		{
			return _updatePipeline;
		}

		var source = _shaderCompiler.GetMetalComputeSource("gpu_draw_update.compute.slang", "CSUpdate");
		var bytes = Encoding.UTF8.GetBytes(source);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSUpdate", default, default, default);
		_updatePipeline = device.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: bytes));
		return _updatePipeline;
	}

	private IGfxPipeline EnsureCullPipeline(IGfxDevice device)
	{
		if (_cullPipeline is not null)
		{
			return _cullPipeline;
		}

		var source = _shaderCompiler.GetMetalComputeSource("gpu_draw_cull.compute.slang", "CSCull");
		var bytes = Encoding.UTF8.GetBytes(source);
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSCull", default, default, default);
		_cullPipeline = device.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: bytes));
		return _cullPipeline;
	}

	private static void ExtractFrustumPlanes(Matrix4x4 viewProjection, Span<Vector4> planes)
	{
		var col1 = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
		var col2 = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
		var col3 = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
		var col4 = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

		planes[0] = NormalizePlane(col4 + col1);
		planes[1] = NormalizePlane(col4 - col1);
		planes[2] = NormalizePlane(col4 + col2);
		planes[3] = NormalizePlane(col4 - col2);
		planes[4] = NormalizePlane(col3);
		planes[5] = NormalizePlane(col4 - col3);
	}

	private static Vector4 NormalizePlane(Vector4 plane)
	{
		var normal = new Vector3(plane.X, plane.Y, plane.Z);
		var length = normal.Length();
		if (length <= 0.0f)
		{
			return plane;
		}

		var invLength = 1.0f / length;
		return plane * invLength;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CullParams
	{
		public Vector4 Plane0;
		public Vector4 Plane1;
		public Vector4 Plane2;
		public Vector4 Plane3;
		public Vector4 Plane4;
		public Vector4 Plane5;
		public Vector4 CameraPositionAndMaxDrawCount;
	}
}
