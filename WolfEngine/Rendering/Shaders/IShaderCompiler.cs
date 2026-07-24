#nullable enable

namespace WolfEngine;

/// <summary>
/// Compatibility contract for renderer consumers of precompiled shader artifacts.
/// Implementations that compile source live belong to editor tooling.
/// </summary>
public interface IShaderCompiler : Rendering.Shaders.IShaderProvider;
