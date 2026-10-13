using System.Reflection;
using System.Runtime.Loader;
using WolfEngine.Gameplay;
using WolfEngine.Serialization;

namespace WolfEngine.Runtime.Desktop;

/// <summary>
/// Loads the gameplay assembly the build stored in the packs. Loading code at runtime needs a JIT, so only desktop
/// hosts can do this; NativeAOT hosts compile the gameplay assembly in instead.
/// </summary>
public sealed class GameplayAssemblyLoader
{
	public (Assembly Assembly, IGameplayModule Module) Load(byte[] bytes, byte[]? symbols = null)
	{
		using var stream = new MemoryStream(bytes, false);
		using var symbolStream = symbols is null ? null : new MemoryStream(symbols, false);
		var assembly = symbolStream is null
			? AssemblyLoadContext.Default.LoadFromStream(stream)
			: AssemblyLoadContext.Default.LoadFromStream(stream, symbolStream);

		// Scenes are loaded before any gameplay code runs, so register the gameplay serializers now.
		SerializedTypeRegistry.EnsureRegistered(assembly);

		var method = assembly.GetTypes()
			             .Select(type => type.GetMethod("CreateModule", BindingFlags.Public | BindingFlags.Static))
			             .SingleOrDefault(candidate => candidate is not null)
		             ?? throw new InvalidDataException("Gameplay assembly has no unique public CreateModule entrypoint.");
		var module = (IGameplayModule?)method.Invoke(null, null)
		             ?? throw new InvalidDataException("Gameplay CreateModule returned null.");

		return (assembly, module);
	}
}
