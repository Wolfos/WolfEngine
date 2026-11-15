using WolfEngine.Build;

namespace WolfEngine.Editor.Tests;

public sealed class GameExecutableNameTests
{
	[Test]
	public void UsesTheProjectFolderNameWhenNoneIsConfigured()
	{
		Assert.That(GameBuilder.GetExecutableName("/Projects/My Game/", ""), Is.EqualTo("My Game"));
		Assert.That(GameBuilder.GetExecutableName("/Projects/My Game", null), Is.EqualTo("My Game"));
	}

	[Test]
	public void UsesTheConfiguredNameOverTheFolderName()
	{
		Assert.That(GameBuilder.GetExecutableName("/Projects/WolfEngineGame", "  Sword & Sparrow "), Is.EqualTo("Sword _ Sparrow"));
	}

	[Test]
	public void FallsBackToGameWhenNothingUsableRemains()
	{
		Assert.That(GameBuilder.GetExecutableName("/Projects/WolfEngineGame", "..."), Is.EqualTo("Game"));
	}
}
