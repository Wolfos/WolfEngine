using Moq;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class PerViewDrawResourcesTests
{
	[Test]
	public void CpuWrittenBuffers_AreSeparatePerView()
	{
		// The frame is one command list submitted at the end, so a buffer the CPU fills in two views' passes would
		// hold the second view's data by the time the GPU ran the first view's.
		var device = CreateDevice(out _);
		var resources = new GpuDrawResources(new Mock<IShaderProvider>().Object);

		resources.ActiveViewIndex = 0;
		resources.EnsureDecalCapacity(device, 4);
		var primary = resources.DecalProjectorBuffer;
		resources.ActiveViewIndex = 1;
		resources.EnsureDecalCapacity(device, 4);
		var second = resources.DecalProjectorBuffer;
		resources.ActiveViewIndex = 0;

		Assert.Multiple(() =>
		{
			Assert.That(primary, Is.Not.Null);
			Assert.That(second, Is.Not.Null);
			Assert.That(second, Is.Not.SameAs(primary));
			Assert.That(resources.DecalProjectorBuffer, Is.SameAs(primary), "switching back must return the first view's buffer");
		});
	}

	[Test]
	public void ReleaseView_RetiresThatViewsBuffersAndKeepsThePrimarys()
	{
		var device = CreateDevice(out var deviceMock);
		var resources = new GpuDrawResources(new Mock<IShaderProvider>().Object);
		resources.ActiveViewIndex = 0;
		resources.EnsureDecalCapacity(device, 4);
		var primary = resources.DecalProjectorBuffer;
		resources.ActiveViewIndex = 1;
		resources.EnsureDecalCapacity(device, 4);

		resources.ReleaseView(device, 1);

		Assert.Multiple(() =>
		{
			// Retired, not disposed: frames still in flight may read them.
			deviceMock.Verify(
				d => d.Retire(It.IsAny<Action>(), It.IsAny<string?>()),
				Times.Exactly(GpuDrawResources.MaxFramesInFlight));
			Assert.That(resources.ActiveViewIndex, Is.Zero, "releasing the active view falls back to the primary");
			Assert.That(resources.DecalProjectorBuffer, Is.SameAs(primary));
		});

		resources.ReleaseView(device, 0);
		Assert.That(resources.DecalProjectorBuffer, Is.SameAs(primary), "the primary view's buffers are never released");
	}

	[Test]
	public void PerViewIndirectCommandSets_AreDistinctPerViewAndPerSetAndStartFreshAfterTake()
	{
		var sets = new PerViewIndirectCommandSets(setsPerView: 3);
		var primaryCascade0 = sets.Get(0, 0);

		Assert.Multiple(() =>
		{
			Assert.That(sets.Get(0, 0), Is.SameAs(primaryCascade0));
			Assert.That(sets.Get(0, 1), Is.Not.SameAs(primaryCascade0));
			Assert.That(sets.Get(1, 0), Is.Not.SameAs(primaryCascade0));
			Assert.Throws<ArgumentOutOfRangeException>(() => sets.Get(0, 3));
		});

		var secondViewSets = sets.Take(1);
		Assert.Multiple(() =>
		{
			Assert.That(secondViewSets, Has.Count.EqualTo(3));
			Assert.That(sets.Get(1, 0), Is.Not.SameAs(secondViewSets[0]), "a reused slot must not inherit encoded records");
			Assert.That(sets.Take(7), Is.Empty);
		});
	}

	[Test]
	public void InvalidateEncoding_ForcesAFullReencodeOfEverySlot()
	{
		// A set that stops being used no longer holds back replay-log compaction, so the records it would need to
		// catch up may be gone by its next use; invalidating makes that use re-encode instead of replaying.
		var set = new SharedDrawIndirectCommandSet();
		set.MarkSlotEncoded(slotIndex: 0, frameSlot: 0, bindlessEpoch: 1, bindingVersion: 1, structuralVersion: 5);
		Assert.That(set.RequiresFullReencode(0, 0, 1, 1), Is.False);

		set.InvalidateEncoding();

		Assert.Multiple(() =>
		{
			Assert.That(set.RequiresFullReencode(0, 0, 1, 1), Is.True);
			Assert.That(set.GetAppliedStructuralVersion(0), Is.Zero);
		});
	}

	private static IGfxDevice CreateDevice(out Mock<IGfxDevice> deviceMock)
	{
		deviceMock = new Mock<IGfxDevice>();
		deviceMock
			.Setup(d => d.CreateBuffer(It.Ref<BufferDescriptor>.IsAny))
			.Returns(new CreateBufferCallback((in BufferDescriptor descriptor) =>
			{
				var buffer = new Mock<IGfxBuffer>();
				var copy = descriptor;
				buffer.SetupGet(b => b.Descriptor).Returns(copy);
				buffer.As<IDisposable>();
				return buffer.Object;
			}));
		return deviceMock.Object;
	}

	private delegate IGfxBuffer CreateBufferCallback(in BufferDescriptor descriptor);
}
