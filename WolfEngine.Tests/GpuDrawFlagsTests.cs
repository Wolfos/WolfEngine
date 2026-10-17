using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class GpuDrawFlagsTests
{
	[TestCase("gpu_draw_cull.compute.slang")]
	[TestCase("gpu_draw_compact.compute.slang")]
	public void SlangDecoders_MatchTheFlagsTheEngineEncodes(string shader)
	{
		// The cull and compaction shaders decode GpuDrawCommand.flags themselves. A layout change that misses one
		// of them misreads every draw's bucket or owner, and the symptom is draws vanishing from a view.
		var source = File.ReadAllText(Path.Combine(
			MetalIndirectCompactionKernelTests.ResolveShaderDirectory(), "GpuDraw", shader));
		Assert.Multiple(() =>
		{
			Assert.That(MetalIndirectCompactionKernelTests.ReadUintConstant(source, "DRAW_FLAG_ACTIVE"), Is.EqualTo(GpuDrawFlags.Active));
			Assert.That(MetalIndirectCompactionKernelTests.ReadUintConstant(source, "DRAW_FLAG_BUCKET_SHIFT"), Is.EqualTo((uint)GpuDrawFlags.BucketShift));
			Assert.That(MetalIndirectCompactionKernelTests.ReadUintConstant(source, "DRAW_FLAG_BUCKET_MASK"), Is.EqualTo(GpuDrawFlags.BucketMask));
			Assert.That(MetalIndirectCompactionKernelTests.ReadUintConstant(source, "DRAW_FLAG_OWNER_VIEW_SHIFT"), Is.EqualTo((uint)GpuDrawFlags.OwnerViewShift));
			Assert.That(MetalIndirectCompactionKernelTests.ReadUintConstant(source, "DRAW_FLAG_OWNER_VIEW_MASK"), Is.EqualTo(GpuDrawFlags.OwnerViewMask));
		});
	}

	[Test]
	public void BucketAndOwnerFieldsDoNotOverlapAndCoverEveryViewSlot()
	{
		var bucketBits = GpuDrawFlags.BucketMask << GpuDrawFlags.BucketShift;
		var ownerBits = GpuDrawFlags.OwnerViewMask << GpuDrawFlags.OwnerViewShift;
		Assert.Multiple(() =>
		{
			Assert.That(bucketBits & ownerBits, Is.Zero);
			Assert.That(bucketBits & GpuDrawFlags.Active, Is.Zero);
			Assert.That(GpuDrawFlags.OwnerViewMask + 1, Is.GreaterThanOrEqualTo((uint)Rendering.UI.UiTextureIds.MaxViewports));
			Assert.That(GpuDrawFlags.BucketMask + 1, Is.EqualTo(32u), "the cull's participation mask caps lanes at 32");
		});
	}

	[Test]
	public void CreateRoundTripsBucketAndOwner()
	{
		var owner = RenderViewId.FromIndex(Rendering.UI.UiTextureIds.MaxViewports - 1);
		var flags = GpuDrawFlags.Create(31, owner);
		Assert.Multiple(() =>
		{
			Assert.That(flags & GpuDrawFlags.Active, Is.EqualTo(GpuDrawFlags.Active));
			Assert.That(GpuDrawFlags.GetBucket(flags), Is.EqualTo(31));
			Assert.That(GpuDrawFlags.GetOwnerView(flags), Is.EqualTo(owner));
		});
	}
}
