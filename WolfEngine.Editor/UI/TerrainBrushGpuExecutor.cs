using System;
using System.Numerics;
using System.Runtime.Versioning;
using SharpMetal.Metal;
using SharpMetal.Foundation;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using WolfEngine.Backend.D3D12;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.D3D12;
using WolfEngine.Rendering.Backend.Metal;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Utility;

using AbstractionDepthStencilFormat = WolfEngine.Rendering.Abstraction.DepthStencilFormat;

namespace WolfEngine.Editor.UI;

public readonly record struct TerrainGpuStrokePreviewSet(
	Texture CurrentPreviewTexture,
	Texture ScratchPreviewTexture);

public readonly record struct TerrainGpuBrushDispatch(
	TerrainBrushStrokeRequest Request,
	TerrainBrushModifierState Modifiers,
	Texture InputTexture,
	Texture OutputTexture,
	float Strength,
	Vector2 BrushCenterPixels,
	Vector2 BrushRadiusPixels,
	float? FlattenHeightNormalized);

public interface ITerrainBrushGpuExecutor
{
	TerrainGpuStrokePreviewSet CreateStrokeResources(Texture sourceTexture, TerrainAuthoringSurfaceTarget surfaceTarget);
	void ApplyStamp(in TerrainGpuBrushDispatch dispatch);
	byte[] ReadTopMip(Texture texture);
	void RefreshTextureResources(Texture texture);
	void SynchronizePreviewTexture(Texture previewTexture, Texture sourceTexture, TerrainAuthoringSurfaceTarget surfaceTarget);
}

internal sealed unsafe class TerrainBrushGpuExecutor : ITerrainBrushGpuExecutor
{
	private static readonly ShaderProgramId ShaderProgram = EngineShaderPrograms.TerrainAuthoringBrushes;
	private const string RaiseLowerEntryPoint = "ApplyHeightmapRaiseLowerBrush";
	private const string FlattenEntryPoint = "ApplyHeightmapFlattenBrush";
	private const string SmoothEntryPoint = "ApplyHeightmapSmoothBrush";
	private const string PaintLayerEntryPoint = "ApplyLayerMapLayerBrush";

	private readonly IRenderer _renderer;
	private readonly IShaderProvider _shaderCompiler;
	private readonly IMainThreadDispatcher _mainThreadDispatcher;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly object _sync = new();

	private GraphicsBackendKind? _compiledBackendKind;
	private long _compiledShaderRevision = -1;
	private readonly System.Collections.Generic.Dictionary<TerrainBrushOperation, BrushPipelineState> _pipelineStates = new();

	public TerrainBrushGpuExecutor(
		IRenderer renderer,
		IShaderProvider shaderCompiler,
		IMainThreadDispatcher mainThreadDispatcher,
		BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_mainThreadDispatcher = mainThreadDispatcher ?? throw new ArgumentNullException(nameof(mainThreadDispatcher));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public TerrainGpuStrokePreviewSet CreateStrokeResources(Texture sourceTexture, TerrainAuthoringSurfaceTarget surfaceTarget)
	{
		ArgumentNullException.ThrowIfNull(sourceTexture);
		return _mainThreadDispatcher.Invoke(() =>
		{
			var current = CreatePreviewTexture($"{sourceTexture.Name}__terrain_preview_current", sourceTexture, surfaceTarget);
			var scratch = CreatePreviewTexture($"{sourceTexture.Name}__terrain_preview_scratch", sourceTexture, surfaceTarget);
			EnsureGpuTexture(current);
			EnsureGpuTexture(scratch);
			return new TerrainGpuStrokePreviewSet(current, scratch);
		});
	}

	public void ApplyStamp(in TerrainGpuBrushDispatch dispatch)
	{
		var copiedDispatch = dispatch;
		_mainThreadDispatcher.Invoke(() => ApplyStampOnMainThread(copiedDispatch));
	}

	public byte[] ReadTopMip(Texture texture)
	{
		ArgumentNullException.ThrowIfNull(texture);
		return _mainThreadDispatcher.Invoke(() => ReadTopMipOnMainThread(texture));
	}

	public void RefreshTextureResources(Texture texture)
	{
		ArgumentNullException.ThrowIfNull(texture);
		_mainThreadDispatcher.Invoke(() =>
		{
			var resources = _renderer.CreateTextureResources(texture);
			texture.MarkGpuResourcesCreated(resources);
		});
	}

	public void SynchronizePreviewTexture(Texture previewTexture, Texture sourceTexture, TerrainAuthoringSurfaceTarget surfaceTarget)
	{
		ArgumentNullException.ThrowIfNull(previewTexture);
		ArgumentNullException.ThrowIfNull(sourceTexture);
		_mainThreadDispatcher.Invoke(() =>
		{
			var synchronizedPreview = CreatePreviewTexture(previewTexture.Name, sourceTexture, surfaceTarget);
			previewTexture.ApplyTextureData(
				synchronizedPreview.Width,
				synchronizedPreview.Height,
				synchronizedPreview.IsSrgb,
				synchronizedPreview.Format,
				synchronizedPreview.MipLevels);
			var resources = _renderer.CreateTextureResources(previewTexture);
			previewTexture.MarkGpuResourcesCreated(resources);
		});
	}

	private void ApplyStampOnMainThread(in TerrainGpuBrushDispatch dispatch)
	{
		EnsureGpuTexture(dispatch.InputTexture);
		EnsureGpuTexture(dispatch.OutputTexture);

		var device = _renderer.GetGfxDevice()
			?? throw new InvalidOperationException("Terrain authoring requires an initialized graphics device.");
		_bindlessRegistry.EnsureInitialized(device);
		var pipelineState = EnsurePipelineState(device, dispatch.Request.Operation);
		var inputResource = dispatch.InputTexture.Resources?.Texture
			?? throw new InvalidOperationException("Terrain input preview texture is missing GPU resources.");
		var outputResource = dispatch.OutputTexture.Resources?.Texture
			?? throw new InvalidOperationException("Terrain output preview texture is missing GPU resources.");
		var inputHandle = _bindlessRegistry.GetTextureHandle(inputResource);
		var outputHandle = _bindlessRegistry.RegisterRwTexture(outputResource);
		var commandList = device.BeginCompute();

		try
		{
			commandList.Barrier(new ResourceBarrierDescription(outputResource, ResourceState.ShaderResource, ResourceState.UnorderedAccess));
			commandList.BindPipeline(pipelineState.Pipeline);

			pipelineState.BindlessWriter.Clear();
			pipelineState.BindlessWriter.SetUInt("inputHandle", inputHandle.Value);
			pipelineState.BindlessWriter.SetUInt("outputHandle", outputHandle.Value);
			commandList.SetComputeConstants(pipelineState.BindlessWriter.RegisterIndex, pipelineState.BindlessWriter.AsBytes());

			pipelineState.SettingsWriter.Clear();
			pipelineState.SettingsWriter.SetUInt("textureWidth", (uint)dispatch.InputTexture.Width);
			pipelineState.SettingsWriter.SetUInt("textureHeight", (uint)dispatch.InputTexture.Height);
			pipelineState.SettingsWriter.SetUInt("layerIndex", (uint)Math.Clamp(dispatch.Request.Settings.LayerIndex, 0, 255));
			pipelineState.SettingsWriter.SetFloat("brushStrength", Math.Clamp(dispatch.Strength, 0.0f, 1.0f));
			pipelineState.SettingsWriter.SetFloat("brushFalloff", MathF.Max(dispatch.Request.Settings.Falloff, 0.1f));
			pipelineState.SettingsWriter.SetFloat("brushInvertSign", dispatch.Modifiers.Invert ? -1.0f : 1.0f);
			pipelineState.SettingsWriter.SetFloat("flattenHeightNormalized", Math.Clamp(dispatch.FlattenHeightNormalized ?? 0.0f, 0.0f, 1.0f));
			pipelineState.SettingsWriter.SetVector2("brushCenterPixels", dispatch.BrushCenterPixels);
			pipelineState.SettingsWriter.SetVector2("brushRadiusPixels", dispatch.BrushRadiusPixels);
			commandList.SetComputeConstants(pipelineState.SettingsWriter.RegisterIndex, pipelineState.SettingsWriter.AsBytes());

			var (dispatchX, dispatchY, dispatchZ) = pipelineState.ThreadGroupSize.GetDispatchGroupCount(
				(uint)Math.Max(dispatch.InputTexture.Width, 1),
				(uint)Math.Max(dispatch.InputTexture.Height, 1));
			commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
			commandList.Barrier(new ResourceBarrierDescription(outputResource, ResourceState.UnorderedAccess, ResourceState.ShaderResource));
		}
		finally
		{
			device.Submit(commandList);
			device.WaitForIdle();
		}
	}

	private byte[] ReadTopMipOnMainThread(Texture texture)
	{
		EnsureGpuTexture(texture);

		return _renderer.GetGfxDevice().BackendKind switch
		{
			GraphicsBackendKind.D3D12 => ReadTopMipD3D12(texture),
			GraphicsBackendKind.Metal when OperatingSystem.IsMacOS() => ReadTopMipMetal(texture),
			_ => throw new NotSupportedException("Terrain GPU authoring is not supported on this graphics backend.")
		};
	}

	private byte[] ReadTopMipD3D12(Texture texture)
	{
		if (texture.Resources?.Texture is not ID3D12BackendTexture d3dTexture)
		{
			throw new InvalidOperationException("Terrain preview texture was not created by the Direct3D12 backend.");
		}

		if (_renderer.GetGfxDevice() is not D3D12Device device)
		{
			throw new InvalidOperationException("Terrain authoring expected a Direct3D12 graphics device.");
		}

		if (texture.Format != TextureFormat.Rgba8Unorm &&
		    texture.Format != TextureFormat.Bgra8Unorm &&
		    texture.Format != TextureFormat.Rgba16Float)
		{
			throw new InvalidOperationException($"Terrain readback only supports RGBA8 or RGBA16F preview formats, but got '{texture.Format}'.");
		}

		var bytesPerPixel = texture.Format == TextureFormat.Rgba16Float ? 8U : 4U;
		var rowPitch = AlignTo((uint)texture.Width * bytesPerPixel, D3D12.TextureDataPitchAlignment);
		var readbackSize = (ulong)rowPitch * (ulong)texture.Height;
		var readbackBuffer = device.CreateBuffer(new BufferDescriptor(readbackSize, BufferUsage.Staging));
		if (readbackBuffer is not D3D12Buffer d3dReadbackBuffer)
		{
			throw new InvalidOperationException("Terrain authoring expected a Direct3D12 readback buffer.");
		}

		var commandList = device.BeginGraphics();
		try
		{
			commandList.Barrier(new ResourceBarrierDescription(d3dTexture, ResourceState.ShaderResource, ResourceState.CopySource));
			if (commandList is not D3D12CommandList d3dCommandList)
			{
				throw new InvalidOperationException("Terrain authoring expected a Direct3D12 command list.");
			}

			var destination = new TextureCopyLocation
			{
				PResource = d3dReadbackBuffer.Resource.Handle,
				Type = TextureCopyType.PlacedFootprint
			};
			destination.Anonymous.PlacedFootprint = new PlacedSubresourceFootprint
			{
				Offset = 0,
				Footprint = new SubresourceFootprint
				{
					Format = texture.Format switch
					{
						TextureFormat.Bgra8Unorm => Silk.NET.DXGI.Format.FormatB8G8R8A8Unorm,
						TextureFormat.Rgba16Float => Silk.NET.DXGI.Format.FormatR16G16B16A16Float,
						_ => Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm
					},
					Width = (uint)texture.Width,
					Height = (uint)texture.Height,
					Depth = 1,
					RowPitch = rowPitch
				}
			};

			var source = new TextureCopyLocation
			{
				PResource = d3dTexture.Resource,
				Type = TextureCopyType.SubresourceIndex
			};
			source.Anonymous.SubresourceIndex = 0;
			d3dCommandList.NativeCommandList->CopyTextureRegion(&destination, 0, 0, 0, &source, (Box*)null);
		}
		finally
		{
			device.Submit(commandList);
			device.WaitForIdle();
		}

		if (readbackBuffer is not IReadableGpuBuffer readableBuffer)
		{
			throw new InvalidOperationException("Terrain authoring readback buffer does not support CPU reads.");
		}

		var raw = new byte[readbackSize];
		readableBuffer.Read(raw);

		var packedBytesPerPixel = (int)bytesPerPixel;
		var packed = new byte[texture.Width * texture.Height * packedBytesPerPixel];
		var sourceOffset = 0;
		for (var y = 0; y < texture.Height; y++)
		{
			Buffer.BlockCopy(raw, sourceOffset, packed, y * texture.Width * packedBytesPerPixel, texture.Width * packedBytesPerPixel);
			sourceOffset += (int)rowPitch;
		}

		return packed;
	}

	[SupportedOSPlatform("macos")]
	private static unsafe byte[] ReadTopMipMetal(Texture texture)
	{
		if (texture.Resources?.Texture is not MetalTexture metalTexture)
		{
			throw new InvalidOperationException("Terrain preview texture was not created by the Metal backend.");
		}

		if (texture.Format != TextureFormat.Rgba8Unorm &&
		    texture.Format != TextureFormat.Bgra8Unorm &&
		    texture.Format != TextureFormat.Rgba16Float)
		{
			throw new InvalidOperationException($"Terrain readback only supports RGBA8 or RGBA16F preview formats, but got '{texture.Format}'.");
		}

		var bytesPerRow = texture.Width * TextureFormatUtilities.GetBytesPerBlock(texture.Format);
		var data = new byte[bytesPerRow * texture.Height];
		var region = new MTLRegion
		{
			origin = new MTLOrigin { x = 0, y = 0, z = 0 },
			size = new MTLSize { width = (nuint)texture.Width, height = (nuint)texture.Height, depth = 1 }
		};

		fixed (byte* destination = data)
		{
			metalTexture.Texture.GetBytes((nint)destination, (nuint)bytesPerRow, region, 0);
		}

		return data;
	}

	private BrushPipelineState EnsurePipelineState(IGfxDevice device, TerrainBrushOperation operation)
	{
		lock (_sync)
		{
			if (_compiledShaderRevision != _shaderCompiler.Revision ||
			    _compiledBackendKind.HasValue &&
			    _compiledBackendKind.Value != device.BackendKind)
			{
				_pipelineStates.Clear();
			}

			if (_pipelineStates.TryGetValue(operation, out var cached))
			{
				_compiledBackendKind = device.BackendKind;
				return cached;
			}

			var entryPoint = GetEntryPoint(operation);
			var compiled = _shaderCompiler.GetComputeShaderWithReflection(ShaderProgram, entryPoint, device.BackendKind);
			var reflection = compiled.ReflectionLayout;
			var pipeline = device.GetOrCreatePipeline(
				new PipelineKey(
					PassKind.Compute,
					vertexEntryPoint: null,
					pixelEntryPoint: null,
					computeEntryPoint: entryPoint,
					renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
					depthStencil: new AbstractionDepthStencilFormat(TextureFormat.Unknown),
					renderState: default,
					shaderVariant: ShaderProgram.Value),
				new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: compiled.ThreadGroupSize));
			var state = new BrushPipelineState(
				pipeline,
				compiled.ThreadGroupSize,
				new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles")),
				new ShaderPropertyWriter(reflection.GetConstantBuffer("TerrainBrushParameters")));
			_pipelineStates[operation] = state;
			_compiledBackendKind = device.BackendKind;
			_compiledShaderRevision = _shaderCompiler.Revision;
			return state;
		}
	}

	private void EnsureGpuTexture(Texture texture)
	{
		if (texture.HasGpuResources)
		{
			return;
		}

		var resources = _renderer.CreateTextureResources(texture);
		texture.MarkGpuResourcesCreated(resources);
	}

	private static Texture CreatePreviewTexture(string name, Texture source, TerrainAuthoringSurfaceTarget surfaceTarget)
	{
		if (surfaceTarget == TerrainAuthoringSurfaceTarget.Heightmap)
		{
			return CreateHeightPreviewTexture(name, source);
		}

		return CloneTexture(name, source);
	}

	private static Texture CloneTexture(string name, Texture source)
	{
		var mipLevels = new TextureMipData[source.MipLevels.Length];
		for (var i = 0; i < source.MipLevels.Length; i++)
		{
			var mip = source.MipLevels[i];
			mipLevels[i] = new TextureMipData(mip.Width, mip.Height, mip.Data.ToArray());
		}

		return new Texture(name, source.Width, source.Height, source.IsSrgb, source.Format, mipLevels);
	}

	private static Texture CreateHeightPreviewTexture(string name, Texture source)
	{
		if (source.Format == TextureFormat.Rgba16Float)
		{
			return CloneTexture(name, source);
		}

		if (source.Format != TextureFormat.Rgba8Unorm &&
		    source.Format != TextureFormat.Bgra8Unorm &&
		    source.Format != TextureFormat.R16Unorm)
		{
			throw new InvalidOperationException($"Terrain height painting expects an R16 or RGBA8 source heightmap, but got '{source.Format}'.");
		}

		var sourceTopMip = source.MipLevels[0];
		var previewData = ConvertHeightTopMipToRgba16Float(sourceTopMip, source.Format);

		return new Texture(
			name,
			source.Width,
			source.Height,
			false,
			TextureFormat.Rgba16Float,
			[new TextureMipData(sourceTopMip.Width, sourceTopMip.Height, previewData)]);
	}

	private static byte[] ConvertHeightTopMipToRgba16Float(TextureMipData sourceTopMip, TextureFormat sourceFormat)
	{
		var previewData = new byte[sourceTopMip.Width * sourceTopMip.Height * 8];
		for (var pixelIndex = 0; pixelIndex < sourceTopMip.Width * sourceTopMip.Height; pixelIndex++)
		{
			var normalizedHeight = sourceFormat == TextureFormat.R16Unorm
				? ReadUInt16(sourceTopMip.Data, pixelIndex * 2) / 65535.0f
				: (sourceFormat == TextureFormat.Bgra8Unorm
					? sourceTopMip.Data[pixelIndex * 4 + 2]
					: sourceTopMip.Data[pixelIndex * 4]) / 255.0f;
			var halfHeight = (ushort)BitConverter.HalfToUInt16Bits((Half)normalizedHeight);
			var halfAlpha = (ushort)BitConverter.HalfToUInt16Bits((Half)1.0f);
			var destinationOffset = pixelIndex * 8;
			WriteUInt16(previewData, destinationOffset + 0, halfHeight);
			WriteUInt16(previewData, destinationOffset + 2, halfHeight);
			WriteUInt16(previewData, destinationOffset + 4, halfHeight);
			WriteUInt16(previewData, destinationOffset + 6, halfAlpha);
		}

		return previewData;
	}

	private static string GetEntryPoint(TerrainBrushOperation operation)
	{
		return operation switch
		{
			TerrainBrushOperation.RaiseLower => RaiseLowerEntryPoint,
			TerrainBrushOperation.Flatten => FlattenEntryPoint,
			TerrainBrushOperation.Smooth => SmoothEntryPoint,
			TerrainBrushOperation.PaintLayer => PaintLayerEntryPoint,
			_ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported terrain brush operation.")
		};
	}

	private static uint AlignTo(uint value, uint alignment)
	{
		return ((value + alignment - 1) / alignment) * alignment;
	}

	private static byte[] ConvertRgba16FloatToRgba8(byte[] source, int width, int height)
	{
		var result = new byte[width * height * 4];
		for (var pixelIndex = 0; pixelIndex < width * height; pixelIndex++)
		{
			var sourceOffset = pixelIndex * 8;
			var normalizedHeight = (float)BitConverter.UInt16BitsToHalf(ReadUInt16(source, sourceOffset));
			var encodedHeight = EncodeNormalizedToByte(normalizedHeight);
			var destinationOffset = pixelIndex * 4;
			result[destinationOffset + 0] = encodedHeight;
			result[destinationOffset + 1] = encodedHeight;
			result[destinationOffset + 2] = encodedHeight;
			result[destinationOffset + 3] = 255;
		}

		return result;
	}

	private static ushort ReadUInt16(byte[] data, int offset)
	{
		return (ushort)(data[offset] | (data[offset + 1] << 8));
	}

	private static void WriteUInt16(byte[] data, int offset, ushort value)
	{
		data[offset] = (byte)(value & 0xFF);
		data[offset + 1] = (byte)(value >> 8);
	}

	private static byte EncodeNormalizedToByte(float value)
	{
		return (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);
	}

	private sealed record BrushPipelineState(
		IGfxPipeline Pipeline,
		ComputeThreadGroupSize ThreadGroupSize,
		ShaderPropertyWriter BindlessWriter,
		ShaderPropertyWriter SettingsWriter);
}
