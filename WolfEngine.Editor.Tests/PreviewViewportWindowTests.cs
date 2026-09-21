using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class PreviewViewportWindowTests
{
	[Test]
	public void PreviewWindowIsAvailableButDoesNotCreateAViewUntilDrawn()
	{
		var viewHost = Substitute.For<IRenderViewHost>();
		var viewportStateBus = new EditorViewportStateBus();
		var window = new PreviewViewportWindow(
			viewHost,
			Substitute.For<IWorldManager>(),
			new EditorRenderViews(),
			viewportStateBus);

		window.OnHidden();

		Assert.Multiple(() =>
		{
			Assert.That(EditorWindowIds.All, Does.Contain(EditorWindowIds.Preview));
			Assert.That(window.Name, Is.EqualTo("Preview"));
			Assert.That(viewportStateBus.GetViews(), Is.Empty);
		});
		viewHost.DidNotReceiveWithAnyArgs().CreateView(default);
	}
}
