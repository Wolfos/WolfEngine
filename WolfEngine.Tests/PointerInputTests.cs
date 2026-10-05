using System.Numerics;
using WolfEngine.Input;

namespace WolfEngine.Tests;

public sealed class PointerInputTests
{
	private sealed class Router : IPointerInputRouter
	{
		public bool Consume;
		public readonly List<PointerInputEvent> Events = [];
		public readonly List<PointerInputContext> Contexts = [];
		public void BeginFrame(PointerInputContext context) => Contexts.Add(context);
		public bool Route(PointerInputEvent input) { Events.Add(input); return Consume; }
		public void EndFrame() { }
	}
	private static readonly PointerInputContext Context = new(true, true, true, Vector2.Zero, new(100, 100));

	[Test]
	public void RepeatedMouseScrollEventsReachGameplay()
	{
		var input = new InputSystem(new PointerInputQueue());
		var router = new Router();
		var scrollEvents = new List<Vector2>();

		input.RegisterAxis2D(
			new InputAction("Scroll", InputActionType.Axis2D, [InputActionBinding.MouseScroll]),
			callback => scrollEvents.Add(callback.Value));

		input.SetAxis2D(InputActionBinding.MouseScroll, new Vector2(0, 1));
		input.ProcessPointerInput(router, Context);

		input.SetAxis2D(InputActionBinding.MouseScroll, new Vector2(0, 1));
		input.ProcessPointerInput(router, Context);

		Assert.That(scrollEvents, Is.EqualTo(new[] { new Vector2(0, 1), new Vector2(0, 1) }));
	}

	[Test]
	public void CoalescesNativeMotionButNeverAcrossButtonTransitions()
	{
		var input = new InputSystem(new PointerInputQueue()); var router = new Router();
		input.SetAxis2D(InputActionBinding.MousePosition, new(1, 1)); input.SetAxis2D(InputActionBinding.MouseDelta, new(1, 1));
		input.SetAxis2D(InputActionBinding.MousePosition, new(2, 3)); input.SetAxis2D(InputActionBinding.MouseDelta, new(1, 2));
		input.SetButton(InputActionBinding.MouseButtonLeft, true);
		input.SetAxis2D(InputActionBinding.MousePosition, new(4, 5)); input.SetButton(InputActionBinding.MouseButtonLeft, false);
		Assert.That(router.Events, Is.Empty);
		input.ProcessPointerInput(router, Context);
		Assert.That(router.Events.Select(e => e.Binding), Is.EqualTo(new[] { InputActionBinding.MousePosition, InputActionBinding.MouseDelta, InputActionBinding.MouseButtonLeft, InputActionBinding.MousePosition, InputActionBinding.MouseButtonLeft }));
		Assert.That(router.Events[0].Value, Is.EqualTo(new Vector2(2, 3)));
		Assert.That(router.Events[1].Value, Is.EqualTo(new Vector2(2, 3)));
		Assert.That(router.Events[2].Position, Is.EqualTo(new Vector2(2, 3)));
	}
	[TestCase(true)]
	[TestCase(false)]
	public void ReleaseAlwaysFollowsOriginalPressOwner(bool uiOwned)
	{
		var input = new InputSystem(new PointerInputQueue()); var router = new Router { Consume = uiOwned }; var gameplay = new List<bool>();
		input.RegisterButton(new("fire", InputActionType.Button, [InputActionBinding.MouseButtonLeft]), e => gameplay.Add(e.Value));
		input.SetButton(InputActionBinding.MouseButtonLeft, true); input.ProcessPointerInput(router, Context);
		router.Consume = !uiOwned;
		input.SetButton(InputActionBinding.MouseButtonLeft, false); input.ProcessPointerInput(router, Context);
		Assert.That(gameplay, Is.EqualTo(uiOwned ? [] : new[] { true, false }));
	}
	[Test]
	public void FocusLossCancelsAndReleasesEvenWhenRegainedBeforePump()
	{
		var input = new InputSystem(new PointerInputQueue()); var router = new Router(); var gameplay = new List<bool>();
		input.RegisterButton(new("fire", InputActionType.Button, [InputActionBinding.MouseButtonLeft]), e => gameplay.Add(e.Value));
		input.SetButton(InputActionBinding.MouseButtonLeft, true); input.ProcessPointerInput(router, Context);
		input.SetPointerFocus(false); input.SetPointerFocus(true); input.ProcessPointerInput(router, Context);
		Assert.That(router.Contexts.Any(c => !c.Focused), Is.True);
		Assert.That(gameplay, Is.EqualTo(new[] { true, false }));
		input.SetButton(InputActionBinding.MouseButtonLeft, true); input.ProcessPointerInput(router, Context);
		Assert.That(gameplay, Is.EqualTo(new[] { true, false, true }));
	}
	[Test]
	public void ModifiersAreSnapshottedWithTheMouseEvent()
	{
		var input = new InputSystem(new PointerInputQueue()); var router = new Router();
		input.SetButton(InputActionBinding.KeyLeftShift, true); input.SetButton(InputActionBinding.MouseButtonLeft, true);
		input.SetButton(InputActionBinding.KeyLeftShift, false); input.SetButton(InputActionBinding.MouseButtonLeft, false);
		input.ProcessPointerInput(router, Context);
		Assert.That(router.Events[0].ShiftKey, Is.True); Assert.That(router.Events[1].ShiftKey, Is.False);
	}

	[Test]
	public void MouseViewportPosition_TracksConsumedMotionAndRemapsWhenViewportMoves()
	{
		var input = new InputSystem(new PointerInputQueue());
		var router = new Router { Consume = true };
		input.SetAxis2D(InputActionBinding.MousePosition, new Vector2(150.0f, 60.0f));
		input.ProcessPointerInput(router, new PointerInputContext(true, false, true,
			new Vector2(50.0f, 20.0f), new Vector2(100.0f, 40.0f)));

		Assert.That(input.TryGetMouseViewportPosition(out var first), Is.True);
		Assert.That(first, Is.EqualTo(Vector2.One));
		Assert.That(router.Events, Has.Count.EqualTo(1));

		// No pointer event: only the viewport rectangle moved and resized.
		input.ProcessPointerInput(router, new PointerInputContext(true, false, true,
			new Vector2(100.0f, 40.0f), new Vector2(100.0f, 40.0f)));

		Assert.That(input.TryGetMouseViewportPosition(out var remapped), Is.True);
		Assert.That(remapped, Is.EqualTo(new Vector2(0.5f, 0.5f)));
	}

	[Test]
	public void MouseViewportPosition_RejectsUnknownInvalidDisabledAndUnfocusedState()
	{
		var input = new InputSystem(new PointerInputQueue());
		var router = new Router();
		input.ProcessPointerInput(router, Context);
		Assert.That(input.TryGetMouseViewportPosition(out _), Is.False);
		Assert.That(float.IsNaN(input.GetMouseViewportPosition().X), Is.True);

		input.SetAxis2D(InputActionBinding.MousePosition, new Vector2(150.0f, 60.0f));
		input.ProcessPointerInput(router, new PointerInputContext(true, false, true,
			new Vector2(50.0f, 20.0f), new Vector2(100.0f, 40.0f)));
		Assert.That(input.TryGetMouseViewportPosition(out var extrapolated), Is.True);
		Assert.That(extrapolated, Is.EqualTo(Vector2.One));

		input.ProcessPointerInput(router, new PointerInputContext(false, true, true,
			Vector2.Zero, new Vector2(100.0f, 100.0f)));
		Assert.That(input.TryGetMouseViewportPosition(out _), Is.False);

		input.ProcessPointerInput(router, new PointerInputContext(true, true, true,
			Vector2.Zero, new Vector2(0.0f, 100.0f)));
		Assert.That(input.TryGetMouseViewportPosition(out _), Is.False);

		input.ProcessPointerInput(router, new PointerInputContext(true, true, true,
			Vector2.Zero, new Vector2(100.0f, 100.0f)));
		Assert.That(input.TryGetMouseViewportPosition(out _), Is.True);
		input.SetPointerFocus(false);
		Assert.That(input.TryGetMouseViewportPosition(out _), Is.False);
	}
}
