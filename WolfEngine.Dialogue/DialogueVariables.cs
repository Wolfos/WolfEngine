using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Yarn;

namespace WolfEngine.Dialogue;

public enum DialogueValueKind
{
	Boolean,
	Number,
	String
}

/// <summary>One stored Yarn variable's value, in a form a save system can write out as-is.</summary>
public readonly record struct DialogueValue(DialogueValueKind Kind, bool Boolean, float Number, string? Text)
{
	public static DialogueValue From(bool value) => new(DialogueValueKind.Boolean, value, 0, null);
	public static DialogueValue From(float value) => new(DialogueValueKind.Number, false, value, null);
	public static DialogueValue From(string value) => new(DialogueValueKind.String, false, 0, value ?? string.Empty);

	public object Boxed => Kind switch
	{
		DialogueValueKind.Boolean => Boolean,
		DialogueValueKind.Number => Number,
		_ => Text ?? string.Empty
	};
}

/// <summary>
/// Dialogue memory: the Yarn variables scripts have set, such as <c>$met_blacksmith</c>, plus Yarn's own node-visit
/// tracking. Variables a script never set read as their declared default from the running program. Quest and
/// inventory state are not kept here; scripts read those through functions, so gameplay stays their only owner.
/// </summary>
public sealed class DialogueVariables : IVariableStorage
{
	private readonly Dictionary<string, DialogueValue> _values = new(StringComparer.Ordinal);

	public Yarn.Program? Program { get; set; }

	public ISmartVariableEvaluator? SmartVariableEvaluator { get; set; }

	/// <summary>How many variables have been set.</summary>
	public int Count => _values.Count;

	public void SetValue(string variableName, string stringValue) => _values[variableName] = DialogueValue.From(stringValue);

	public void SetValue(string variableName, float floatValue) => _values[variableName] = DialogueValue.From(floatValue);

	public void SetValue(string variableName, bool boolValue) => _values[variableName] = DialogueValue.From(boolValue);

	public void Clear() => _values.Clear();

	public bool TryGetValue<T>(string variableName, [NotNullWhen(true)] out T? result)
	{
		if (_values.TryGetValue(variableName, out var stored))
		{
			return TryConvert(stored.Boxed, out result);
		}

		switch (Program?.GetVariableKind(variableName) ?? VariableKind.Unknown)
		{
			case VariableKind.Stored when Program!.TryGetInitialValue<T>(variableName, out var initial) && initial is not null:
				result = initial;
				return true;
			case VariableKind.Smart when SmartVariableEvaluator is { } evaluator &&
			                             evaluator.TryGetSmartVariable<T>(variableName, out var smart) && smart is not null:
				result = smart;
				return true;
			default:
				result = default;
				return false;
		}
	}

	public VariableKind GetVariableKind(string variableName)
	{
		if (_values.ContainsKey(variableName)) return VariableKind.Stored;

		return Program?.GetVariableKind(variableName) ?? VariableKind.Unknown;
	}

	/// <summary>Every variable that has been set, for a save file.</summary>
	public IReadOnlyDictionary<string, DialogueValue> Snapshot() => new Dictionary<string, DialogueValue>(_values, StringComparer.Ordinal);

	/// <summary>Replaces every variable with <paramref name="values"/>, as read back from a save file.</summary>
	public void Restore(IReadOnlyDictionary<string, DialogueValue> values)
	{
		ArgumentNullException.ThrowIfNull(values);
		_values.Clear();
		foreach (var (name, value) in values) _values[name] = value;
	}

	private static bool TryConvert<T>(object value, out T? result)
	{
		if (value is T typed)
		{
			result = typed;
			return true;
		}

		try
		{
			result = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
			return true;
		}
		catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
		{
			result = default;
			return false;
		}
	}
}
