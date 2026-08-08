using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using ImGuiNET;
using SharpMetal.Metal;
using WolfEngine.Platform;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.UI;

[SupportedOSPlatform("MacOS")]
internal sealed unsafe class MetalImGuiRenderer : IImGuiRenderer
{
	private sealed class UiBufferSet
	{
		public required MetalBuffer VertexBuffer { get; init; }
		public required MetalBuffer IndexBuffer { get; init; }
		public required int VertexBufferSize { get; init; }
		public required int IndexBufferSize { get; init; }
	}

	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxDevice? _device;
	private IGfxPipeline? _pipeline;
	private MetalTexture? _fontTexture;
	private DescriptorHandle _fontHandle = DescriptorHandle.Invalid;
	private DescriptorHandle _samplerHandle = DescriptorHandle.Invalid;
	private ShaderPropertyWriter? _projectionWriter;
	private ShaderPropertyWriter? _bindlessWriter;
	private readonly List<UiBufferSet> _availableBuffers = new();
	private UiBufferSet? _recordingBuffers;
	private bool _fontUploaded;
	private readonly string _fragmentEntryPoint;
	private readonly bool _sampleTexture;

	public MetalImGuiRenderer(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry, bool sampleTexture = true)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_fragmentEntryPoint = "fragmentShader";
		_sampleTexture = sampleTexture;
	}

	public void EnsureResources(IGfxDevice device, UiFrameData frame)
	{
		_device ??= device;
		if (_pipeline is null)
		{
			_pipeline = CreatePipeline(device);
		}

		// A UI frame can be consumed before the renderer is ready to create its
		// resources. Keep accepting atlas-bearing frames until we have a compatible
		// texture rather than treating consumption as a successful upload.
		if (frame.HasFontAtlas && (_fontTexture is null ||
		                          _fontTexture.Descriptor.Width != (uint)frame.FontAtlas.Width ||
		                          _fontTexture.Descriptor.Height != (uint)frame.FontAtlas.Height))
		{
			CreateFontTexture(device, frame.FontAtlas);
			_fontUploaded = true;
		}

		if (frame.HasFontAtlas && _fontUploaded)
		{
			frame.MarkFontAtlasUploaded();
		}
	}

	public void InvalidateShaderPipeline()
	{
		_pipeline = null;
	}

	public void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture finalColorTarget, bool clearTarget,
		ColorRGBA? clearColor = null)
	{
		var commandList = context.CommandList as MetalCommandList;
		if (commandList is null)
		{
			return;
		}

		var finalColor = finalColorTarget as MetalTexture;
		if (finalColor is null)
		{
			return;
		}

		var destination = finalColor.Texture;
		if (destination.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var targets = new PassTargets(new[] { new ColorTargetBinding(finalColor) });
		var viewport = new Viewport(0, 0, finalColor.Descriptor.Width, finalColor.Descriptor.Height);
		commandList.BeginPass(targets, viewport);
		if (clearTarget)
		{
			commandList.ClearColorAttachment(0, clearColor ?? new ColorRGBA(0.05f, 0.05f, 0.05f, 1.0f));
		}

		if (frame.CommandCount == 0 || _pipeline is null || _fontTexture is null)
		{
			commandList.EndPass();
			return;
		}

		EnsureBuffers(frame);

		commandList.BindPipeline(_pipeline);
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

		var buffers = _recordingBuffers ?? throw new InvalidOperationException("ImGui buffers were not prepared.");
		var vertexView = new VertexBufferView(buffers.VertexBuffer, (uint)Unsafe.SizeOf<ImDrawVert>());
		commandList.SetVertexBuffers(new[] { vertexView, vertexView, vertexView });
		commandList.SetIndexBuffer(new IndexBufferView(buffers.IndexBuffer, IndexFormat.UInt16, 0));

		var L = frame.DisplayPos.X;
		var R = frame.DisplayPos.X + frame.DisplaySize.X;
		var T = frame.DisplayPos.Y;
		var B = frame.DisplayPos.Y + frame.DisplaySize.Y;
		var projectionWriter = _projectionWriter
			?? throw new InvalidOperationException("Metal ImGui projection writer was not initialized.");
		projectionWriter.Clear();
		projectionWriter.SetMatrix4x4(
			"ProjectionMatrix",
			new Matrix4x4(
				2.0f / (R - L), 0.0f, 0.0f, 0.0f,
				0.0f, 2.0f / (T - B), 0.0f, 0.0f,
				0.0f, 0.0f, 0.5f, 0.0f,
				(R + L) / (L - R), (T + B) / (B - T), 0.5f, 1.0f));
		commandList.SetGraphicsConstants(projectionWriter.RegisterIndex, projectionWriter.AsBytes());

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("Metal ImGui bindless writer was not initialized.");
		uint activeTextureHandle = 0;
		var hasActiveTextureHandle = false;

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
			var textureHandle = ResolveTextureHandle(cmd.TextureId);
			if (hasActiveTextureHandle == false || textureHandle != activeTextureHandle)
			{
				bindlessWriter.Clear();
				bindlessWriter.SetUInt("textureHandle", textureHandle);
				bindlessWriter.SetUInt("samplerHandle", _samplerHandle.Value);
				bindlessWriter.SetUInt("sampleTexture", _sampleTexture ? 1u : 0u);
				commandList.SetGraphicsConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());
				activeTextureHandle = textureHandle;
				hasActiveTextureHandle = true;
			}

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
		FinalizeFrameBuffers();
	}

	private uint ResolveTextureHandle(nint textureId)
	{
		if (textureId == UiTextureIds.FontAtlas)
		{
			return _fontHandle.Value;
		}

		if (textureId == UiTextureIds.SceneViewport)
		{
			var errorHandle = _bindlessRegistry.ErrorTextureHandle;
			return errorHandle.IsValid ? errorHandle.Value : _fontHandle.Value;
		}

		if (textureId == 0)
		{
			var errorHandle = _bindlessRegistry.ErrorTextureHandle;
			if (errorHandle.IsValid)
			{
				return errorHandle.Value;
			}

			return _fontHandle.Value;
		}

		return unchecked((uint)textureId);
	}

	private void EnsureBuffers(UiFrameData frame)
	{
		if (_device is null)
		{
			throw new InvalidOperationException("ImGui renderer has no device.");
		}

		var vertexBytes = frame.VertexCount * Unsafe.SizeOf<ImDrawVert>();
		var indexBytes = frame.IndexCount * sizeof(ushort);
		_recordingBuffers = AcquireBufferSet(_device, vertexBytes, indexBytes);

		if (frame.VertexCount > 0)
		{
			BufferHelper.CopyToBuffer<ImDrawVert>(
				frame.Vertices.AsSpan(0, frame.VertexCount),
				_recordingBuffers.VertexBuffer.Buffer);
		}

		if (frame.IndexCount > 0)
		{
			BufferHelper.CopyToBuffer<ushort>(
				frame.Indices.AsSpan(0, frame.IndexCount),
				_recordingBuffers.IndexBuffer.Buffer);
		}
	}

	private UiBufferSet AcquireBufferSet(IGfxDevice device, int vertexBytes, int indexBytes)
	{
		var requiredVertexBytes = (int)Math.Max(vertexBytes, 65536);
		var requiredIndexBytes = (int)Math.Max(indexBytes, 65536);
		for (var i = 0; i < _availableBuffers.Count; i++)
		{
			var candidate = _availableBuffers[i];
			if (candidate.VertexBufferSize < requiredVertexBytes || candidate.IndexBufferSize < requiredIndexBytes)
			{
				continue;
			}

			_availableBuffers.RemoveAt(i);
			return candidate;
		}

		return new UiBufferSet
		{
			VertexBuffer = CreateBuffer(device, requiredVertexBytes, BufferUsage.Vertex),
			IndexBuffer = CreateBuffer(device, requiredIndexBytes, BufferUsage.Index),
			VertexBufferSize = requiredVertexBytes,
			IndexBufferSize = requiredIndexBytes
		};
	}

	private void FinalizeFrameBuffers()
	{
		if (_recordingBuffers is null)
		{
			return;
		}

		var used = _recordingBuffers;
		_recordingBuffers = null;
		if (_device is not null)
		{
			_device.Retire(
				() => _availableBuffers.Add(used),
				"Metal ImGui buffer-set recycle");
			return;
		}

		_availableBuffers.Add(used);
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
		var compiled = _shaderCompiler.GetGraphicsShaderWithReflection(
			EngineShaderPrograms.ImGui,
			"vertexShader",
			_fragmentEntryPoint,
			GraphicsBackendKind.Metal);
		_projectionWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("Projection"));
		_bindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("ImGuiBindless"));
		var shaderBytes = compiled.Bytecode.Vertex
			?? throw new InvalidOperationException("ImGui Metal vertex bytecode was missing from reflection compile result.");
		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.None,
			depthTestEnabled: false,
			depthWriteEnabled: false,
			BlendMode.AlphaBlend);

		var pipelineKey = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: _fragmentEntryPoint,
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
		if (_fontTexture is not null)
		{
			device.Retire(_fontTexture, "Metal ImGui font texture");
		}
		_fontTexture = texture;
		_bindlessRegistry.EnsureInitialized(device);
		_fontHandle = _bindlessRegistry.RegisterTexture(texture);

		if (_samplerHandle.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
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
