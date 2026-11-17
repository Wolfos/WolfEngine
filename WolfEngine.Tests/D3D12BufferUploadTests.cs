using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.D3D12;

namespace WolfEngine.Tests;

/// <summary>
/// CPU writes to GPU-only buffers on D3D12, against a real device on the WARP software adapter. The graphics queue is
/// held behind a fence the test controls, which stands in for a GPU still busy with earlier frames.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class D3D12BufferUploadTests
{
	private const int ElementCount = 64;
	private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

	[Test]
	public void WriteDoesNotWaitForWorkAlreadyQueuedOnTheGpu()
	{
		using var gpu = WarpGpu.CreateOrIgnore();
		var buffer = gpu.CreateGpuOnlyBuffer();

		gpu.HoldQueue();
		try
		{
			var write = Task.Run(() => buffer.Write<uint>(Fill(1)));

			Assert.That(write.Wait(WriteTimeout), Is.True,
				"Write blocked until the GPU finished work queued before it.");
		}
		finally
		{
			gpu.ReleaseQueue();
		}
	}

	[Test]
	public void EachWriteIsSeenByTheGpuWorkSubmittedAfterIt()
	{
		using var gpu = WarpGpu.CreateOrIgnore();
		var buffer = gpu.CreateGpuOnlyBuffer();
		var afterFirst = gpu.CreateReadbackBuffer();
		var afterSecond = gpu.CreateReadbackBuffer();

		gpu.HoldQueue();
		try
		{
			// Both writes land while the GPU is still held, so the second must not overwrite the data the first
			// copy reads from.
			Assert.That(Task.Run(() =>
			{
				buffer.Write<uint>(Fill(1));
				gpu.CopyOnGpu(buffer, afterFirst);
				buffer.Write<uint>(Fill(2));
				gpu.CopyOnGpu(buffer, afterSecond);
			}).Wait(WriteTimeout), Is.True, "Write blocked until the GPU finished work queued before it.");
		}
		finally
		{
			gpu.ReleaseQueue();
		}

		gpu.Device.WaitForIdle();
		Assert.That(gpu.ReadBack(afterFirst), Is.EqualTo(Fill(1)), "The first copy saw data written after it.");
		Assert.That(gpu.ReadBack(afterSecond), Is.EqualTo(Fill(2)));
	}

	[Test]
	public void ReadbackBuffersRefuseCpuWrites()
	{
		using var gpu = WarpGpu.CreateOrIgnore();
		var readback = (IWritableGpuBuffer)gpu.CreateReadbackBuffer();

		Assert.Throws<InvalidOperationException>(() => readback.Write<uint>(Fill(1)));
	}

	private static uint[] Fill(uint value) => Enumerable.Repeat(value, ElementCount).ToArray();

	private sealed class WarpGpu : IDisposable
	{
		private readonly ComPtr<ID3D12CommandQueue> _queue;
		private readonly ComPtr<ID3D12Fence> _gate;
		private readonly List<IDisposable> _buffers = new();
		private bool _held;

		private WarpGpu(ComPtr<ID3D12Device> device, ComPtr<ID3D12CommandQueue> queue)
		{
			_queue = queue;
			SilkMarshal.ThrowHResult(device.CreateFence(0, FenceFlags.None, out _gate));
			Device = new D3D12Device(device, queue);
		}

		public D3D12Device Device { get; }

		public static WarpGpu CreateOrIgnore()
		{
			try
			{
#pragma warning disable CS0618
				var dxgi = DXGI.GetApi();
#pragma warning restore CS0618
				var factory = dxgi.CreateDXGIFactory<IDXGIFactory4>();
				SilkMarshal.ThrowHResult(factory.EnumWarpAdapter(out ComPtr<IDXGIAdapter> adapter));
				SilkMarshal.ThrowHResult(D3D12.GetApi().CreateDevice(adapter, D3DFeatureLevel.Level110, out ComPtr<ID3D12Device> device));
				var queueDescription = new CommandQueueDesc { Type = CommandListType.Direct };
				SilkMarshal.ThrowHResult(device.CreateCommandQueue(in queueDescription, out ComPtr<ID3D12CommandQueue> queue));
				return new WarpGpu(device, queue);
			}
			catch (Exception exception) when (exception is not IgnoreException)
			{
				Assert.Ignore($"No D3D12 WARP device available: {exception.Message}");
				throw;
			}
		}

		public IWritableGpuBuffer CreateGpuOnlyBuffer() => (IWritableGpuBuffer)Track(Device.CreateBuffer(new BufferDescriptor(
			ElementCount * sizeof(uint), BufferUsage.Structured, BufferFlags.AllowShaderResource, "UploadTarget")));

		public IReadableGpuBuffer CreateReadbackBuffer() => (IReadableGpuBuffer)Track(Device.CreateBuffer(new BufferDescriptor(
			ElementCount * sizeof(uint), BufferUsage.Staging, BufferFlags.None, "Readback")));

		/// <summary>Holds every later submission on the queue until <see cref="ReleaseQueue"/>.</summary>
		public void HoldQueue()
		{
			SilkMarshal.ThrowHResult(_queue.Wait(_gate, 1));
			_held = true;
		}

		public void ReleaseQueue()
		{
			if (_held == false)
			{
				return;
			}

			SilkMarshal.ThrowHResult(_gate.Signal(1));
			_held = false;
		}

		public void CopyOnGpu(IGfxBuffer source, IGfxBuffer destination)
		{
			var commandList = Device.BeginGraphics();
			commandList.CopyBuffer(source, 0, destination, 0, ElementCount * sizeof(uint));
			Device.Submit(commandList);
		}

		public uint[] ReadBack(IReadableGpuBuffer buffer)
		{
			var bytes = new byte[ElementCount * sizeof(uint)];
			buffer.Read(bytes);
			var values = new uint[ElementCount];
			Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
			return values;
		}

		public void Dispose()
		{
			ReleaseQueue();
			Device.WaitForIdle();
			foreach (var buffer in _buffers)
			{
				buffer.Dispose();
			}

			Device.Dispose();
			_gate.Dispose();
		}

		private IGfxBuffer Track(IGfxBuffer buffer)
		{
			_buffers.Add((IDisposable)buffer);
			return buffer;
		}
	}
}
