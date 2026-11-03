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
			Assert.That(properties, Does.Not.Contain(nameof(RenderFrameSharedResources.SkyboxBrdfLut)));
		});
	}

	[Test]
	public void PerViewResources_OwnTheViewsSky()
	{
		var properties = typeof(RenderViewResources)
			.GetProperties()
			.Select(property => property.Name)
			.ToHashSet(StringComparer.Ordinal);

		// Each view is lit by its own sun, so a preview world must not draw the scene view's sky.
		Assert.That(properties, Is.SupersetOf(new[]
		{
			nameof(RenderViewResources.SkyboxEnvironment),
			nameof(RenderViewResources.SkyboxIrradiance),
			nameof(RenderViewResources.SkyboxPrefilter)
		}));
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
			nameof(RenderFrameSharedResources.SkyboxBrdfLut)
		}.Order(StringComparer.Ordinal)));
	}
}
