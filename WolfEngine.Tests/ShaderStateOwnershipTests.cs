using System.Reflection;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Features;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

/// <summary>
/// A pass keeps compiled shader state only inside a compute kernel, graphics shader or shader variant set. Those
/// rebuild themselves after a shader reload; state a pass caches itself would outlive the reload, still holding the
/// old bytecode or a pipeline the reload disposed.
/// </summary>
[TestFixture]
public sealed class ShaderStateOwnershipTests
{
	// Shader state that goes stale on a reload.
	private static readonly Type[] ShaderStateTypes =
	[
		typeof(IGfxPipeline),
		typeof(ShaderPropertyWriter),
		typeof(ShaderReflectionLayout),
		typeof(CompiledComputeShaderWithReflection),
		typeof(CompiledGraphicsShaderWithReflection),
		typeof(ComputeThreadGroupSize),
		typeof(IShaderProvider)
	];

	// Types that hold shader state but are not passes, named because their names do not end in "Pass".
	private static readonly string[] OtherHolderTypeNames =
	[
		"WolfEngine.Rendering.RayTracingScene",
		"WolfEngine.Rendering.RayTracingSceneResources",
		"WolfEngine.Rendering.GpuDrawResources",
		"WolfEngine.WolfRendererMetal",
		"WolfEngine.WolfRendererD3D",
		"WolfEngine.Rendering.RenderGraph",
		"WolfEngine.Rendering.RenderGraphFrameBuilder",
		"WolfEngine.Rendering.RenderGraphPassSet",
		"WolfEngine.Rendering.Splash.SplashScreenRenderer",
		"WolfEngine.Rendering.UI.MetalUiRenderer",
		"WolfEngine.Rendering.UI.D3D12UiRenderer",
		"WolfEngine.Rendering.UI.GameplayUiGpuRenderer",
		"WolfEngine.Rendering.Features.RenderFeatureHost"
	];

	// Holders that keep this state for something other than engine shaders.
	private static readonly Type[] ExemptHolders =
	[
		// A game compute shader's writers, keyed by the reflected layout object, so a reimport gets new ones. Engine
		// shader reloads do not change a game shader's reflection.
		typeof(RenderFeatureComputePass)
	];

	// Fields whose type matches but which hold no shader state.
	private static readonly string[] ExemptFields =
	[
		// Texture data uploaded by a feature, not bytecode.
		"WolfEngine.Rendering.Features.RenderFeatureHost+UploadPass.<Texels>k__BackingField"
	];

	[Test]
	public void PassesAndShaderHolders_KeepShaderStateOnlyInsideTheShaderModule()
	{
		var assembly = typeof(ComputeKernel).Assembly;
		var holders = assembly.GetTypes()
			.Where(type => type.IsClass &&
			               type.Namespace is { } ns &&
			               ns.StartsWith("WolfEngine.Rendering", StringComparison.Ordinal) &&
			               type.Name.EndsWith("Pass", StringComparison.Ordinal) &&
			               type.Name != "RenderGraphPass")
			.ToList();
		foreach (var name in OtherHolderTypeNames)
		{
			var type = assembly.GetType(name);
			Assert.That(type, Is.Not.Null, $"Holder type '{name}' no longer exists; update the list.");
			holders.Add(type!);
		}

		Assert.That(holders, Has.Count.GreaterThan(40), "The pass scan found too few passes to be meaningful.");

		var violations = new List<string>();
		foreach (var holder in holders)
		{
			if (ExemptHolders.Contains(holder))
			{
				continue;
			}

			var fields = holder.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
			foreach (var field in fields)
			{
				var fieldName = $"{holder.FullName}.{field.Name}";
				if (ExemptFields.Contains(fieldName))
				{
					continue;
				}

				if (HoldsShaderState(field.FieldType))
				{
					violations.Add($"{fieldName} : {field.FieldType.Name}");
				}
			}
		}

		Assert.That(
			violations,
			Is.Empty,
			"Keep compiled shader state in a ComputeKernel, GraphicsShader or ShaderVariantSet so a shader reload rebuilds it.");
	}

	private static bool HoldsShaderState(Type type)
	{
		if (Nullable.GetUnderlyingType(type) is { } underlying)
		{
			return HoldsShaderState(underlying);
		}

		if (type.IsArray)
		{
			return HoldsShaderState(type.GetElementType()!);
		}

		if (ShaderStateTypes.Any(stateType => stateType.IsAssignableFrom(type)))
		{
			return true;
		}

		if (type.IsGenericType == false)
		{
			return false;
		}

		var definition = type.GetGenericTypeDefinition();
		if (definition == typeof(ShaderVariantSet<,>))
		{
			// The set owns its variants' lifetime: a reload drops them all.
			return false;
		}

		if (definition == typeof(ReadOnlyMemory<>) || definition == typeof(Memory<>))
		{
			// Cached shader bytecode.
			return type.GetGenericArguments()[0] == typeof(byte);
		}

		return type.GetGenericArguments().Any(HoldsShaderState);
	}
}
