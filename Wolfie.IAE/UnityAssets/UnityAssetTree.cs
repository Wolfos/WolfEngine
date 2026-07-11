namespace Wolfie.IAE.UnityAssets;

public enum UnityAssetEntryType { Folder, File }

public sealed record UnityAssetEntry(
	string Name,
	string RelativePath,
	UnityAssetEntryType Type,
	string Extension,
	DateTime? LastModifiedUtc,
	IReadOnlyList<UnityAssetEntry> Children,
	bool IsManaged = false,
	Guid? ManagedAssetId = null,
	string? UnityGuid = null);

public sealed record UnityAssetScanResult(UnityAssetEntry Root, IReadOnlyList<string> Warnings);
