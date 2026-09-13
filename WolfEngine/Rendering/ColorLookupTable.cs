using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;

namespace WolfEngine;

/// <summary>
/// A 3D colour grading lookup table, imported from a .cube file. Input is display-encoded sRGB remapped from
/// <see cref="DomainMin"/>..<see cref="DomainMax"/> onto the lattice; output is display-encoded sRGB.
/// </summary>
[RuntimeAsset(AssetType.ColorLookupTable, typeof(ColorLookupTableAssetSummary), typeof(IColorLookupTableRuntimeResolver))]
public sealed class ColorLookupTable
{
	public ColorLookupTable(string name, int size, Vector3 domainMin, Vector3 domainMax, Texture texture)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(texture);
		if (texture.Dimension != TextureDimension.Texture3D ||
		    texture.Width != size || texture.Height != size || texture.Depth != size)
		{
			throw new ArgumentException($"Lookup table texture must be a {size}^3 volume.", nameof(texture));
		}

		Name = name;
		Size = size;
		DomainMin = domainMin;
		DomainMax = domainMax;
		Texture = texture;
	}

	public string Name { get; }

	/// <summary>Lattice entries per axis.</summary>
	public int Size { get; }

	public Vector3 DomainMin { get; }

	public Vector3 DomainMax { get; }

	/// <summary>RGBA16F volume in red-fastest (x = red) order.</summary>
	public Texture Texture { get; }

	/// <summary>
	/// Builds the runtime table from a cooked artifact. The texture goes through the factory so it is cached by
	/// <paramref name="name"/> (a reimport updates the existing texture in place) and scheduled for GPU upload.
	/// </summary>
	public static ColorLookupTable Create(string name, ColorLookupTableArtifact artifact, ITextureFactory textureFactory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(artifact);
		ArgumentNullException.ThrowIfNull(textureFactory);

		var size = artifact.Size;
		var texture = textureFactory.GetTexture(Texture.Create3D(
			name,
			size,
			size,
			size,
			TextureFormat.Rgba16Float,
			[new TextureMipData(size, size, artifact.Rgba16FloatTexels, size)]));
		return new ColorLookupTable(name, size, artifact.DomainMin, artifact.DomainMax, texture);
	}
}
