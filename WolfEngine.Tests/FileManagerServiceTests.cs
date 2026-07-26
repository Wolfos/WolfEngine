using WolfEngine.Utility;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class FileManagerServiceTests
{
	[Test]
	public void FileManagerName_MatchesHostPlatform()
	{
		var expected =
			OperatingSystem.IsWindows() ? "Explorer" :
			OperatingSystem.IsMacOS() ? "Finder" :
			"File Manager";

		Assert.That(new FileManagerService().FileManagerName, Is.EqualTo(expected));
	}

	[Test]
	public void OpenFolder_RejectsEmptyAndMissingPaths()
	{
		var service = new FileManagerService();
		var missingFolder = Path.Combine(Path.GetTempPath(), $"WolfEngineMissing{Guid.NewGuid():N}");

		Assert.That(() => service.OpenFolder(string.Empty), Throws.TypeOf<ArgumentException>());
		Assert.That(() => service.OpenFolder(missingFolder), Throws.TypeOf<DirectoryNotFoundException>());
	}

	[Test]
	public void RevealPath_RejectsEmptyAndMissingPaths()
	{
		var service = new FileManagerService();
		var missingFile = Path.Combine(Path.GetTempPath(), $"WolfEngineMissing{Guid.NewGuid():N}.txt");

		Assert.That(() => service.RevealPath(string.Empty), Throws.TypeOf<ArgumentException>());
		Assert.That(() => service.RevealPath(missingFile), Throws.TypeOf<FileNotFoundException>());
	}
}
