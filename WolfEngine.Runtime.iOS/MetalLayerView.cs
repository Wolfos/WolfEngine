using CoreAnimation;
using ObjCRuntime;

namespace WolfEngine.Runtime.iOS;

/// <summary>
/// A view backed by a <see cref="CAMetalLayer"/> whose drawable matches the view's size in pixels.
/// </summary>
public sealed class MetalLayerView : UIView
{
	public MetalLayerView(CGRect frame)
		: base(frame)
	{
		ContentScaleFactor = UIScreen.MainScreen.NativeScale;
		MultipleTouchEnabled = true;
	}

	[Export("layerClass")]
	public static Class LayerClass()
	{
		return new Class(typeof(CAMetalLayer));
	}

	public CAMetalLayer MetalLayer => (CAMetalLayer)Layer;

	public event Action<CGSize>? DrawableSizeChanged;

	public override void LayoutSubviews()
	{
		base.LayoutSubviews();

		var scale = ContentScaleFactor;
		var drawableSize = new CGSize(
			Math.Max(1, Math.Round(Bounds.Width * scale)),
			Math.Max(1, Math.Round(Bounds.Height * scale)));
		if (MetalLayer.DrawableSize == drawableSize)
		{
			return;
		}

		MetalLayer.DrawableSize = drawableSize;
		DrawableSizeChanged?.Invoke(drawableSize);
	}
}
