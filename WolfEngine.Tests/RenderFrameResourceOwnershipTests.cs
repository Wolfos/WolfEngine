using WolfEngine.Rendering;

namespace WolfEngine.Tests;

public sealed class RenderFrameResourceOwnershipTests
{
	[Test]
	public void PerViewResources_DoNotOwnFrameSharedTargets()
	{
		var properties = typeof(RenderViewResources)
			.GetProperties()
			.Select(property => property.Name)
			.ToHashSet(StringComparer.Ordinal);

		Assert.Multiple(() =>
		{
			Assert.That(properties, Does.Not.Contain(nameof(RenderFrameSharedResources.FinalColor)));
			Assert.That(properties, Does.Not.Contain(nameof(RenderFrameSharedResources.SkyboxEnvironment)));
			Assert.That(properties, Does.Not.Contain(nameof(RenderFrameSharedResources.SkyboxIrradiance)));
			Assert.That(properties, Does.Not.Contain(nameof(RenderFrameSharedResources.SkyboxPrefilter)));
			Assert.That(properties, Does.Not.Contain(nameof(RenderFrameSharedResources.SkyboxBrdfLut)));
		});
	}

	[Test]
	public void SharedResources_ContainOnlyProcessWideFrameState()
	{
		var properties = typeof(RenderFrameSharedResources)
			.GetProperties()
			.Select(property => property.Name)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.That(properties, Is.EqualTo(new[]
		{
			nameof(RenderFrameSharedResources.FinalColor),
			nameof(RenderFrameSharedResources.FramebufferSize),
			nameof(RenderFrameSharedResources.SkyboxBrdfLut),
			nameof(RenderFrameSharedResources.SkyboxEnvironment),
			nameof(RenderFrameSharedResources.SkyboxIrradiance),
			nameof(RenderFrameSharedResources.SkyboxPrefilter)
		}.Order(StringComparer.Ordinal)));
	}
}
