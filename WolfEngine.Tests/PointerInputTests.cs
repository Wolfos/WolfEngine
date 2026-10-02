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
}
