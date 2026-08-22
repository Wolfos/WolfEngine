using ImGuiNET;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ImGuiUiSystemThreadingTests
{
	private static readonly Int2 Size = new(1280, 720);

	[Test]
	public void MarkFontAtlasUploaded_DoesNotBlockWhileDrawCallbackIsRunning()
	{
		var ui = CreateWarmedUpSystem();
		var frame = ProduceFrameWithFontAtlas(ui);

		var drawEntered = new ManualResetEventSlim(false);
		var releaseDraw = new ManualResetEventSlim(false);
		var uiThread = new Thread(() =>
		{
			ui.NewFrame(1.0f / 60.0f, Size, Size);
			ui.RunGui(() =>
			{
				drawEntered.Set();
				releaseDraw.Wait();
			});
		})
		{
			IsBackground = true,
			Name = "ui-thread"
		};

		uiThread.Start();
		Assert.That(drawEntered.Wait(TimeSpan.FromSeconds(10)), Is.True, "UI thread never entered the draw callback.");

		try
		{
			// The render thread reaches this the moment it has uploaded the atlas. A draw callback that
			// blocks on the main thread (icon/thumbnail loading goes through MainThreadDispatcher.Invoke)
			// must not be able to stall it, or the two threads deadlock during editor startup.
			var upload = Task.Run(frame.MarkFontAtlasUploaded);
			Assert.That(
				upload.Wait(TimeSpan.FromSeconds(5)),
				Is.True,
				"MarkFontAtlasUploaded blocked while a draw callback was in flight.");
		}
		finally
		{
			releaseDraw.Set();
			uiThread.Join(TimeSpan.FromSeconds(10));
		}
	}

	[Test]
	public void FontAtlas_IsRepublishedUntilUploadIsAcknowledged()
	{
		var ui = CreateWarmedUpSystem();

		var first = ProduceFrameWithFontAtlas(ui);
		var second = ProduceFrameWithFontAtlas(ui);
		Assert.That(second.HasFontAtlas, Is.True, "Atlas should stay pending until the renderer acknowledges it.");

		second.MarkFontAtlasUploaded();

		var third = ProduceFrame(ui);
		Assert.That(third.HasFontAtlas, Is.False, "Atlas should stop being published once uploaded.");
		Assert.That(first.FontAtlas, Is.SameAs(third.FontAtlas));
	}

	/// <summary>
	/// ImGui hides a freshly created auto-sized window for one frame, so the first cycle produces no
	/// draw data at all. Burn it here so the tests can assert on real frames.
	/// </summary>
	private static ImGuiUiSystem CreateWarmedUpSystem()
	{
		var ui = new ImGuiUiSystem();
		RunFrame(ui);
		ui.TryConsumeLatest(out _);
		return ui;
	}

	private static UiFrameData ProduceFrameWithFontAtlas(ImGuiUiSystem ui)
	{
		var frame = ProduceFrame(ui);
		Assert.That(frame.HasFontAtlas, Is.True, "Expected the frame to carry the pending font atlas.");
		return frame;
	}

	private static UiFrameData ProduceFrame(ImGuiUiSystem ui)
	{
		RunFrame(ui);
		Assert.That(ui.TryConsumeLatest(out var frame), Is.True, "Expected a published UI frame.");
		return frame;
	}

	private static void RunFrame(ImGuiUiSystem ui)
	{
		ui.NewFrame(1.0f / 60.0f, Size, Size);
		ui.RunGui(() =>
		{
			ImGui.Begin("threading-test");
			ImGui.Text("wolf");
			ImGui.End();
		});
	}
}
