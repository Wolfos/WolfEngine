using WolfEngine.Editor.Automation;

namespace WolfEngine.Editor.Tests;

public sealed class EditorAutomationOptionsTests
{
	[Test]
	public void TryParse_ValidCaptureCommand_UsesDefaultsAndAcceptsQuit()
	{
		var parsed = EditorAutomationOptions.TryParse(
			["--scene", "Assets/Scenes/Test/Test.scene.json", "--frames", "10", "--capture", "Artifacts/test.png", "--quit"],
			out var options,
			out var error);

		Assert.That(parsed, Is.True, error);
		Assert.That(options, Is.Not.Null);
		Assert.That(options!.Frames, Is.EqualTo(10));
		Assert.That(options.Resolution.X, Is.EqualTo(1280));
		Assert.That(options.Resolution.Y, Is.EqualTo(720));
	}

	[TestCase("--scene", "Assets/Test.scene.json", "--frames", "0", "--capture", "capture.png")]
	[TestCase("--scene", "Assets/Test.scene.json", "--frames", "1", "--capture", "capture.png", "--width", "0")]
	[TestCase("--scene", "Assets/Test.scene.json", "--frames", "1")]
	public void TryParse_InvalidCaptureCommand_ReturnsError(params string[] arguments)
	{
		var parsed = EditorAutomationOptions.TryParse(arguments, out var options, out var error);

		Assert.That(parsed, Is.False);
		Assert.That(options, Is.Null);
		Assert.That(error, Is.Not.Empty);
	}
}
