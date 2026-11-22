using ImGuiNET;

namespace WolfEngine.Editor.UI;

internal static class FoliageAssetDragDrop
{
	private const string PayloadType = "WOLF_FOLIAGE_TYPE";

	public static unsafe void DrawSource(Guid assetId)
	{
		if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoPreviewTooltip) == false)
		{
			return;
		}

		ImGui.SetDragDropPayload(PayloadType, (nint)(&assetId), (uint)sizeof(Guid));
		ImGui.EndDragDropSource();
	}

	public static unsafe Guid AcceptType()
	{
		if (ImGui.BeginDragDropTarget() == false) return Guid.Empty;

		var payload = ImGui.AcceptDragDropPayload(PayloadType);
		var id = payload.NativePtr != null && payload.IsDelivery() && payload.DataSize == sizeof(Guid)
			? *(Guid*)payload.Data
			: Guid.Empty;
		ImGui.EndDragDropTarget();
		return id;
	}
}
