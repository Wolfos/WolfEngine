using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Backend.Metal;

/// <summary>
/// A <c>CAMetalLayer</c> owned by the host application, such as the layer of an iOS view. The host keeps the layer
/// alive for as long as the renderer runs and reports its size whenever the view lays out.
/// </summary>
public sealed class HostMetalSurface : IMetalSurface
{
	private readonly object _sync = new();
	private readonly IntPtr _layerHandle;
	private Int2 _drawableSize;
	private Int2 _windowSize;
	private bool _hasSize;
	private bool _sizeChanged;

	/// <param name="layerHandle">The Objective-C pointer of the <c>CAMetalLayer</c> to render into.</param>
	public HostMetalSurface(IntPtr layerHandle)
	{
		if (layerHandle == IntPtr.Zero)
		{
			throw new ArgumentException("The host must supply a CAMetalLayer.", nameof(layerHandle));
		}

		_layerHandle = layerHandle;
	}

	/// <summary>The host's display presents every frame in step with the display, so vsync cannot be disabled.</summary>
	bool IMetalSurface.CanDisableVSync => false;

	/// <summary>
	/// Reports the view's current size. Call it whenever the view lays out, from any thread; the renderer resizes its
	/// drawables before the next frame.
	/// </summary>
	/// <param name="drawableSize">The size in pixels: the view's bounds times its content scale.</param>
	/// <param name="windowSize">The size in points.</param>
	public void SetSize(Int2 drawableSize, Int2 windowSize)
	{
		if (drawableSize.X <= 0 || drawableSize.Y <= 0)
		{
			return;
		}

		lock (_sync)
		{
			_sizeChanged |= _hasSize == false || drawableSize != _drawableSize || windowSize != _windowSize;
			_drawableSize = drawableSize;
			_windowSize = windowSize;
			_hasSize = true;
		}
	}

	IntPtr IMetalSurface.Initialize(Int2 initialSize, bool exactDrawableSize)
	{
		return _layerHandle;
	}

	bool IMetalSurface.TryGetDrawableSize(out Int2 size)
	{
		lock (_sync)
		{
			size = _drawableSize;
			return _hasSize;
		}
	}

	bool IMetalSurface.TryGetWindowSize(out Int2 size)
	{
		lock (_sync)
		{
			size = _windowSize;
			return _hasSize;
		}
	}

	MetalSurfaceEvents IMetalSurface.PumpEvents()
	{
		lock (_sync)
		{
			if (_sizeChanged == false)
			{
				return MetalSurfaceEvents.None;
			}

			_sizeChanged = false;
			return MetalSurfaceEvents.Resized;
		}
	}

	void IMetalSurface.Shutdown()
	{
	}
}

public static class HostMetalSurfaceServiceCollectionExtensions
{
	/// <summary>Makes the Metal renderer present into a layer the host application owns, instead of opening a window.</summary>
	public static IServiceCollection AddHostMetalSurface(this IServiceCollection services, HostMetalSurface surface)
	{
		ArgumentNullException.ThrowIfNull(surface);

		return services.AddSingleton<IMetalSurface>(surface);
	}
}
