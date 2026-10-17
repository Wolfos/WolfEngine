using WolfEngine.Rendering.UI;

namespace WolfEngine.Tests;

public sealed class GameplayUiTextureRevisionTests
{
	[Test]
	public void DrawAcknowledgementNotPublicationDeterminesRedraw()
	{
		var target = new GameplayUiTextureRevision();
		Assert.That(target.NeedsRedraw(0), Is.True, "New targets need initialization, even at revision zero.");
		target.MarkRendered(0);
		Assert.That(target.NeedsRedraw(0), Is.False);
		Assert.That(target.NeedsRedraw(10), Is.True, "Intermediate snapshots may be dropped.");
		Assert.That(target.NeedsRedraw(10), Is.True, "Checking a snapshot must not acknowledge a draw.");
		target.MarkRendered(10);
		Assert.That(target.NeedsRedraw(10), Is.False);
		Assert.That(new GameplayUiTextureRevision().NeedsRedraw(10), Is.True, "Recreated targets must redraw unchanged content.");
	}
}
