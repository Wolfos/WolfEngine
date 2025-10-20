#nullable enable

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public static class GBufferPass
{
	public static PassTargets CreatePassTargets(GBufferPassConfig config)
	{
		if (config is null)
		{
			throw new ArgumentNullException(nameof(config));
		}

        var colorBindings = new[]
        {
            new ColorTargetBinding(config.AlbedoTarget),
            new ColorTargetBinding(config.NormalTarget),
            new ColorTargetBinding(config.MaterialTarget)
        };

        var depthBinding = new DepthTargetBinding(config.DepthTarget);

        return new PassTargets(colorBindings, depthBinding);
	}

    public static Viewport CreateViewport(GBufferPassConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        return new Viewport(0.0f, 0.0f, config.FramebufferWidth, config.FramebufferHeight);
    }

	public static void Record(IGfxCommandList commandList, GBufferPassConfig config, Action drawCommands)
	{
		if (commandList is null)
		{
			throw new ArgumentNullException(nameof(commandList));
		}

		if (config is null)
		{
			throw new ArgumentNullException(nameof(config));
		}

		var targets = CreatePassTargets(config);
		var viewport = CreateViewport(config);
		commandList.BeginPass(targets, viewport);
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));
		commandList.ClearColorAttachment(0, config.AlbedoClearColor);
		commandList.ClearColorAttachment(1, config.NormalClearColor);
		commandList.ClearColorAttachment(2, config.MaterialClearColor);
		commandList.ClearDepthStencil(config.DepthClearValue);

		drawCommands?.Invoke();

		commandList.EndPass();
	}
}
