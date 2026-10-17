using WolfEngine.Rendering;

namespace WolfEngine.Tests;

public sealed class RenderGraphContextViewTests
{
	[Test]
	public void ViewSnapshot_CanSelectASecondaryViewWithoutReadingPrimaryFacade()
	{
		var frame = new FrameSnapshot();
		var primary = frame.GetOrCreateView(RenderViewId.Primary);
		var secondary = frame.GetOrCreateView(RenderViewId.FromIndex(1));
		var context = new RenderGraphContext(new RenderGraphResourceRegistry(), "secondary")
		{
			FrameSnapshot = frame,
			ViewSnapshot = secondary,
			GpuDrawDatabase = secondary.GpuDrawDatabase
		};

		Assert.Multiple(() =>
		{
			Assert.That(context.FrameSnapshot, Is.SameAs(frame));
			Assert.That(context.ViewSnapshot, Is.SameAs(secondary));
			Assert.That(context.ViewSnapshot, Is.Not.SameAs(primary));
			Assert.That(context.GpuDrawDatabase, Is.SameAs(secondary.GpuDrawDatabase));
		});
	}
}
