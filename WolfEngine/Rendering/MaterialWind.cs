namespace WolfEngine;

/// <summary>How a material's vertices move in the scene's wind. See docs/foliage.md, Wind.</summary>
public enum WindMode
{
	/// <summary>The material does not move.</summary>
	None = 0,

	/// <summary>
	/// The whole object sways by height above its instance origin, and vertices away from the trunk axis flutter.
	/// Use it on a tree's bark as well as its leaves so the trunk moves with them.
	/// </summary>
	Tree = 1,

	/// <summary>Each plant bends from its base by height, with a little flutter.</summary>
	Grass = 2
}

/// <summary>A material's wind settings. Scales multiply the scene's <c>WindConfig</c>.</summary>
public record struct MaterialWind
{
	public MaterialWind()
	{
	}

	public WindMode Mode { get; set; } = WindMode.None;

	/// <summary>Multiplies how far the object bends.</summary>
	public float StrengthScale { get; set; } = 1.0f;

	/// <summary>Multiplies leaf and blade flutter.</summary>
	public float FlutterScale { get; set; } = 1.0f;
}
