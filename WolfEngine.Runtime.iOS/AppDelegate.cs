namespace WolfEngine.Runtime.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		return true;
	}

	public override UISceneConfiguration GetConfiguration(
		UIApplication application,
		UISceneSession connectingSceneSession,
		UISceneConnectionOptions options)
	{
		return new UISceneConfiguration("Default Configuration", connectingSceneSession.Role);
	}
}
