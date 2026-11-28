using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// The files a <c>.gltf</c> reads besides itself: the buffers and images its JSON names by relative URI. Changing only a
/// buffer, such as rewritten indices in a <c>.bin</c>, leaves the <c>.gltf</c> byte for byte the same, so its staleness
/// has to be judged on those files too.
/// </summary>
internal static class GltfSourceInputs
{
	public static bool HasExternalInputs(string absoluteSourcePath)
	{
		return string.Equals(Path.GetExtension(absoluteSourcePath), ".gltf", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Hashes the <c>.gltf</c> together with each file it references, in a stable order.</summary>
	public static string ComputeInputsHash(string absoluteSourcePath)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var json = File.ReadAllBytes(absoluteSourcePath);
		hash.AppendData(json);

		var folder = Path.GetDirectoryName(Path.GetFullPath(absoluteSourcePath))!;
		foreach (var uri in ReadExternalUris(json).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
		{
			hash.AppendData(Encoding.UTF8.GetBytes(uri + "\n"));
			var path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(uri)));
			if (File.Exists(path))
			{
				hash.AppendData(File.ReadAllBytes(path));
			}
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private static IEnumerable<string> ReadExternalUris(byte[] json)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json);
		}
		catch (JsonException)
		{
			yield break;
		}

		using (document)
		{
			foreach (var arrayName in new[] { "buffers", "images" })
			{
				if (document.RootElement.TryGetProperty(arrayName, out var array) == false ||
				    array.ValueKind != JsonValueKind.Array)
				{
					continue;
				}

				foreach (var entry in array.EnumerateArray())
				{
					if (entry.ValueKind == JsonValueKind.Object &&
					    entry.TryGetProperty("uri", out var uriElement) &&
					    uriElement.GetString() is { Length: > 0 } uri &&
					    uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == false)
					{
						yield return uri;
					}
				}
			}
		}
	}
}
