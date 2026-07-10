using System;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Importing;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Utility;

namespace WolfEngine.Editor.Projects;

public interface ITextureGpuCompressionService
{
	Texture CompileBcTexture(ImportedTexture importedTexture);
}

internal sealed class TextureGpuCompressionService : ITextureGpuCompressionService
{
	private static readonly ShaderProgramId Bc1Shader = EngineShaderPrograms.Bc1Compress;
	private static readonly ShaderProgramId Bc4Shader = EngineShaderPrograms.Bc4Compress;
	private static readonly ShaderProgramId Bc3StitchShader = EngineShaderPrograms.Bc3Stitch;
	private static readonly ShaderProgramId Bc5StitchShader = EngineShaderPrograms.Bc5Stitch;
	private const string Bc1EntryPoint = "CompressBc1";
	private const string Bc4EntryPoint = "CompressBc4";
	private const string Bc3StitchEntryPoint = "StitchBc3";
	private const string Bc5StitchEntryPoint = "StitchBc5";
	private const uint Bc1RefinementPasses = 2;

	private readonly IRenderer _renderer;
	private readonly IShaderCompiler _shaderCompiler;
	private readonly IMainThreadDispatcher _mainThreadDispatcher;
	private readonly object _sync = new();

	private GraphicsBackendKind? _cachedBackend;
	private long _cachedShaderRevision = -1;
	private IGfxPipeline? _bc1Pipeline;
	private IGfxPipeline? _bc4Pipeline;
	private IGfxPipeline? _bc3StitchPipeline;
	private IGfxPipeline? _bc5StitchPipeline;
	private ComputeThreadGroupSize _bc1ThreadGroupSize;
	private ComputeThreadGroupSize _bc4ThreadGroupSize;
	private ComputeThreadGroupSize _bc3StitchThreadGroupSize;
	private ComputeThreadGroupSize _bc5StitchThreadGroupSize;
	private IGfxBuffer? _bc1Match5Buffer;
	private IGfxBuffer? _bc1Match6Buffer;

	public TextureGpuCompressionService(
		IRenderer renderer,
		IShaderCompiler shaderCompiler,
		IMainThreadDispatcher mainThreadDispatcher)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_mainThreadDispatcher = mainThreadDispatcher ?? throw new ArgumentNullException(nameof(mainThreadDispatcher));
	}

	public Texture CompileBcTexture(ImportedTexture importedTexture)
	{
		ArgumentNullException.ThrowIfNull(importedTexture.MipLevels);
		if (TextureCompressionCompiler.TryGetBcRuntimeFormat(importedTexture.Semantic, out var format) == false)
		{
			throw new InvalidOperationException(
				$"GPU BC compression does not support texture semantic '{importedTexture.Semantic}' for '{importedTexture.NameOrPath}'.");
		}

		var rawMips = TextureMipGenerator.GenerateRgba32MipChain(importedTexture.MipLevels[0]);
		var compressedMips = new TextureMipData[rawMips.Length];
		for (var i = 0; i < rawMips.Length; i++)
		{
			compressedMips[i] = CompressMip(rawMips[i], format);
		}

		return new Texture(
			importedTexture.NameOrPath,
			importedTexture.Width,
			importedTexture.Height,
			importedTexture.IsSrgb,
			format,
			compressedMips);
	}

	private TextureMipData CompressMip(TextureMipData mip, TextureFormat format)
	{
		return _mainThreadDispatcher.Invoke(() => CompressMipOnMainThread(mip, format));
	}

	private TextureMipData CompressMipOnMainThread(TextureMipData mip, TextureFormat format)
	{
		var device = _renderer.GetGfxDevice()
			?? throw new InvalidOperationException(
				"GPU texture compression requires an initialized graphics device. Start the renderer before importing textures.");
		EnsurePipelineState(device);

		var inputBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)mip.Data.Length,
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess));

		try
		{
			if (inputBuffer is not IWritableGpuBuffer writableInput)
			{
				throw new InvalidOperationException("GPU input buffer does not support CPU writes.");
			}

			writableInput.Write<uint>(MemoryMarshal.Cast<byte, uint>(mip.Data.AsSpan()));

			return format switch
			{
				TextureFormat.Bc1Unorm => ExecuteSinglePassCompression(device, mip, inputBuffer, format, 0),
				TextureFormat.Bc4Unorm => ExecuteSinglePassCompression(device, mip, inputBuffer, format, 0),
				TextureFormat.Bc5Unorm => ExecuteBc5Compression(device, mip, inputBuffer),
				TextureFormat.Bc3Unorm => ExecuteBc3Compression(device, mip, inputBuffer),
				_ => throw new InvalidOperationException($"Unsupported GPU BC format '{format}'.")
			};
		}
		finally
		{
			DisposeIfNeeded(inputBuffer);
		}
	}

	private TextureMipData ExecuteSinglePassCompression(IGfxDevice device, TextureMipData mip, IGfxBuffer inputBuffer, TextureFormat format, uint channelIndex)
	{
		var outputSizeInBytes = TextureFormatUtilities.GetMipDataSize(format, mip.Width, mip.Height);
		var outputBuffer = device.CreateBuffer(new BufferDescriptor((ulong)outputSizeInBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var readbackBuffer = device.CreateBuffer(new BufferDescriptor((ulong)outputSizeInBytes, BufferUsage.Staging));

		try
		{
			var commandList = device.BeginCompute();
			try
			{
				DispatchPrimitiveCompression(commandList, inputBuffer, outputBuffer, mip.Width, mip.Height, format, channelIndex);
				commandList.CopyBuffer(outputBuffer, 0, readbackBuffer, 0, (ulong)outputSizeInBytes);
			}
			finally
			{
				device.Submit(commandList);
			}

			return ReadBackMip(device, readbackBuffer, mip.Width, mip.Height, outputSizeInBytes, format);
		}
		finally
		{
			DisposeIfNeeded(outputBuffer);
			DisposeIfNeeded(readbackBuffer);
		}
	}

	private TextureMipData ExecuteBc3Compression(IGfxDevice device, TextureMipData mip, IGfxBuffer inputBuffer)
	{
		var blockWidth = GetBlockCount(mip.Width);
		var blockHeight = GetBlockCount(mip.Height);
		var primitiveSizeInBytes = blockWidth * blockHeight * 8;
		var finalSizeInBytes = TextureFormatUtilities.GetMipDataSize(TextureFormat.Bc3Unorm, mip.Width, mip.Height);

		var colorBuffer = device.CreateBuffer(new BufferDescriptor((ulong)primitiveSizeInBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var alphaBuffer = device.CreateBuffer(new BufferDescriptor((ulong)primitiveSizeInBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var outputBuffer = device.CreateBuffer(new BufferDescriptor((ulong)finalSizeInBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var readbackBuffer = device.CreateBuffer(new BufferDescriptor((ulong)finalSizeInBytes, BufferUsage.Staging));

		try
		{
			var commandList = device.BeginCompute();
			try
			{
				DispatchPrimitiveCompression(commandList, inputBuffer, colorBuffer, mip.Width, mip.Height, TextureFormat.Bc1Unorm, 0);
				DispatchPrimitiveCompression(commandList, inputBuffer, alphaBuffer, mip.Width, mip.Height, TextureFormat.Bc4Unorm, 3);
				DispatchStitch(commandList, _bc3StitchPipeline!, _bc3StitchThreadGroupSize, colorBuffer, alphaBuffer, outputBuffer, blockWidth, blockHeight);
				commandList.CopyBuffer(outputBuffer, 0, readbackBuffer, 0, (ulong)finalSizeInBytes);
			}
			finally
			{
				device.Submit(commandList);
			}

			return ReadBackMip(device, readbackBuffer, mip.Width, mip.Height, finalSizeInBytes, TextureFormat.Bc3Unorm);
		}
		finally
		{
			DisposeIfNeeded(colorBuffer);
			DisposeIfNeeded(alphaBuffer);
			DisposeIfNeeded(outputBuffer);
			DisposeIfNeeded(readbackBuffer);
		}
	}

	private TextureMipData ExecuteBc5Compression(IGfxDevice device, TextureMipData mip, IGfxBuffer inputBuffer)
	{
		var blockWidth = GetBlockCount(mip.Width);
		var blockHeight = GetBlockCount(mip.Height);
		var primitiveSizeInBytes = blockWidth * blockHeight * 8;
		var finalSizeInBytes = TextureFormatUtilities.GetMipDataSize(TextureFormat.Bc5Unorm, mip.Width, mip.Height);

		var redBuffer = device.CreateBuffer(new BufferDescriptor((ulong)primitiveSizeInBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var greenBuffer = device.CreateBuffer(new BufferDescriptor((ulong)primitiveSizeInBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var outputBuffer = device.CreateBuffer(new BufferDescriptor((ulong)finalSizeInBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var readbackBuffer = device.CreateBuffer(new BufferDescriptor((ulong)finalSizeInBytes, BufferUsage.Staging));

		try
		{
			var commandList = device.BeginCompute();
			try
			{
				DispatchPrimitiveCompression(commandList, inputBuffer, redBuffer, mip.Width, mip.Height, TextureFormat.Bc4Unorm, 0);
				DispatchPrimitiveCompression(commandList, inputBuffer, greenBuffer, mip.Width, mip.Height, TextureFormat.Bc4Unorm, 1);
				DispatchStitch(commandList, _bc5StitchPipeline!, _bc5StitchThreadGroupSize, redBuffer, greenBuffer, outputBuffer, blockWidth, blockHeight);
				commandList.CopyBuffer(outputBuffer, 0, readbackBuffer, 0, (ulong)finalSizeInBytes);
			}
			finally
			{
				device.Submit(commandList);
			}

			return ReadBackMip(device, readbackBuffer, mip.Width, mip.Height, finalSizeInBytes, TextureFormat.Bc5Unorm);
		}
		finally
		{
			DisposeIfNeeded(redBuffer);
			DisposeIfNeeded(greenBuffer);
			DisposeIfNeeded(outputBuffer);
			DisposeIfNeeded(readbackBuffer);
		}
	}

	private TextureMipData ReadBackMip(IGfxDevice device, IGfxBuffer readbackBuffer, int width, int height, int outputSizeInBytes, TextureFormat format)
	{
		device.WaitForIdle();
		if (readbackBuffer is not IReadableGpuBuffer readableOutput)
		{
			throw new InvalidOperationException("GPU readback buffer does not support CPU reads.");
		}

		var output = new byte[outputSizeInBytes];
		readableOutput.Read(output);
		return new TextureMipData(width, height, output);
	}

	private void DispatchPrimitiveCompression(
		IGfxCommandList commandList,
		IGfxBuffer inputBuffer,
		IGfxBuffer outputBuffer,
		int width,
		int height,
		TextureFormat format,
		uint channelIndex)
	{
		if (format == TextureFormat.Bc1Unorm)
		{
			var constants = new PrimitiveCompressionConstants((uint)width, (uint)height, (uint)GetBlockCount(width), Bc1RefinementPasses);
			commandList.BindPipeline(_bc1Pipeline!);
			commandList.SetComputeConstants(11, StructAsBytes(ref constants));
			commandList.SetComputeBuffer(0, inputBuffer);
			commandList.SetComputeBuffer(1, outputBuffer);
			commandList.SetComputeBuffer(2, _bc1Match5Buffer!);
			commandList.SetComputeBuffer(3, _bc1Match6Buffer!);
			commandList.Dispatch(
				(uint)DivideRoundUp(GetBlockCount(width), (int)_bc1ThreadGroupSize.X),
				(uint)DivideRoundUp(GetBlockCount(height), (int)_bc1ThreadGroupSize.Y),
				1);
			return;
		}

		if (format == TextureFormat.Bc4Unorm)
		{
			var constants = new PrimitiveCompressionConstants((uint)width, (uint)height, (uint)GetBlockCount(width), channelIndex);
			commandList.BindPipeline(_bc4Pipeline!);
			commandList.SetComputeConstants(11, StructAsBytes(ref constants));
			commandList.SetComputeBuffer(0, inputBuffer);
			commandList.SetComputeBuffer(1, outputBuffer);
			commandList.Dispatch(
				1,
				(uint)DivideRoundUp(GetBlockCount(width), (int)_bc4ThreadGroupSize.Y),
				(uint)DivideRoundUp(GetBlockCount(height), (int)_bc4ThreadGroupSize.Z));
			return;
		}

		throw new InvalidOperationException($"Unsupported primitive compression format '{format}'.");
	}

	private void DispatchStitch(
		IGfxCommandList commandList,
		IGfxPipeline pipeline,
		ComputeThreadGroupSize threadGroupSize,
		IGfxBuffer primaryBuffer,
		IGfxBuffer secondaryBuffer,
		IGfxBuffer outputBuffer,
		int blockWidth,
		int blockHeight)
	{
		var constants = new StitchConstants((uint)blockWidth, (uint)blockHeight, 0u, 0u);
		commandList.BindPipeline(pipeline);
		commandList.SetComputeConstants(11, StructAsBytes(ref constants));
		commandList.SetComputeBuffer(0, primaryBuffer);
		commandList.SetComputeBuffer(1, secondaryBuffer);
		commandList.SetComputeBuffer(2, outputBuffer);
		commandList.Dispatch(
			(uint)DivideRoundUp(blockWidth, (int)threadGroupSize.X),
			(uint)DivideRoundUp(blockHeight, (int)threadGroupSize.Y),
			1);
	}

	private void EnsurePipelineState(IGfxDevice device)
	{
		lock (_sync)
		{
			if (_cachedShaderRevision != _shaderCompiler.Revision)
			{
				_bc1Pipeline = null;
				_bc4Pipeline = null;
				_bc3StitchPipeline = null;
				_bc5StitchPipeline = null;
				_cachedBackend = null;
			}

			if (_cachedBackend == device.BackendKind &&
			    _bc1Pipeline is not null &&
			    _bc4Pipeline is not null &&
			    _bc3StitchPipeline is not null &&
			    _bc5StitchPipeline is not null &&
			    _bc1Match5Buffer is not null &&
			    _bc1Match6Buffer is not null)
			{
				return;
			}

			(_bc1Pipeline, _bc1ThreadGroupSize) = CreatePipeline(device, Bc1Shader, Bc1EntryPoint, "texture-import-bc1");
			(_bc4Pipeline, _bc4ThreadGroupSize) = CreatePipeline(device, Bc4Shader, Bc4EntryPoint, "texture-import-bc4");
			(_bc3StitchPipeline, _bc3StitchThreadGroupSize) = CreatePipeline(device, Bc3StitchShader, Bc3StitchEntryPoint, "texture-import-bc3-stitch");
			(_bc5StitchPipeline, _bc5StitchThreadGroupSize) = CreatePipeline(device, Bc5StitchShader, Bc5StitchEntryPoint, "texture-import-bc5-stitch");

			_bc1Match5Buffer = CreateLookupBuffer(device, Match5TableBytes);
			_bc1Match6Buffer = CreateLookupBuffer(device, Match6TableBytes);
			_cachedBackend = device.BackendKind;
			_cachedShaderRevision = _shaderCompiler.Revision;
		}
	}

	private (IGfxPipeline Pipeline, ComputeThreadGroupSize ThreadGroupSize) CreatePipeline(
		IGfxDevice device,
		ShaderProgramId shaderProgram,
		string entryPoint,
		string variant)
	{
		var compiled = _shaderCompiler.GetComputeShaderWithReflection(shaderProgram, entryPoint, device.BackendKind);
		var pipeline = device.GetOrCreatePipeline(
			new PipelineKey(
				PassKind.Compute,
				null,
				null,
				entryPoint,
				new RenderTargetFormats(Array.Empty<TextureFormat>()),
				new DepthStencilFormat(TextureFormat.Unknown),
				default,
				shaderVariant: variant),
			new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: compiled.ThreadGroupSize));
		return (pipeline, compiled.ThreadGroupSize);
	}

	private static ReadOnlySpan<byte> StructAsBytes<T>(ref T value) where T : unmanaged
	{
		return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
	}

	private static IGfxBuffer CreateLookupBuffer(IGfxDevice device, ReadOnlySpan<byte> matchTableBytes)
	{
		var values = new Vector2[matchTableBytes.Length / 2];
		for (var i = 0; i < values.Length; i++)
		{
			values[i] = new Vector2(matchTableBytes[i * 2], matchTableBytes[i * 2 + 1]);
		}

		var buffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)(values.Length * Marshal.SizeOf<Vector2>()),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess));
		if (buffer is not IWritableGpuBuffer writableBuffer)
		{
			throw new InvalidOperationException("GPU lookup buffer does not support CPU writes.");
		}

		writableBuffer.Write<Vector2>(values);
		return buffer;
	}

	private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

	private static int GetBlockCount(int dimension) => Math.Max(1, (dimension + 3) / 4);

	private static void DisposeIfNeeded(IGfxBuffer? buffer)
	{
		if (buffer is IDisposable disposable)
		{
			disposable.Dispose();
		}
	}

	private readonly record struct PrimitiveCompressionConstants(
		uint Width,
		uint Height,
		uint BlocksX,
		uint Parameter);

	private readonly record struct StitchConstants(
		uint BlocksX,
		uint BlocksY,
		uint Reserved0,
		uint Reserved1);

	private static ReadOnlySpan<byte> Match5TableBytes =>
	[
		0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 1,
		1, 1, 2, 0, 2, 0, 0, 4, 2, 1, 2, 1, 2, 1, 3, 0,
		3, 0, 3, 0, 3, 1, 1, 5, 3, 2, 3, 2, 4, 0, 4, 0,
		4, 1, 4, 1, 4, 2, 4, 2, 4, 2, 3, 5, 5, 1, 5, 1,
		5, 2, 4, 4, 5, 3, 5, 3, 5, 3, 6, 2, 6, 2, 6, 2,
		6, 3, 5, 5, 6, 4, 6, 4, 4, 8, 7, 3, 7, 3, 7, 3,
		7, 4, 7, 4, 7, 4, 7, 5, 5, 9, 7, 6, 7, 6, 8, 4,
		8, 4, 8, 5, 8, 5, 8, 6, 8, 6, 8, 6, 7, 9, 9, 5,
		9, 5, 9, 6, 8, 8, 9, 7, 9, 7, 9, 7, 10, 6, 10, 6,
		10, 6, 10, 7, 9, 9, 10, 8, 10, 8, 8, 12, 11, 7, 11, 7,
		11, 7, 11, 8, 11, 8, 11, 8, 11, 9, 9, 13, 11, 10, 11, 10,
		12, 8, 12, 8, 12, 9, 12, 9, 12, 10, 12, 10, 12, 10, 11, 13,
		13, 9, 13, 9, 13, 10, 12, 12, 13, 11, 13, 11, 13, 11, 14, 10,
		14, 10, 14, 10, 14, 11, 13, 13, 14, 12, 14, 12, 12, 16, 15, 11,
		15, 11, 15, 11, 15, 12, 15, 12, 15, 12, 15, 13, 13, 17, 15, 14,
		15, 14, 16, 12, 16, 12, 16, 13, 16, 13, 16, 14, 16, 14, 16, 14,
		15, 17, 17, 13, 17, 13, 17, 14, 16, 16, 17, 15, 17, 15, 17, 15,
		18, 14, 18, 14, 18, 14, 18, 15, 17, 17, 18, 16, 18, 16, 16, 20,
		19, 15, 19, 15, 19, 15, 19, 16, 19, 16, 19, 16, 19, 17, 17, 21,
		19, 18, 19, 18, 20, 16, 20, 16, 20, 17, 20, 17, 20, 18, 20, 18,
		20, 18, 19, 21, 21, 17, 21, 17, 21, 18, 20, 20, 21, 19, 21, 19,
		21, 19, 22, 18, 22, 18, 22, 18, 22, 19, 21, 21, 22, 20, 22, 20,
		20, 24, 23, 19, 23, 19, 23, 19, 23, 20, 23, 20, 23, 20, 23, 21,
		21, 25, 23, 22, 23, 22, 24, 20, 24, 20, 24, 21, 24, 21, 24, 22,
		24, 22, 24, 22, 23, 25, 25, 21, 25, 21, 25, 22, 24, 24, 25, 23,
		25, 23, 25, 23, 26, 22, 26, 22, 26, 22, 26, 23, 25, 25, 26, 24,
		26, 24, 24, 28, 27, 23, 27, 23, 27, 23, 27, 24, 27, 24, 27, 24,
		27, 25, 25, 29, 27, 26, 27, 26, 28, 24, 28, 24, 28, 25, 28, 25,
		28, 26, 28, 26, 28, 26, 27, 29, 29, 25, 29, 25, 29, 26, 28, 28,
		29, 27, 29, 27, 29, 27, 30, 26, 30, 26, 30, 26, 30, 27, 29, 29,
		30, 28, 30, 28, 30, 28, 31, 27, 31, 27, 31, 27, 31, 28, 31, 28,
		31, 28, 31, 29, 31, 29, 31, 30, 31, 30, 31, 30, 31, 31, 31, 31
	];

	private static ReadOnlySpan<byte> Match6TableBytes =>
	[
		0, 0, 0, 1, 1, 0, 1, 0, 1, 1, 2, 0, 2, 1, 3, 0,
		3, 0, 3, 1, 4, 0, 4, 0, 4, 1, 5, 0, 5, 1, 6, 0,
		6, 0, 6, 1, 7, 0, 7, 0, 7, 1, 8, 0, 8, 1, 8, 1,
		8, 2, 9, 1, 9, 2, 9, 2, 9, 3, 10, 2, 10, 3, 10, 3,
		10, 4, 11, 3, 11, 4, 11, 4, 11, 5, 12, 4, 12, 5, 12, 5,
		12, 6, 13, 5, 13, 6, 8, 16, 13, 7, 14, 6, 14, 7, 9, 17,
		14, 8, 15, 7, 15, 8, 11, 16, 15, 9, 15, 10, 16, 8, 16, 9,
		16, 10, 15, 13, 17, 9, 17, 10, 17, 11, 15, 16, 18, 10, 18, 11,
		18, 12, 16, 16, 19, 11, 19, 12, 19, 13, 17, 17, 20, 12, 20, 13,
		20, 14, 19, 16, 21, 13, 21, 14, 21, 15, 20, 17, 22, 14, 22, 15,
		25, 10, 22, 16, 23, 15, 23, 16, 26, 11, 23, 17, 24, 16, 24, 17,
		27, 12, 24, 18, 25, 17, 25, 18, 28, 13, 25, 19, 26, 18, 26, 19,
		29, 14, 26, 20, 27, 19, 27, 20, 30, 15, 27, 21, 28, 20, 28, 21,
		28, 21, 28, 22, 29, 21, 29, 22, 24, 32, 29, 23, 30, 22, 30, 23,
		25, 33, 30, 24, 31, 23, 31, 24, 27, 32, 31, 25, 31, 26, 32, 24,
		32, 25, 32, 26, 31, 29, 33, 25, 33, 26, 33, 27, 31, 32, 34, 26,
		34, 27, 34, 28, 32, 32, 35, 27, 35, 28, 35, 29, 33, 33, 36, 28,
		36, 29, 36, 30, 35, 32, 37, 29, 37, 30, 37, 31, 36, 33, 38, 30,
		38, 31, 41, 26, 38, 32, 39, 31, 39, 32, 42, 27, 39, 33, 40, 32,
		40, 33, 43, 28, 40, 34, 41, 33, 41, 34, 44, 29, 41, 35, 42, 34,
		42, 35, 45, 30, 42, 36, 43, 35, 43, 36, 46, 31, 43, 37, 44, 36,
		44, 37, 44, 37, 44, 38, 45, 37, 45, 38, 40, 48, 45, 39, 46, 38,
		46, 39, 41, 49, 46, 40, 47, 39, 47, 40, 43, 48, 47, 41, 47, 42,
		48, 40, 48, 41, 48, 42, 47, 45, 49, 41, 49, 42, 49, 43, 47, 48,
		50, 42, 50, 43, 50, 44, 48, 48, 51, 43, 51, 44, 51, 45, 49, 49,
		52, 44, 52, 45, 52, 46, 51, 48, 53, 45, 53, 46, 53, 47, 52, 49,
		54, 46, 54, 47, 57, 42, 54, 48, 55, 47, 55, 48, 58, 43, 55, 49,
		56, 48, 56, 49, 59, 44, 56, 50, 57, 49, 57, 50, 60, 45, 57, 51,
		58, 50, 58, 51, 61, 46, 58, 52, 59, 51, 59, 52, 62, 47, 59, 53,
		60, 52, 60, 53, 60, 53, 60, 54, 61, 53, 61, 54, 61, 54, 61, 55,
		62, 54, 62, 55, 62, 55, 62, 56, 63, 55, 63, 56, 63, 56, 63, 57,
		63, 58, 63, 59, 63, 59, 63, 60, 63, 61, 63, 62, 63, 62, 63, 63
	];
}
