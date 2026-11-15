using Moq;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class ViewHistoryTests
{
	private const int SlotCount = 2;

	[Test]
	public void NewHistoryIsUnallocatedAndInvalid()
	{
		var history = CreateHistory();

		Assert.Multiple(() =>
		{
			Assert.That(history.IsAllocated, Is.False);
			Assert.That(history.IsValid, Is.False);
			Assert.That(history.IsImported, Is.False);
			Assert.That(history.Previous().IsValid, Is.False);
			Assert.That(history.Current().IsValid, Is.False);
		});
	}

	[Test]
	public void EnsureAllocatesBothSidesOfEverySlotOnceForTheSameShape()
	{
		var device = new HistoryDevice();
		var history = CreateHistory();

		var first = history.Ensure(device.Object, new Int2(64, 32));
		var second = history.Ensure(device.Object, new Int2(64, 32));

		Assert.Multiple(() =>
		{
			Assert.That(first, Is.True);
			Assert.That(second, Is.False, "an unchanged shape must keep the textures and their history");
			Assert.That(device.CreatedTextures, Has.Count.EqualTo(SlotCount * 2));
			Assert.That(history.GetTexture(1, 0)!.Descriptor.Format, Is.EqualTo(TextureFormat.R32Float));
			Assert.That(history.GetTexture(0, 1)!.Descriptor.Width, Is.EqualTo(64));
			Assert.That(history.IsValid, Is.False, "a new history has no previous frame");
		});
	}

	[Test]
	public void CompletingAnImportedFrameSwapsTheSidesAndMakesHistoryValid()
	{
		var device = new HistoryDevice();
		var history = CreateHistory();
		history.Ensure(device.Object, new Int2(8, 8));
		var writtenTexture = history.GetTexture(0, 1 - history.ReadIndex);

		RunFrame(history);

		Assert.Multiple(() =>
		{
			Assert.That(history.IsValid, Is.True);
			Assert.That(history.IsImported, Is.False);
			Assert.That(
				history.GetTexture(0, history.ReadIndex),
				Is.SameAs(writtenTexture),
				"what this frame wrote must be what next frame reads");
		});
	}

	[Test]
	public void CompletingAFrameThatDidNotImportInvalidatesButKeepsTheTextures()
	{
		var device = new HistoryDevice();
		var history = CreateHistory();
		history.Ensure(device.Object, new Int2(8, 8));
		RunFrame(history);
		var readIndex = history.ReadIndex;

		history.Complete(new RenderGraphResourceRegistry());

		Assert.Multiple(() =>
		{
			Assert.That(history.IsValid, Is.False, "a frame that skipped the effect wrote nothing to read back");
			Assert.That(history.ReadIndex, Is.EqualTo(readIndex), "the sides must not swap when nothing was written");
			Assert.That(history.IsAllocated, Is.True);
		});
	}

	[Test]
	public void CompleteRecordsTheStateEachTextureWasLeftInForNextFramesImport()
	{
		var device = new HistoryDevice();
		var history = CreateHistory();
		history.Ensure(device.Object, new Int2(8, 8));
		var firstFrame = new RenderGraphResourceRegistry();
		history.Import(firstFrame);
		firstFrame.SetResourceState(history.Current(), ResourceState.ShaderResource);
		history.Complete(firstFrame);

		var secondFrame = new RenderGraphResourceRegistry();
		history.Import(secondFrame);

		Assert.That(secondFrame.GetResourceState(history.Previous()), Is.EqualTo(ResourceState.ShaderResource));
	}

	[Test]
	public void InvalidateKeepsTheTexturesAndTheNextCompletedFrameRestoresHistory()
	{
		var device = new HistoryDevice();
		var history = CreateHistory();
		history.Ensure(device.Object, new Int2(8, 8));
		RunFrame(history);

		history.Invalidate();
		var invalidated = history.IsValid;
		RunFrame(history);

		Assert.Multiple(() =>
		{
			Assert.That(invalidated, Is.False);
			Assert.That(history.IsValid, Is.True);
			Assert.That(device.CreatedTextures, Has.Count.EqualTo(SlotCount * 2), "invalidation must not reallocate");
		});
	}

	[Test]
	public void ShapeChangeRetiresTheOldTexturesAndStartsANewInvalidHistory()
	{
		var device = new HistoryDevice();
		var history = CreateHistory();
		history.Ensure(device.Object, new Int2(8, 8));
		RunFrame(history);

		var reallocated = history.Ensure(device.Object, new Int2(16, 8));

		Assert.Multiple(() =>
		{
			Assert.That(reallocated, Is.True);
			Assert.That(history.IsValid, Is.False);
			Assert.That(history.Shape, Is.EqualTo(new Int2(16, 8)));
			Assert.That(device.RetiredCount, Is.EqualTo(SlotCount * 2), "the old textures go through the retirement queue");
			Assert.That(device.CreatedTextures, Has.Count.EqualTo(SlotCount * 4));
		});
	}

	[Test]
	public void DeviceChangeReallocatesOnTheNewDeviceAndRetiresOnTheOld()
	{
		var oldDevice = new HistoryDevice();
		var newDevice = new HistoryDevice();
		var history = CreateHistory();
		history.Ensure(oldDevice.Object, new Int2(8, 8));
		RunFrame(history);

		var reallocated = history.Ensure(newDevice.Object, new Int2(8, 8));

		Assert.Multiple(() =>
		{
			Assert.That(reallocated, Is.True);
			Assert.That(history.IsValid, Is.False);
			Assert.That(history.Device, Is.SameAs(newDevice.Object));
			Assert.That(oldDevice.RetiredCount, Is.EqualTo(SlotCount * 2));
			Assert.That(newDevice.RetiredCount, Is.Zero);
		});
	}

	[Test]
	public void ReleaseRetiresEveryTextureAndReturnsToUnallocated()
	{
		var device = new HistoryDevice();
		var history = CreateHistory();
		history.Ensure(device.Object, new Int2(8, 8));
		RunFrame(history);

		history.Release();

		Assert.Multiple(() =>
		{
			Assert.That(device.RetiredCount, Is.EqualTo(SlotCount * 2));
			Assert.That(history.IsAllocated, Is.False);
			Assert.That(history.IsValid, Is.False);
			Assert.That(history.ReadIndex, Is.Zero);
			Assert.That(history.Shape, Is.EqualTo(default(Int2)));
			Assert.That(history.GetTexture(0, 0), Is.Null);
		});
	}

	[Test]
	public void ImportBeforeEnsureThrows()
	{
		var history = CreateHistory();

		Assert.Throws<InvalidOperationException>(() => history.Import(new RenderGraphResourceRegistry()));
	}

	/// <summary>Gives <paramref name="history"/> one completed frame, so it holds valid history.</summary>
	internal static void AccumulateOneFrame<TShape>(ViewHistory<TShape> history, TShape shape)
		where TShape : struct, IEquatable<TShape>
	{
		history.Ensure(new HistoryDevice().Object, shape);
		RunFrame(history);
	}

	private static void RunFrame(ViewHistory history)
	{
		var registry = new RenderGraphResourceRegistry();
		history.Import(registry);
		history.Complete(registry);
	}

	private static ViewHistory<Int2> CreateHistory()
	{
		return new ViewHistory<Int2>(
			"Test",
			SlotCount,
			(size, slot) => new TextureDescriptor(
				size.X,
				size.Y,
				slot == 0 ? TextureFormat.Rgba16Float : TextureFormat.R32Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
	}

	private delegate IGfxTexture CreateTextureCallback(in TextureDescriptor descriptor);

	/// <summary>A device that creates textures matching each descriptor and counts retirements.</summary>
	private sealed class HistoryDevice
	{
		public HistoryDevice()
		{
			var device = new Mock<IGfxDevice>();
			device.SetupGet(value => value.BackendKind).Returns(GraphicsBackendKind.Metal);
			device
				.Setup(value => value.CreateTexture(in It.Ref<TextureDescriptor>.IsAny))
				.Returns(new CreateTextureCallback((in TextureDescriptor descriptor) => CreateTexture(descriptor)));
			device
				.Setup(value => value.Retire(It.IsAny<Action>(), It.IsAny<string?>()))
				.Callback(() => RetiredCount++);
			Object = device.Object;
		}

		public IGfxDevice Object { get; }

		public List<IGfxTexture> CreatedTextures { get; } = [];

		public int RetiredCount { get; private set; }

		private IGfxTexture CreateTexture(TextureDescriptor descriptor)
		{
			var texture = new Mock<IGfxTexture>();
			texture.SetupGet(value => value.Descriptor).Returns(descriptor);
			CreatedTextures.Add(texture.Object);

			return texture.Object;
		}
	}
}
