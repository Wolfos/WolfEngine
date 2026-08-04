#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Platform;

namespace WolfEngine.Rendering.Backend.Metal;

/// <summary>
/// Owns the compute kernel that compacts an indirect command buffer page down to the draws culling
/// left visible.
///
/// The kernel is hand-written MSL rather than a Slang program like the rest of the engine's shaders:
/// a Metal indirect command buffer is an opaque object, not a buffer of command records, so moving a
/// surviving command means calling the GPU-side <c>copy_command</c> intrinsic, which the shared shader
/// language cannot express. It is compiled on demand so that a device or OS without the intrinsic
/// reports itself unavailable and leaves the shared draw passes on their full-range fallback.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalIndirectCompactionKernel : IDisposable
{
	public const uint ThreadsPerThreadgroup = 64;

	public const uint CommandBuffersBufferIndex = 0;
	public const uint ExecutionRangeBufferIndex = 1;
	public const uint DrawArgsBufferIndex = 2;
	public const uint DrawCommandsBufferIndex = 3;
	public const uint ParamsBufferIndex = 4;

	private const string EntryPoint = "CSCompactIndirectCommands";
	private const string SourceResourceName = "WolfEngine.Shaders.GpuDraw.Metal.gpu_draw_compact_icb.metal";
	private const ulong SourceCommandBufferArgumentIndex = 0;
	private const ulong DestinationCommandBufferArgumentIndex = 1;

	private readonly MTLDevice _device;
	private readonly object _compileSync = new();
	private MTLLibrary _library;
	private MTLFunction _function;
	private MTLArgumentEncoder _argumentEncoder;
	private MTLComputePipelineState _pipelineState;
	private bool _compileAttempted;
	private string? _unavailableReason;
	private bool _disposed;

	public MetalIndirectCompactionKernel(MTLDevice device)
	{
		_device = device;
	}

	public bool IsAvailable => TryCompile();

	/// <summary>Why compaction is unavailable, or null while it has not been asked for or is working.</summary>
	public string? UnavailableReason => _unavailableReason;

	internal MTLComputePipelineState PipelineState => _pipelineState;

	/// <summary>
	/// Builds the argument buffer the kernel reads its two command buffers through. Metal has no way to
	/// bind an indirect command buffer directly to a compute encoder, so the pair travels in an argument
	/// buffer, which is fixed for the lifetime of the page and worth building once.
	/// </summary>
	public MTLBuffer CreateArgumentBuffer(
		MTLIndirectCommandBuffer source,
		MTLIndirectCommandBuffer destination)
	{
		if (TryCompile() == false)
		{
			return default;
		}

		var argumentBuffer = _device.NewBuffer(
			_argumentEncoder.EncodedLength,
			MTLResourceOptions.ResourceStorageModeShared);
		if (argumentBuffer.NativePtr == IntPtr.Zero)
		{
			return default;
		}

		_argumentEncoder.SetArgumentBuffer(argumentBuffer, 0);
		_argumentEncoder.SetIndirectCommandBuffer(source, SourceCommandBufferArgumentIndex);
		_argumentEncoder.SetIndirectCommandBuffer(destination, DestinationCommandBufferArgumentIndex);
		return argumentBuffer;
	}

	private bool TryCompile()
	{
		lock (_compileSync)
		{
			if (_compileAttempted)
			{
				return _unavailableReason is null;
			}

			_compileAttempted = true;
			try
			{
				CompileLocked();
			}
			catch (Exception exception)
			{
				_unavailableReason = exception.Message;
				ReleaseLocked();
			}

			return _unavailableReason is null;
		}
	}

	private void CompileLocked()
	{
		using var source = NSStringHelper.From(ReadSource());
		using var options = new MTLCompileOptions
		{
			// copy_command needs a language version that has GPU-side command encoding; pinning it keeps
			// the kernel off whatever default the running toolchain happens to pick.
			LanguageVersion = MTLLanguageVersion.Version30
		};

		var libraryError = new NSError(IntPtr.Zero);
		_library = _device.NewLibrary(source, options, ref libraryError);
		if (_library.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"Failed to compile the Metal indirect command compaction kernel: {DescribeError(libraryError)}");
		}

		using var entryPoint = NSStringHelper.From(EntryPoint);
		_function = _library.NewFunction(entryPoint);
		if (_function.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"The Metal indirect command compaction library does not define '{EntryPoint}'.");
		}

		var pipelineError = new NSError(IntPtr.Zero);
		_pipelineState = _device.NewComputePipelineState(_function, ref pipelineError);
		if (_pipelineState.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"Failed to create the Metal indirect command compaction pipeline state: {DescribeError(pipelineError)}");
		}

		_argumentEncoder = _function.NewArgumentEncoder(CommandBuffersBufferIndex);
		if (_argumentEncoder.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				"The Metal indirect command compaction kernel did not expose an argument encoder for its command buffers.");
		}
	}

	private static string ReadSource()
	{
		using var stream = typeof(MetalIndirectCompactionKernel).Assembly
			                   .GetManifestResourceStream(SourceResourceName)
		                   ?? throw new InvalidOperationException(
			                   $"The embedded Metal compaction shader '{SourceResourceName}' is missing from the assembly.");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	private static string DescribeError(NSError error) =>
		error.NativePtr == IntPtr.Zero
			? "Unknown Metal error."
			: error.LocalizedDescription.ToManagedString("Unknown Metal error.");

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		lock (_compileSync)
		{
			ReleaseLocked();
		}

		_disposed = true;
	}

	private void ReleaseLocked()
	{
		if (_argumentEncoder.NativePtr != IntPtr.Zero)
		{
			_argumentEncoder.Dispose();
			_argumentEncoder = default;
		}

		if (_pipelineState.NativePtr != IntPtr.Zero)
		{
			_pipelineState.Dispose();
			_pipelineState = default;
		}

		if (_function.NativePtr != IntPtr.Zero)
		{
			_function.Dispose();
			_function = default;
		}

		if (_library.NativePtr != IntPtr.Zero)
		{
			_library.Dispose();
			_library = default;
		}
	}
}

/// <summary>
/// Per-page inputs to <c>CSCompactIndirectCommands</c>. Mirrors <c>CompactionParams</c> in
/// gpu_draw_compact_icb.metal.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MetalIndirectCompactionParams
{
	public uint PageStartCommandIndex;
	public uint PageCommandCapacity;
	public uint LaneIndex;
	public uint ExecutionRangeIndex;
	public uint ActiveDrawCommandUpperBound;
}
