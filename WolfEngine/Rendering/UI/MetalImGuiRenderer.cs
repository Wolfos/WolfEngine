using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ImGuiNET;
using SharpMetal.Metal;
using WolfEngine.Platform;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;

namespace WolfEngine.Rendering.UI;

[SupportedOSPlatform("MacOS")]
internal sealed unsafe class MetalImGuiRenderer : IImGuiRenderer
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxDevice _device;
	private IGfxPipeline _pipeline;
	private MetalBuffer _vertexBuffer;
	private MetalBuffer _indexBuffer;
	private int _vertexBufferSize;
	private int _indexBufferSize;
	private MetalTexture _fontTexture;
	private DescriptorHandle _fontHandle = DescriptorHandle.Invalid;
	private DescriptorHandle _samplerHandle = DescriptorHandle.Invalid;
	private bool _fontUploaded;

	public MetalImGuiRenderer(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public void EnsureResources(IGfxDevice device, UiFrameData frame)
	{
		_device ??= device;
		if (_pipeline is null)
		{
			_pipeline = CreatePipeline(device);
		}

		if (frame.HasFontAtlas)
		{
			CreateFontTexture(device, frame.FontAtlas);
			_fontUploaded = true;
		}
	}

	public void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture finalColorTarget, IGfxTexture lightingSource)
	{
		var commandList = context.CommandList as MetalCommandList;
		if (commandList is null)
		{
			return;
		}

		var finalColor = finalColorTarget as MetalTexture;
		var lighting = lightingSource as MetalTexture;
		if (finalColor is null || lighting is null)
		{
			return;
		}

		var source = lighting.Texture;
		var destination = finalColor.Texture;
		if (source.NativePtr == IntPtr.Zero || destination.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var width = Math.Min(source.Width, destination.Width);
		var height = Math.Min(source.Height, destination.Height);
		commandList.CopyTexture(source, destination, (uint)width, (uint)height);

		if (frame.CommandCount == 0)
		{
			return;
		}

		if (_pipeline is null || _fontTexture is null)
		{
			return;
		}

		var targets = new PassTargets(new[] { new ColorTargetBinding(finalColor) });
		var viewport = new Viewport(0, 0, finalColor.Descriptor.Width, finalColor.Descriptor.Height);
		commandList.BeginPass(targets, viewport);

		EnsureBuffers(frame);

		commandList.BindPipeline(_pipeline);
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

		var vertexView = new VertexBufferView(_vertexBuffer!, (uint)Unsafe.SizeOf<ImDrawVert>());
		commandList.SetVertexBuffers(new[] { vertexView, vertexView, vertexView });
		commandList.SetIndexBuffer(new IndexBufferView(_indexBuffer!, IndexFormat.UInt16, 0));

		Span<float> projection = stackalloc float[16];
		var L = frame.DisplayPos.X;
		var R = frame.DisplayPos.X + frame.DisplaySize.X;
		var T = frame.DisplayPos.Y;
		var B = frame.DisplayPos.Y + frame.DisplaySize.Y;
		projection[0] = 2.0f / (R - L);
		projection[1] = 0.0f;
		projection[2] = 0.0f;
		projection[3] = 0.0f;

		projection[4] = 0.0f;
		projection[5] = 2.0f / (T - B);
		projection[6] = 0.0f;
		projection[7] = 0.0f;

		projection[8] = 0.0f;
		projection[9] = 0.0f;
		projection[10] = 0.5f;
		projection[11] = 0.0f;

		projection[12] = (R + L) / (L - R);
		projection[13] = (T + B) / (B - T);
		projection[14] = 0.5f;
		projection[15] = 1.0f;
		commandList.SetGraphicsConstants(0, MemoryMarshal.AsBytes(projection));

		Span<uint> bindless = stackalloc uint[4];
		bindless[0] = _fontHandle.Value;
		bindless[1] = _samplerHandle.Value;
		bindless[2] = 0;
		bindless[3] = 0;
		commandList.SetGraphicsConstants(1, MemoryMarshal.AsBytes(bindless));

		var scaleX = 1.0f;
		var scaleY = 1.0f;
		if (frame.DisplaySize.X > 0.0f && frame.DisplaySize.Y > 0.0f)
		{
			scaleX = frame.FramebufferSize.X / frame.DisplaySize.X;
			scaleY = frame.FramebufferSize.Y / frame.DisplaySize.Y;
		}

		for (var i = 0; i < frame.CommandCount; i++)
		{
			var cmd = frame.Commands[i];
			var clip = cmd.ClipRect;
			var clipX1 = (int)Math.Floor((clip.X - frame.DisplayPos.X) * scaleX);
			var clipY1 = (int)Math.Floor((clip.Y - frame.DisplayPos.Y) * scaleY);
			var clipX2 = (int)Math.Ceiling((clip.Z - frame.DisplayPos.X) * scaleX);
			var clipY2 = (int)Math.Ceiling((clip.W - frame.DisplayPos.Y) * scaleY);

			if (clipX1 < 0) clipX1 = 0;
			if (clipY1 < 0) clipY1 = 0;
			if (clipX2 > frame.FramebufferSize.X) clipX2 = (int)frame.FramebufferSize.X;
			if (clipY2 > frame.FramebufferSize.Y) clipY2 = (int)frame.FramebufferSize.Y;
			if (clipX2 <= clipX1 || clipY2 <= clipY1)
			{
				continue;
			}

			commandList.SetScissorRect(new RectInt(clipX1, clipY1, clipX2 - clipX1, clipY2 - clipY1));
			commandList.Draw(new DrawArguments(
				(uint)cmd.ElemCount,
				1,
				(uint)cmd.IdxOffset,
				cmd.VtxOffset,
				0));
		}

		commandList.EndPass();
	}

	private void EnsureBuffers(UiFrameData frame)
	{
		if (_device is null)
		{
			throw new InvalidOperationException("ImGui renderer has no device.");
		}

		var vertexBytes = frame.VertexCount * Unsafe.SizeOf<ImDrawVert>();
		var indexBytes = frame.IndexCount * sizeof(ushort);

		if (_vertexBuffer is null || _vertexBufferSize < vertexBytes)
		{
			_vertexBuffer?.Dispose();
			_vertexBufferSize = (int)Math.Max(vertexBytes, 65536);
			_vertexBuffer = CreateBuffer(_device, _vertexBufferSize, BufferUsage.Vertex);
		}

		if (_indexBuffer is null || _indexBufferSize < indexBytes)
		{
			_indexBuffer?.Dispose();
			_indexBufferSize = (int)Math.Max(indexBytes, 65536);
			_indexBuffer = CreateBuffer(_device, _indexBufferSize, BufferUsage.Index);
		}

		if (frame.VertexCount > 0)
		{
			BufferHelper.CopyToBuffer<ImDrawVert>(frame.Vertices.AsSpan(0, frame.VertexCount), _vertexBuffer!.Buffer);
		}

		if (frame.IndexCount > 0)
		{
			BufferHelper.CopyToBuffer<ushort>(frame.Indices.AsSpan(0, frame.IndexCount), _indexBuffer!.Buffer);
		}
	}

	private static MetalBuffer CreateBuffer(IGfxDevice device, int size, BufferUsage usage)
	{
		var buffer = device.CreateBuffer(new BufferDescriptor((ulong)size, usage)) as MetalBuffer;
		if (buffer is null || buffer.Buffer.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to allocate Metal ImGui buffer.");
		}

		return buffer;
	}

	private IGfxPipeline CreatePipeline(IGfxDevice device)
	{
		var source = _shaderCompiler.GetMetalSource("imgui.slang");
		var shaderBytes = Encoding.UTF8.GetBytes(source);
		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.None,
			depthTestEnabled: false,
			depthWriteEnabled: false,
			BlendMode.AlphaBlend);

		var pipelineKey = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: "fragmentShader",
			computeEntryPoint: null,
			renderTargets: new(new[] { TextureFormat.Bgra8Unorm }),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: renderState,
			layout: GraphicsLayoutKind.ImGui);

		return device.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(shaderBytes, shaderBytes));
	}

	private void CreateFontTexture(IGfxDevice device, ImGuiFontAtlas atlas)
	{
		var descriptor = new TextureDescriptor(
			atlas.Width,
			atlas.Height,
			TextureFormat.Rgba8Unorm,
			TextureUsage.ShaderResource);

		var texture = device.CreateTexture(descriptor) as MetalTexture;
		if (texture is null)
		{
			throw new InvalidOperationException("ImGui font texture was not created by Metal backend.");
		}

		UploadTextureData(texture.Texture, atlas);
		_fontTexture?.Dispose();
		_fontTexture = texture;
		_fontHandle = texture.ShaderResourceView;

		if (_samplerHandle.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_bindlessRegistry.EnsureInitialized(device);
			_samplerHandle = _bindlessRegistry.GetSamplerHandle(sampler);
		}
	}

	private static void UploadTextureData(MTLTexture texture, ImGuiFontAtlas atlas)
	{
		if (atlas.PixelsRgba.Length == 0)
		{
			return;
		}

		var origin = new MTLOrigin { x = 0, y = 0, z = 0 };
		var size = new MTLSize { width = (ulong)atlas.Width, height = (ulong)atlas.Height, depth = 1 };
		var region = new MTLRegion { origin = origin, size = size };
		var bytesPerRow = (ulong)(atlas.Width * 4);

		fixed (byte* ptr = atlas.PixelsRgba)
		{
			texture.ReplaceRegion(region, 0, (IntPtr)ptr, bytesPerRow);
		}
	}
}
