namespace WolfEngine.Runtime.iOS;

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
	private IosGameHost? _game;

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

		_game = IosGameHost.Create(view, IosGameHost.GetBundledManifest());
	}

	[Export("sceneDidBecomeActive:")]
	public void DidBecomeActive(UIScene scene)
	{
		_game?.Start();
	}

	// iOS terminates apps that submit GPU work while backgrounded, so rendering stops before that.
	[Export("sceneWillResignActive:")]
	public void WillResignActive(UIScene scene)
	{
		_game?.Stop();
	}

	[Export("sceneDidDisconnect:")]
	public void DidDisconnect(UIScene scene)
	{
		_game?.Dispose();
		_game = null;
	}
}
