using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Importing;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Editor.Tooling;

/// <summary>
/// Registers editor-only source tooling. Keep non-runtime authoring dependencies here.
/// </summary>
public static class EditorToolingServiceCollectionExtensions
{
	public static IServiceCollection AddEditorToolingShaders(
		this IServiceCollection services,
		EngineShaderOptions shaderOptions)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(shaderOptions);
		services.AddSingleton(shaderOptions);
		services.AddSingleton<IShaderProvider, DevelopmentShaderProvider>();
		return services;
	}

	/// <summary>
	/// Registers editor-only 3D, font and compute shader source tooling and their authoring dependencies. Compute
	/// shader import needs <see cref="AddEditorToolingShaders"/> too, for the engine's public shader module.
	/// </summary>
	public static IServiceCollection AddEditorToolingImporter(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);
		services.AddSingleton<IThreeDFileImporter, ThreeDFileImporter>();
		services.AddSingleton<IFontCompiler, Fonts.FontCompiler>();
		services.AddSingleton<IComputeShaderCompiler, ComputeShaderCompiler>();
		return services;
	}
}
