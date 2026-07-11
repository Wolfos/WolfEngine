namespace Wolfie.IAE.UnityAssets;

public enum UnityAssetEntryType { Folder, File }

public sealed record UnityAssetEntry(
	string Name,
	string RelativePath,
	UnityAssetEntryType Type,
	string Extension,
	DateTime? LastModifiedUtc,
	IReadOnlyList<UnityAssetEntry> Children);

public sealed record UnityAssetScanResult(UnityAssetEntry Root, IReadOnlyList<string> Warnings);
