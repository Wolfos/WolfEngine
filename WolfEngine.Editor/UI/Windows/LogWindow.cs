using System.Globalization;
using System.Numerics;
using ImGuiNET;
using WolfEngine.Logging;

namespace WolfEngine.Editor.UI;

public sealed class LogWindow : EditorWindow
{
	private const float IconSize = 16.0f;
	private const float DetailHeight = 150.0f;

	private readonly IEditorLogStore _logStore;
	private readonly IIconManager _icons;
	private IReadOnlyList<LogEntry> _entries = Array.Empty<LogEntry>();
	private readonly List<LogEntry> _visibleEntries = new();
	private long _snapshotVersion = -1;
	private long? _selectedSequence;
	private bool _isOpen = true;
	private bool _showInfo = true;
	private bool _showWarnings = true;
	private bool _showErrors = true;
	private bool _autoScroll = true;
	private bool _filtersDirty = true;
	private bool _hasNewVisibleEntries;

	internal LogWindow(IEditorLogStore logStore, IIconManager icons)
	{
		_logStore = logStore ?? throw new ArgumentNullException(nameof(logStore));
		_icons = icons ?? throw new ArgumentNullException(nameof(icons));
	}

	public override string Name => "Log";

	public void Open()
	{
		_isOpen = true;
	}

	public override void Draw(EditorScene scene)
	{
		if (_isOpen == false)
		{
			return;
		}

		RefreshEntries();
		Begin(ref _isOpen);
		DrawToolbar();
		RefreshEntries();
		ImGui.Separator();

		var selectedEntry = LogWindowModel.ResolveSelection(_entries, _selectedSequence);
		var availableHeight = ImGui.GetContentRegionAvail().Y;
		var listHeight = selectedEntry.HasValue
			? MathF.Max(80.0f, availableHeight - DetailHeight - ImGui.GetStyle().ItemSpacing.Y)
			: availableHeight;
		DrawEntryList(listHeight);

		selectedEntry = LogWindowModel.ResolveSelection(_entries, _selectedSequence);
		if (selectedEntry is { } entry)
		{
			DrawDetails(entry);
		}

		ImGui.End();
	}

	private void RefreshEntries()
	{
		var version = _logStore.Version;
		if (version == _snapshotVersion && _filtersDirty == false)
		{
			return;
		}

		var previousLastSequence = _visibleEntries.Count > 0 ? _visibleEntries[^1].Sequence : 0;
		if (version != _snapshotVersion)
		{
			_entries = _logStore.GetSnapshot();
			_snapshotVersion = version;
			_selectedSequence = LogWindowModel.ResolveSelection(_entries, _selectedSequence)?.Sequence;
		}

		LogWindowModel.Filter(
			_entries,
			_showInfo,
			_showWarnings,
			_showErrors,
			_visibleEntries);
		_hasNewVisibleEntries = _filtersDirty == false &&
		                        _visibleEntries.Count > 0 &&
		                        _visibleEntries[^1].Sequence > previousLastSequence;
		_filtersDirty = false;
	}

	private void DrawToolbar()
	{
		if (ImGui.Button("Clear"))
		{
			_logStore.Clear();
			_selectedSequence = null;
			_snapshotVersion = -1;
			_hasNewVisibleEntries = false;
			RefreshEntries();
		}

		var counts = LogWindowModel.CountByKind(_entries);
		ImGui.SameLine();
		DrawKindToggle("info", LogMessageKind.Info, counts.Info, ref _showInfo);
		ImGui.SameLine();
		DrawKindToggle("warning", LogMessageKind.Warning, counts.Warning, ref _showWarnings);
		ImGui.SameLine();
		DrawKindToggle("error", LogMessageKind.Error, counts.Error, ref _showErrors);
		ImGui.SameLine();
		ImGui.Checkbox("Auto-scroll", ref _autoScroll);
	}

	private void DrawKindToggle(string iconName, LogMessageKind kind, int count, ref bool enabled)
	{
		ImGui.PushID(kind.ToString());
		if (enabled == false)
		{
			ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.45f);
		}

		if (_icons.TryGet(iconName, out var textureId))
		{
			if (ImGui.ImageButton("severity", textureId, Vector2.One * IconSize))
			{
				enabled = !enabled;
				_filtersDirty = true;
			}
		}
		else if (ImGui.Button(kind.ToString()))
		{
			enabled = !enabled;
			_filtersDirty = true;
		}

		if (enabled == false)
		{
			ImGui.PopStyleVar();
		}

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip($"Toggle {kind.ToString().ToLowerInvariant()} messages");
		}

		ImGui.SameLine(0.0f, 3.0f);
		ImGui.TextUnformatted(count.ToString(CultureInfo.InvariantCulture));
		ImGui.PopID();
	}

	private unsafe void DrawEntryList(float height)
	{
		if (ImGui.BeginChild("LogEntries", new Vector2(0.0f, height), ImGuiChildFlags.Borders) == false)
		{
			ImGui.EndChild();
			return;
		}

		var wasAtBottom = LogWindowModel.IsAtBottom(ImGui.GetScrollY(), ImGui.GetScrollMaxY());
		if (_visibleEntries.Count == 0)
		{
			ImGui.TextDisabled(_entries.Count == 0 ? "No log messages." : "No messages match the active filters.");
		}
		else if (ImGui.BeginTable(
			         "LogEntriesTable",
			         3,
			         ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
		{
			ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 28.0f);
			ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 92.0f);
			ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch);

			var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
			try
			{
				clipper.Begin(_visibleEntries.Count);
				while (clipper.Step())
				{
					for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
					{
						DrawEntryRow(_visibleEntries[index]);
					}
				}
			}
			finally
			{
				clipper.Destroy();
			}

			ImGui.EndTable();
		}

		if (LogWindowModel.ShouldAutoScroll(_autoScroll, wasAtBottom, _hasNewVisibleEntries))
		{
			ImGui.SetScrollHereY(1.0f);
		}
		_hasNewVisibleEntries = false;
		ImGui.EndChild();
	}

	private void DrawEntryRow(in LogEntry entry)
	{
		ImGui.PushID(entry.Sequence.ToString(CultureInfo.InvariantCulture));
		ImGui.TableNextRow();
		ImGui.TableSetColumnIndex(0);
		DrawKindIcon(entry.Kind);
		ImGui.TableSetColumnIndex(1);
		ImGui.TextUnformatted(LogWindowModel.FormatLocalTimestamp(entry.TimestampUtc));
		ImGui.TableSetColumnIndex(2);
		var selected = _selectedSequence == entry.Sequence;
		if (ImGui.Selectable(
			    LogWindowModel.GetSummary(entry.Message),
			    selected,
			    ImGuiSelectableFlags.SpanAllColumns))
		{
			_selectedSequence = entry.Sequence;
		}
		ImGui.PopID();
	}

	private void DrawDetails(in LogEntry entry)
	{
		ImGui.BeginChild("LogDetails", new Vector2(0.0f, 0.0f), ImGuiChildFlags.Borders);
		DrawKindIcon(entry.Kind);
		ImGui.SameLine();
		ImGui.TextUnformatted($"{LogWindowModel.FormatLocalTimestamp(entry.TimestampUtc)}  {entry.Kind}");
		ImGui.Separator();
		ImGui.TextWrapped(entry.Message);
		if (string.IsNullOrWhiteSpace(entry.Details) == false)
		{
			ImGui.Separator();
			ImGui.TextWrapped(entry.Details);
		}
		ImGui.EndChild();
	}

	private void DrawKindIcon(LogMessageKind kind)
	{
		var iconName = kind.ToString().ToLowerInvariant();
		if (_icons.TryGet(iconName, out var textureId))
		{
			ImGui.Image(textureId, Vector2.One * IconSize);
		}
		else
		{
			ImGui.Dummy(Vector2.One * IconSize);
		}
	}
}

internal static class LogWindowModel
{
	public static void Filter(
		IReadOnlyList<LogEntry> entries,
		bool showInfo,
		bool showWarnings,
		bool showErrors,
		List<LogEntry> destination)
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(destination);
		destination.Clear();
		for (var index = 0; index < entries.Count; index++)
		{
			var entry = entries[index];
			if (entry.Kind switch
			    {
				    LogMessageKind.Info => showInfo,
				    LogMessageKind.Warning => showWarnings,
				    LogMessageKind.Error => showErrors,
				    _ => false
			    })
			{
				destination.Add(entry);
			}
		}
	}

	public static (int Info, int Warning, int Error) CountByKind(IReadOnlyList<LogEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);
		var info = 0;
		var warning = 0;
		var error = 0;
		for (var index = 0; index < entries.Count; index++)
		{
			switch (entries[index].Kind)
			{
				case LogMessageKind.Info: info++; break;
				case LogMessageKind.Warning: warning++; break;
				case LogMessageKind.Error: error++; break;
			}
		}

		return (info, warning, error);
	}

	public static string GetSummary(string message)
	{
		ArgumentNullException.ThrowIfNull(message);
		var trimmed = message.Trim();
		var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
		return lineBreak < 0 ? trimmed : trimmed[..lineBreak].TrimEnd();
	}

	public static LogEntry? ResolveSelection(IReadOnlyList<LogEntry> entries, long? selectedSequence)
	{
		if (selectedSequence is null)
		{
			return null;
		}

		for (var index = 0; index < entries.Count; index++)
		{
			if (entries[index].Sequence == selectedSequence.Value)
			{
				return entries[index];
			}
		}

		return null;
	}

	public static bool IsAtBottom(float scrollY, float scrollMaxY) => scrollMaxY <= 0.0f || scrollY >= scrollMaxY - 1.0f;

	public static bool ShouldAutoScroll(bool enabled, bool wasAtBottom, bool hasNewVisibleEntries) =>
		enabled && wasAtBottom && hasNewVisibleEntries;

	public static string FormatLocalTimestamp(DateTimeOffset timestampUtc) =>
		timestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
