using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Utility;

namespace WolfEngine.Modeling;

/// <summary>
/// Samples a shape program on the GPU with <c>shape_sdf.compute.slang</c> and reads the distances back.
/// <para>
/// The dispatch runs on the render thread through <see cref="IMainThreadDispatcher"/> and waits for the device to go
/// idle before reading back, as the terrain brushes and texture compression do. That stalls rendering for the length
/// of the dispatch, which is acceptable at prototype grid sizes; an asynchronous readback is the fix if it is not.
/// </para>
/// <para>
/// Buffers are kept between calls and only grow, because a drag recompiles every frame at much the same size.
/// </para>
/// </summary>
public sealed class GpuSdfGridSampler : ISdfGridSampler, IDisposable
{
	private const string EntryPoint = "SampleShapeSdfGrid";
	private const uint ConstantsSlot = 11;

	private readonly IRenderer _renderer;
	private readonly IShaderProvider _shaders;
	private readonly IMainThreadDispatcher _mainThread;
	private readonly object _sync = new();

	private IGfxPipeline? _pipeline;
	private ComputeThreadGroupSize _threadGroupSize;
	private GraphicsBackendKind? _pipelineBackend;
	private long _pipelineShaderRevision = -1;

	private IGfxBuffer? _instructionBuffer;
	private ulong _instructionCapacityBytes;
	private IGfxBuffer? _distanceBuffer;
	private IGfxBuffer? _readbackBuffer;
	private ulong _distanceCapacityBytes;
	private bool _disposed;

	public GpuSdfGridSampler(IRenderer renderer, IShaderProvider shaders, IMainThreadDispatcher mainThread)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaders = shaders ?? throw new ArgumentNullException(nameof(shaders));
		_mainThread = mainThread ?? throw new ArgumentNullException(nameof(mainThread));
	}

	public string Name => "gpu";

	public SdfGrid Sample(ShapeSdfProgram program, SdfGridLayout layout)
	{
		ArgumentNullException.ThrowIfNull(program);

		// Copied so the render thread never touches the caller's program while it runs.
		var instructions = program.Instructions.ToArray();
		return _mainThread.Invoke(() => SampleOnMainThread(instructions, layout));
	}

	public void Dispose()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
		}

		_mainThread.Invoke(() =>
		{
			lock (_sync)
			{
				ReleaseBuffers();
			}
		});
	}

	private SdfGrid SampleOnMainThread(ShapeSdfInstruction[] instructions, SdfGridLayout layout)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			var device = _renderer.GetGfxDevice()
				?? throw new InvalidOperationException("Sampling a shape on the GPU needs an initialized graphics device.");
			EnsurePipeline(device);

			var sampleCount = checked((int)layout.SampleCount);
			var distanceBytes = (ulong)sampleCount * sizeof(float);
			EnsureBuffers(device, instructions.Length, distanceBytes);

			if (instructions.Length > 0)
			{
				if (_instructionBuffer is not IWritableGpuBuffer writable)
				{
					throw new InvalidOperationException("The shape instruction buffer does not accept CPU writes.");
				}

				writable.Write<ShapeSdfInstruction>(instructions);
			}

			var constants = new GridConstants(
				new Vector4(layout.Origin, layout.CellSize),
				(uint)layout.SizeX,
				(uint)layout.SizeY,
				(uint)layout.SizeZ,
				(uint)instructions.Length);

			var commandList = device.BeginCompute();
			try
			{
				commandList.BindPipeline(_pipeline!);
				commandList.SetComputeConstants(ConstantsSlot, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref constants, 1)));
				commandList.SetComputeBuffer(0, _instructionBuffer!);
				commandList.SetComputeBuffer(1, _distanceBuffer!);
				var (groupsX, groupsY, groupsZ) = _threadGroupSize.GetDispatchGroupCount(
					(uint)layout.SizeX,
					(uint)layout.SizeY,
					(uint)layout.SizeZ);
				commandList.Dispatch(groupsX, groupsY, groupsZ);
				commandList.CopyBuffer(_distanceBuffer!, 0, _readbackBuffer!, 0, distanceBytes);
			}
			finally
			{
				device.Submit(commandList);
			}

			device.WaitForIdle();
			if (_readbackBuffer is not IReadableGpuBuffer readable)
			{
				throw new InvalidOperationException("The shape readback buffer does not support CPU reads.");
			}

			var distances = new float[sampleCount];
			readable.Read(MemoryMarshal.AsBytes(distances.AsSpan()));
			return new SdfGrid(layout, distances);
		}
	}

	private void EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null &&
		    _pipelineBackend == device.BackendKind &&
		    _pipelineShaderRevision == _shaders.Revision)
		{
			return;
		}

		var compiled = _shaders.GetComputeShaderWithReflection(EngineShaderPrograms.ShapeSdfSample, EntryPoint, device.BackendKind);
		_pipeline = device.GetOrCreatePipeline(
			new PipelineKey(
				PassKind.Compute,
				null,
				null,
				EntryPoint,
				new RenderTargetFormats(Array.Empty<TextureFormat>()),
				new DepthStencilFormat(TextureFormat.Unknown),
				default,
				shaderVariant: EngineShaderPrograms.ShapeSdfSample.Value),
			new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: compiled.ThreadGroupSize));
		_threadGroupSize = compiled.ThreadGroupSize;
		_pipelineBackend = device.BackendKind;
		_pipelineShaderRevision = _shaders.Revision;
	}

	private void EnsureBuffers(IGfxDevice device, int instructionCount, ulong distanceBytes)
	{
		// A buffer cannot be empty, so an empty program still gets room for one instruction.
		var instructionBytes = (ulong)Math.Max(1, instructionCount) * (ulong)Marshal.SizeOf<ShapeSdfInstruction>();
		if (_instructionBuffer is null || _instructionCapacityBytes < instructionBytes)
		{
			Dispose(_instructionBuffer);
			_instructionCapacityBytes = GrowCapacity(instructionBytes);
			_instructionBuffer = device.CreateBuffer(new BufferDescriptor(
				_instructionCapacityBytes,
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess,
				"Shape SDF instructions"));
		}

		if (_distanceBuffer is null || _distanceCapacityBytes < distanceBytes)
		{
			Dispose(_distanceBuffer);
			Dispose(_readbackBuffer);
			_distanceCapacityBytes = GrowCapacity(distanceBytes);
			_distanceBuffer = device.CreateBuffer(new BufferDescriptor(
				_distanceCapacityBytes,
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess,
				"Shape SDF distances"));
			_readbackBuffer = device.CreateBuffer(new BufferDescriptor(
				_distanceCapacityBytes,
				BufferUsage.Staging,
				BufferFlags.None,
				"Shape SDF readback"));
		}
	}

	private void ReleaseBuffers()
	{
		Dispose(_instructionBuffer);
		Dispose(_distanceBuffer);
		Dispose(_readbackBuffer);
		_instructionBuffer = null;
		_distanceBuffer = null;
		_readbackBuffer = null;
		_instructionCapacityBytes = 0;
		_distanceCapacityBytes = 0;
	}

	private static ulong GrowCapacity(ulong required)
	{
		// Half again as large, so a shape that grows a little during a drag does not reallocate every frame.
		return required + required / 2;
	}

	private static void Dispose(IGfxBuffer? buffer)
	{
		if (buffer is IDisposable disposable)
		{
			disposable.Dispose();
		}
	}

	/// <summary>
	/// Matches <c>ShapeSdfGridParams</c> in the shader. Whole vectors only: a float3 followed by a scalar packs
	/// differently on Metal, where a float3 takes 16 bytes.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	private readonly record struct GridConstants(
		Vector4 OriginAndCellSize,
		uint SizeX,
		uint SizeY,
		uint SizeZ,
		uint InstructionCount);
}
