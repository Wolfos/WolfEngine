namespace WolfEngine.Runtime.iOS;

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
	private SpikeRenderer? _renderer;

	[Export("window")]
	public UIWindow? Window { get; set; }

	[Export("scene:willConnectToSession:options:")]
	public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
	{
		if (scene is not UIWindowScene windowScene)
		{
			return;
		}

		Window ??= new UIWindow(windowScene);

		var view = new MetalLayerView(Window.Bounds)
		{
			AutoresizingMask = UIViewAutoresizing.FlexibleDimensions
		};
		var controller = new UIViewController
		{
			View = view
		};
		Window.RootViewController = controller;
		Window.MakeKeyAndVisible();

		var probesPassed = EngineProbes.RunAll();
		_renderer = new SpikeRenderer(view, probesPassed);
	}

	[Export("sceneDidBecomeActive:")]
	public void DidBecomeActive(UIScene scene)
	{
		_renderer?.Start();
	}

	// iOS terminates apps that submit GPU work while backgrounded, so rendering stops before that.
	[Export("sceneWillResignActive:")]
	public void WillResignActive(UIScene scene)
	{
		_renderer?.Stop();
	}

	[Export("sceneDidDisconnect:")]
	public void DidDisconnect(UIScene scene)
	{
		_renderer?.Dispose();
		_renderer = null;
	}
}
