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
}
