using Microsoft.Extensions.DependencyInjection;
using WolfEngine.ECS;
using WolfEngine.Gameplay;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class ApplicationQuitRequestsTests
{
	[Test]
	public void Quit_IsTakenOnceByTheWorldThatRequestedIt()
	{
		var requests = new ApplicationQuitRequests();
		var world = new World(WorldTag.Game);

		requests.Quit(world);

		Assert.That(requests.TryTake(world), Is.True);
		Assert.That(requests.TryTake(world), Is.False, "a request is taken once");
	}

	[Test]
	public void TryTake_WithoutARequest_ReturnsFalse()
	{
		var requests = new ApplicationQuitRequests();

		Assert.That(requests.TryTake(new World(WorldTag.Game)), Is.False);
		Assert.That(requests.TryTake(null), Is.False);
	}

	[Test]
	public void Quit_FromAWorldThatIsNoLongerActive_IsDropped()
	{
		var requests = new ApplicationQuitRequests();
		var replaced = new World(WorldTag.Game);
		var active = new World(WorldTag.Game);

		requests.Quit(replaced);

		Assert.That(requests.TryTake(active), Is.False, "a scene load replaced the requesting world");
		Assert.That(requests.TryTake(replaced), Is.False, "the stale request was dropped, not kept for later");
	}

	[Test]
	public void Quit_AfterPlayModeStopped_DoesNotStopTheNextPlaySession()
	{
		var requests = new ApplicationQuitRequests();
		var stopped = new World(WorldTag.Game);

		requests.Quit(stopped);

		// Edit mode: the editor takes requests every frame with no runtime world.
		Assert.That(requests.TryTake(null), Is.False);

		var next = new World(WorldTag.Game);

		Assert.That(requests.TryTake(next), Is.False);
	}

	[Test]
	public void Quit_RequiresARuntimeWorld()
	{
		var requests = new ApplicationQuitRequests();

		Assert.Throws<ArgumentNullException>(() => requests.Quit(null!));
		Assert.Throws<ArgumentException>(() => requests.Quit(new World(WorldTag.Editor)));
	}

	[Test]
	public void Quit_WhenThePlatformDoesNotSupportIt_Throws()
	{
		var requests = new ApplicationQuitRequests(isQuitSupported: false);
		var world = new World(WorldTag.Game);

		Assert.That(requests.IsQuitSupported, Is.False);
		Assert.Throws<NotSupportedException>(() => requests.Quit(world));
		Assert.That(requests.TryTake(world), Is.False);
	}

	[Test]
	public void EngineServices_ExposeOneSharedQuitService()
	{
		var services = new ServiceCollection();
		global::WolfEngine.WolfEngine.ConfigureServices(services);
		using var provider = services.BuildServiceProvider();

		var gameplayFacing = provider.GetRequiredService<IApplicationQuitService>();
		var hostFacing = provider.GetRequiredService<ApplicationQuitRequests>();
		var world = new World(WorldTag.Game);

		Assert.That(gameplayFacing.IsQuitSupported, Is.True);

		gameplayFacing.Quit(world);

		Assert.That(hostFacing.TryTake(world), Is.True);
	}
}
