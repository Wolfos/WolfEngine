using Microsoft.CodeAnalysis;

namespace WolfEngine.SourceGenerators.Serialization;

internal sealed class WellKnownSymbols
{
	private WellKnownSymbols(Compilation compilation, INamedTypeSymbol entityComponent, INamedTypeSymbol registry)
	{
		EntityComponent = entityComponent;
		Registry = registry;
		DataAsset = compilation.GetTypeByMetadataName("WolfEngine.AssetPipeline.IDataAsset");
		Entity = compilation.GetTypeByMetadataName("WolfEngine.ECS.Entity");
		NotSerializedAttribute = compilation.GetTypeByMetadataName("WolfEngine.ECS.NotSerializedAttribute");
		JsonIgnoreAttribute = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
		JsonIncludeAttribute = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIncludeAttribute");
		JsonConstructorAttribute = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonConstructorAttribute");
		OnDeserializing = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.IJsonOnDeserializing");
		OnDeserialized = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.IJsonOnDeserialized");
	}

	public INamedTypeSymbol EntityComponent { get; }

	public INamedTypeSymbol Registry { get; }

	/// <summary>Null in assemblies that do not reference the main engine assembly, such as WolfEngine.ECS itself.</summary>
	public INamedTypeSymbol? DataAsset { get; }

	public INamedTypeSymbol? Entity { get; }

	public INamedTypeSymbol? NotSerializedAttribute { get; }

	public INamedTypeSymbol? JsonIgnoreAttribute { get; }

	public INamedTypeSymbol? JsonIncludeAttribute { get; }

	public INamedTypeSymbol? JsonConstructorAttribute { get; }

	public INamedTypeSymbol? OnDeserializing { get; }

	public INamedTypeSymbol? OnDeserialized { get; }

	/// <summary>Returns null when the compilation cannot use the binary format, so there is nothing to generate.</summary>
	public static WellKnownSymbols? TryCreate(Compilation compilation)
	{
		var entityComponent = compilation.GetTypeByMetadataName("WolfEngine.ECS.IEntityComponent");
		var registry = compilation.GetTypeByMetadataName("WolfEngine.Serialization.SerializedTypeRegistry");
		if (entityComponent is null || registry is null)
		{
			return null;
		}

		return new WellKnownSymbols(compilation, entityComponent, registry);
	}
}
