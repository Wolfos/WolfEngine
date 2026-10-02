using System.Numerics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Input;
using WolfEngine.Mathematics;

namespace WolfEngine.UI.Tests;

public sealed class PointerTests
{
	private static readonly PointerInputContext Context = new(true, true, true, Vector2.Zero, new(1280, 720));
	private sealed class Harness : IDisposable
	{
		public readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();
		public readonly GameplayUiHost Host;
		public readonly GameplayUiSurface Surface;
		public readonly Probe Model = new();
		public readonly InputSystem Input = new(new PointerInputQueue());
		public Harness()
		{
			Host = new(Services);
			Surface = (GameplayUiSurface)Host.Create<Events>(new(), "Pointer.css", new Dictionary<string, object?> { [nameof(Events.Model)] = Model });
			Pump();
		}
		public void Pump(PointerInputContext? context = null) => Input.ProcessPointerInput(Host, context ?? Context);
		public void Move(float x, float y) { Input.SetAxis2D(InputActionBinding.MousePosition, new(x, y)); Pump(); }
		public void Down() { Input.SetButton(InputActionBinding.MouseButtonLeft, true); Pump(); }
		public void Up() { Input.SetButton(InputActionBinding.MouseButtonLeft, false); Pump(); }
		public void Render() => Surface.SetParameters(new Dictionary<string, object?> { [nameof(Events.Model)] = Model });
		public void Dispose() { Host.Dispose(); Services.Dispose(); }
	}
	public sealed class Probe
	{
		public readonly List<string> Calls = [];
		public bool Disabled, Stop, Removed, PointerNone, Overlap, Clip, KeyChanged, LayoutHover, RemoveOnUp, LabelAuto;
		public int Count, CallbackThread;
		public MouseEventArgs? Last;
		public TaskCompletionSource? Completion;
	}
	public sealed class Events : ComponentBase
	{
		[Parameter] public Probe Model { get; set; } = null!;
		private async Task Click(MouseEventArgs args)
		{
			Model.Calls.Add("click"); Model.Last = args;
			if (Model.Completion is { } completion) await completion.Task;
			Model.CallbackThread = Environment.CurrentManagedThreadId; Model.Count++;
		}
		protected override void BuildRenderTree(RenderTreeBuilder b)
		{
			b.OpenElement(0, "div"); b.AddAttribute(1, "style", "width: 300px; height: 150px;");
			b.AddAttribute(2, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Model.Calls.Add("parent")));
			if (!Model.Removed)
			{
				b.OpenElement(3, "button"); b.SetKey(Model.KeyChanged ? "replacement" : "button");
				b.AddAttribute(4, "class", Model.LayoutHover ? "target layout-change" : "target");
				b.AddAttribute(5, "disabled", Model.Disabled);
				b.AddAttribute(6, "style", "width: 100px; height: 50px;" + (Model.PointerNone ? "pointer-events: none;" : ""));
				b.AddAttribute(7, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, Click));
				b.AddEventStopPropagationAttribute(8, "onclick", Model.Stop);
				b.AddAttribute(9, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, () => Model.Calls.Add("down")));
				b.AddAttribute(10, "onmouseup", EventCallback.Factory.Create<MouseEventArgs>(this, () => { Model.Calls.Add("up"); if (Model.RemoveOnUp) Model.Removed = true; }));
				b.AddAttribute(11, "onmousemove", EventCallback.Factory.Create<MouseEventArgs>(this, () => Model.Calls.Add("move")));
				b.AddAttribute(12, "onmouseenter", EventCallback.Factory.Create<MouseEventArgs>(this, () => Model.Calls.Add("enter")));
				b.AddAttribute(13, "onmouseleave", EventCallback.Factory.Create<MouseEventArgs>(this, () => Model.Calls.Add("leave")));
				b.AddMarkupContent(14, Model.LabelAuto ? "<span style='pointer-events: auto; width: 80px; height: 40px'>Label</span>" : "<span style='width: 80px; height: 40px'>Label</span>");
				b.CloseElement();
			}
			b.CloseElement();
			if (Model.Overlap)
			{
				b.OpenElement(15, "button");
				b.AddAttribute(16, "style", "position: absolute; left: 0px; top: 0px; width: 100px; height: 50px;");
				b.AddAttribute(17, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Model.Calls.Add("top")));
				b.CloseElement();
			}
			if (Model.Clip)
			{
				b.OpenElement(18, "div"); b.AddAttribute(19, "style", "position: absolute; left: 400px; top: 0px; width: 50px; height: 50px; overflow: hidden;");
				b.OpenElement(20, "button"); b.AddAttribute(21, "style", "width: 100px; height: 50px; flex-shrink: 0;");
				b.AddAttribute(22, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Model.Calls.Add("clipped")));
				b.CloseElement(); b.CloseElement();
			}
			b.OpenElement(23, "div"); b.AddContent(24, Model.Count.ToString()); b.CloseElement();
		}
	}
	[Test]
	public void NestedLabelBubblesAndCallbackRendersWithoutInvalidation()
	{
		using var h = new Harness(); h.Move(10, 10); h.Down(); h.Up();
		Assert.That(h.Model.Calls, Does.Contain("click").And.Contain("parent"));
		Assert.That(h.Model.Count, Is.EqualTo(1));
		Assert.That(Text(h.Surface.Root!), Does.Contain("1"));
		Assert.That(h.Model.Last!.ClientX, Is.EqualTo(10));
	}
	[Test]
	public void StopPropagationAndInheritedHandler()
	{
		using var h = new Harness(); h.Model.Stop = true; h.Render(); h.Move(10, 10); h.Down(); h.Up();
		Assert.That(h.Model.Calls, Does.Contain("click").And.Not.Contain("parent"));
		h.Model.Calls.Clear(); h.Move(200, 100); h.Down(); h.Up();
		Assert.That(h.Model.Calls, Does.Contain("parent").And.Not.Contain("click"));
	}
	[Test]
	public void CaptureAllowsLeavingReturningAndDeliversUpOutside()
	{
		using var h = new Harness(); h.Move(10, 10); h.Down(); h.Move(1400, 900);
		Assert.That(h.Model.Calls, Does.Contain("leave"));
		h.Model.Calls.Clear(); h.Move(1401, 900);
		Assert.That(h.Model.Calls, Does.Contain("move"));
		h.Up(); Assert.That(h.Model.Calls, Does.Contain("up").And.Not.Contain("click"));
		h.Move(10, 10); h.Down(); h.Move(1400, 900); h.Move(10, 10); h.Up();
		Assert.That(h.Model.Count, Is.EqualTo(1));
	}
	[TestCase("disabled")]
	[TestCase("removed")]
	[TestCase("replacement")]
	[TestCase("focus")]
	[TestCase("paused")]
	[TestCase("disposed")]
	public void CapturesCancelWithoutClick(string reason)
	{
		using var h = new Harness(); h.Move(10, 10); h.Down();
		switch (reason)
		{
			case "disabled": h.Model.Disabled = true; h.Render(); break;
			case "removed": h.Model.Removed = true; h.Render(); h.Pump(); h.Model.Removed = false; h.Render(); break;
			case "replacement": h.Model.KeyChanged = true; h.Render(); break;
			case "focus": h.Pump(Context with { Focused = false }); break;
			case "paused": h.Pump(Context with { Enabled = false }); break;
			case "disposed": h.Surface.Dispose(); break;
		}
		h.Pump(); h.Up(); Assert.That(h.Model.Count, Is.Zero);
	}
	[Test]
	public void DisabledBlocksAndPointerNonePassesToAncestor()
	{
		using var h = new Harness(); h.Model.Disabled = true; h.Render(); h.Move(10, 10); h.Down(); h.Up();
		Assert.That(h.Model.Calls, Is.Empty);
		h.Model.Disabled = false; h.Model.PointerNone = true; h.Render(); h.Move(10, 10); h.Down(); h.Up();
		Assert.That(h.Model.Calls, Does.Contain("parent").And.Not.Contain("click"));
	}
	[Test]
	public void ReversePaintOrderAndNestedClipping()
	{
		using var h = new Harness(); h.Model.Overlap = h.Model.Clip = true; h.Render(); h.Move(10, 10); h.Down(); h.Up();
		Assert.That(h.Model.Calls, Does.Contain("top").And.Not.Contain("click"));
		h.Model.Calls.Clear(); h.Move(460, 10); h.Down(); h.Up(); Assert.That(h.Model.Calls, Is.Empty);
		h.Move(410, 10); h.Down(); h.Up(); Assert.That(h.Model.Calls, Does.Contain("clipped"));
	}
	[Test]
	public void HiDpiOffsetAndPaintStatesReuseLayoutAndStationaryFrames()
	{
		using var h = new Harness(); h.Host.SetViewportSize(new Int2(1280, 720), 2);
		var context = Context with { Origin = new(50, 80), Size = new(640, 360) }; h.Pump(context);
		h.Input.SetAxis2D(InputActionBinding.MousePosition, new(60, 90)); h.Pump(context);
		Assert.That(h.Surface.Performance.LayoutRan, Is.False);
		var revision = h.Surface.Performance.Revision;
		for (var i = 0; i < 10; i++) h.Pump(context);
		Assert.That(h.Surface.Performance.Revision, Is.EqualTo(revision));
		h.Input.SetButton(InputActionBinding.MouseButtonLeft, true); h.Pump(context);
		Assert.That(h.Surface.Performance.LayoutRan, Is.False);
		Assert.That(h.Surface.Root!.Children[0].Children[0].Active, Is.True);
		h.Input.SetButton(InputActionBinding.MouseButtonLeft, false); h.Pump(context);
		Assert.That(h.Model.Last!.ClientX, Is.EqualTo(10));
	}
	[Test]
	public void AsyncCompletionIsPumpedOnOwnerAndRendersAutomatically()
	{
		using var h = new Harness(); h.Model.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		h.Move(10, 10); h.Down(); h.Up(); Assert.That(h.Model.Count, Is.Zero);
		Task.Run(() => h.Model.Completion.SetResult()).GetAwaiter().GetResult();
		Assert.That(SpinWait.SpinUntil(() => { h.Pump(); return h.Model.Count == 1; }, 2000), Is.True);
		Assert.That(h.Model.CallbackThread, Is.EqualTo(Environment.CurrentManagedThreadId));
		Assert.That(Text(h.Surface.Root!), Does.Contain("1"));
	}
	[Test]
	public void OnlyTopScreenSurfaceReceivesInput()
	{
		using var h = new Harness(); var textureModel = new Probe(); var topModel = new Probe();
		using var texture = h.Host.Create<Events>(new() { Kind = UiSurfaceKind.Texture, Layer = 10 }, initialParameters: new Dictionary<string, object?> { [nameof(Events.Model)] = textureModel });
		using var top = h.Host.Create<Events>(new() { Layer = 1 }, initialParameters: new Dictionary<string, object?> { [nameof(Events.Model)] = topModel });
		h.Pump(); h.Move(10, 10); h.Down(); h.Up();
		Assert.That(topModel.Count, Is.EqualTo(1)); Assert.That(h.Model.Count, Is.Zero); Assert.That(textureModel.Count, Is.Zero);
	}
	private static string Text(UiNode node) => node.Text + string.Concat(node.Children.Select(Text));

	public sealed class KeyedList : ComponentBase
	{
		[Parameter] public string[] Items { get; set; } = [];
		[Parameter] public List<string> Clicks { get; set; } = null!;
		protected override void BuildRenderTree(RenderTreeBuilder b)
		{
			foreach (var item in Items)
			{
				b.OpenElement(0, "button"); b.SetKey(item);
				b.AddAttribute(1, "style", "width: 100px; height: 50px;");
				b.AddAttribute(2, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Clicks.Add(item)));
				b.CloseElement();
			}
		}
	}
	[Test]
	public void ExplicitKeyPreservesCaptureWhenElementsReorder()
	{
		using var services = new ServiceCollection().BuildServiceProvider(); using var host = new GameplayUiHost(services);
		var clicks = new List<string>(); var parameters = new Dictionary<string, object?> { [nameof(KeyedList.Items)] = new[] { "a", "b" }, [nameof(KeyedList.Clicks)] = clicks };
		using var surface = host.Create<KeyedList>(new(), initialParameters: parameters); var input = new InputSystem(new PointerInputQueue());
		input.SetAxis2D(InputActionBinding.MousePosition, new(10, 10)); input.SetButton(InputActionBinding.MouseButtonLeft, true); input.ProcessPointerInput(host, Context);
		parameters[nameof(KeyedList.Items)] = new[] { "b", "a" }; surface.SetParameters(parameters);
		input.SetAxis2D(InputActionBinding.MousePosition, new(10, 60)); input.SetButton(InputActionBinding.MouseButtonLeft, false); input.ProcessPointerInput(host, Context);
		Assert.That(clicks, Is.EqualTo(new[] { "a" }));
	}
	[Test]
	public void StationaryPointerPumpDoesNotAllocate()
	{
		using var h = new Harness(); h.Move(10, 10);
		for (var i = 0; i < 100; i++) { h.Input.SetAxis2D(InputActionBinding.MouseDelta, Vector2.Zero); h.Pump(); }
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 1000; i++) { h.Input.SetAxis2D(InputActionBinding.MouseDelta, Vector2.Zero); h.Pump(); }
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.Zero);
	}
	[Test]
	public void MouseUpRemovingTargetDoesNotDispatchStaleClickHandler()
	{
		using var h = new Harness(); h.Model.RemoveOnUp = true; h.Render(); h.Move(10, 10); h.Down(); h.Up();
		Assert.That(h.Model.Calls, Does.Contain("up").And.Not.Contain("click"));
		Assert.That(h.Model.Count, Is.Zero);
	}
	[Test]
	public void LayoutAffectingHoverDeclarationsInvalidateYoga()
	{
		using var services = new ServiceCollection().BuildServiceProvider(); using var host = new GameplayUiHost(services);
		using var surface = host.Create<LayoutHoverButton>(new(), "Pointer.css");
		host.BeginFrame(Context); host.Route(new(InputActionBinding.MousePosition, new(40, 10), false, new(40, 10), 0));
		Assert.That(surface.Performance.LayoutRan, Is.True);
		Assert.That(((GameplayUiSurface)surface).Root!.Children[0].Left, Is.EqualTo(20));
	}
	public sealed class LayoutHoverButton : ComponentBase
	{
		protected override void BuildRenderTree(RenderTreeBuilder b) => b.AddMarkupContent(0, "<button class='layout-change' style='width: 100px; height: 50px'></button>");
	}
	[Test]
	public void ChildCanRestorePointerEventsAndBubbleThroughNoneAncestor()
	{
		using var h = new Harness(); h.Model.PointerNone = h.Model.LabelAuto = true; h.Render(); h.Move(10, 10); h.Down(); h.Up();
		Assert.That(h.Model.Count, Is.EqualTo(1));
		Assert.That(h.Model.Calls, Does.Contain("parent"));
	}
	public sealed class CallbackParent : ComponentBase
	{
		private int _count;
		protected override void BuildRenderTree(RenderTreeBuilder b)
		{
			b.OpenComponent<CallbackChild>(0);
			b.AddAttribute(1, nameof(CallbackChild.Clicked), EventCallback.Factory.Create<MouseEventArgs>(this, () => _count++));
			b.CloseComponent();
			b.OpenElement(2, "div"); b.AddContent(3, _count.ToString()); b.CloseElement();
		}
	}
	public sealed class CallbackChild : ComponentBase
	{
		[Parameter] public EventCallback<MouseEventArgs> Clicked { get; set; }
		protected override void BuildRenderTree(RenderTreeBuilder b)
		{
			b.OpenElement(0, "button"); b.AddAttribute(1, "style", "width: 100px; height: 50px;");
			b.AddAttribute(2, "onclick", Clicked); b.AddMarkupContent(3, "<span>Child</span>"); b.CloseElement();
		}
	}
	[Test]
	public void ComponentEventCallbackRendersItsParentAutomatically()
	{
		using var services = new ServiceCollection().BuildServiceProvider(); using var host = new GameplayUiHost(services);
		using var surface = host.Create<CallbackParent>(new()); var input = new InputSystem(new PointerInputQueue());
		input.SetAxis2D(InputActionBinding.MousePosition, new(10, 10)); input.SetButton(InputActionBinding.MouseButtonLeft, true); input.ProcessPointerInput(host, Context);
		input.SetButton(InputActionBinding.MouseButtonLeft, false); input.ProcessPointerInput(host, Context);
		Assert.That(Text(((GameplayUiSurface)surface).Root!), Does.Contain("1"));
	}
	[Test]
	public void CompiledRazorClickHandlerSurvivesGameplayParameterUpdatesDuringPress()
	{
		using var services = new ServiceCollection().BuildServiceProvider(); using var host = new GameplayUiHost(services);
		using var surface = host.Create<RazorClickCounter>(new()); var input = new InputSystem(new PointerInputQueue());
		var root = ((GameplayUiSurface)surface).Root!;
		Assert.That(root.Children[0].Events.ContainsKey("onclick"), Is.True, "Razor must compile @onclick as a callback, not a literal attribute.");
		input.SetAxis2D(InputActionBinding.MousePosition, new(10, 10)); input.SetButton(InputActionBinding.MouseButtonLeft, true); input.ProcessPointerInput(host, Context);
		for (var frame = 1; frame <= 20; frame++)
		{
			surface.SetParameters(new Dictionary<string, object?> { [nameof(RazorClickCounter.Frame)] = frame });
			input.ProcessPointerInput(host, Context);
		}
		input.SetButton(InputActionBinding.MouseButtonLeft, false); input.ProcessPointerInput(host, Context);
		Assert.That(Text(((GameplayUiSurface)surface).Root!), Does.Contain("Clicks: 1"));
	}
}
