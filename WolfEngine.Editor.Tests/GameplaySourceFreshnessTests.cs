using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class GameplaySourceFreshnessTests
{
	private static readonly DateTime BuildTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

	private string _projectRoot = null!;
	private string _gameplayDirectory = null!;
	private string _gameplayProjectPath = null!;
	private string _assemblyPath = null!;

	[SetUp]
	public void SetUp()
	{
		_projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineGameplayFreshnessTests", Guid.NewGuid().ToString("N"));
		_gameplayDirectory = Path.Combine(_projectRoot, "Gameplay");
		_gameplayProjectPath = Path.Combine(_gameplayDirectory, "Game.Gameplay.csproj");
		_assemblyPath = Path.Combine(_gameplayDirectory, "bin", "Debug", "net10.0", "Game.Gameplay.dll");

		WriteFile(_gameplayProjectPath, BuildTime.AddMinutes(-10));
		WriteFile(Path.Combine(_gameplayDirectory, "Components", "Health.cs"), BuildTime.AddMinutes(-5));
		WriteFile(_assemblyPath, BuildTime);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_projectRoot))
		{
			Directory.Delete(_projectRoot, recursive: true);
		}
	}

	[Test]
	public void WithoutAStampSourceOlderThanTheAssemblyIsFresh()
	{
		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);

		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, stamp: null), Is.False);
	}

	[Test]
	public void ASourceFileEditedAfterTheBuildIsStale()
	{
		var stamp = CreateStamp();

		File.SetLastWriteTimeUtc(Path.Combine(_gameplayDirectory, "Components", "Health.cs"), BuildTime.AddMinutes(1));
		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);

		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, stamp), Is.True);
		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, stamp: null), Is.True);
	}

	[Test]
	public void ADeletedSourceFileIsStaleWhenTheBuildWasStamped()
	{
		// Deleting a file leaves every remaining input older than the assembly, so only the stamp catches it.
		var stamp = CreateStamp();

		File.Delete(Path.Combine(_gameplayDirectory, "Components", "Health.cs"));
		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);

		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, stamp), Is.True);
	}

	[Test]
	public void AStampForADifferentAssemblyWriteFallsBackToTimestamps()
	{
		// An IDE rebuilt the assembly after the editor's last build, so the stamp describes an older output.
		var stamp = CreateStamp();

		File.Delete(Path.Combine(_gameplayDirectory, "Components", "Health.cs"));
		File.SetLastWriteTimeUtc(_assemblyPath, BuildTime.AddMinutes(2));
		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);

		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, stamp), Is.False);
	}

	[Test]
	public void ABuildThatLeavesTheAssemblyUntouchedStaysFresh()
	{
		// An incremental build may not rewrite the assembly. The stamp records the newest input it saw, so the
		// touched source does not make every later check rebuild.
		File.SetLastWriteTimeUtc(Path.Combine(_gameplayDirectory, "Components", "Health.cs"), BuildTime.AddMinutes(1));
		var stamp = CreateStamp();

		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);

		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, stamp), Is.False);
	}

	[Test]
	public void BuildOutputAndUnrelatedFilesAreNotInputs()
	{
		WriteFile(Path.Combine(_gameplayDirectory, "obj", "Debug", "Generated.cs"), BuildTime.AddMinutes(5));
		WriteFile(Path.Combine(_gameplayDirectory, ".idea", "Workspace.cs"), BuildTime.AddMinutes(5));
		WriteFile(Path.Combine(_gameplayDirectory, "Notes.txt"), BuildTime.AddMinutes(5));

		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);

		Assert.That(source.InputPaths, Is.EqualTo(new[] { "Components/Health.cs", "Game.Gameplay.csproj" }));
		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, stamp: null), Is.False);
		Assert.That(GameplaySourceFreshness.CanAffectBuild(_gameplayDirectory, Path.Combine(_gameplayDirectory, "obj", "A.cs")), Is.False);
		Assert.That(GameplaySourceFreshness.CanAffectBuild(_gameplayDirectory, Path.Combine(_gameplayDirectory, "Systems", "A.cs")), Is.True);
	}

	[Test]
	public void BuildPropertiesBetweenTheProjectAndTheRootAreInputs()
	{
		WriteFile(Path.Combine(_projectRoot, "Directory.Build.props"), BuildTime.AddMinutes(1));

		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);

		Assert.That(source.InputPaths, Does.Contain("../Directory.Build.props"));
		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, stamp: null), Is.True);
	}

	[Test]
	public void TheStampRoundTripsThroughTheLibrary()
	{
		var stamp = CreateStamp();

		GameplaySourceFreshness.WriteStamp(_projectRoot, stamp);
		var read = GameplaySourceFreshness.TryReadStamp(_projectRoot);
		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);

		Assert.That(read, Is.Not.Null);
		Assert.That(GameplaySourceFreshness.IsStale(source, _assemblyPath, read), Is.False);
	}

	private GameplayBuildStamp CreateStamp()
	{
		var source = GameplaySourceFreshness.Scan(_gameplayProjectPath, _projectRoot);
		return GameplaySourceFreshness.CreateStamp(source, _assemblyPath);
	}

	private static void WriteFile(string path, DateTime writeTimeUtc)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, string.Empty);
		File.SetLastWriteTimeUtc(path, writeTimeUtc);
	}
}
