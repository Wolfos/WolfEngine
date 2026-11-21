using System.Globalization;
using Yarn;
using Yarn.Markup;

namespace WolfEngine.Dialogue;

/// <summary>
/// Runs Yarn Spinner conversations one frame at a time. Start one, then each frame read <see cref="State"/> and the
/// current line or options, and call <see cref="Advance"/>, <see cref="SelectOption"/> and <see cref="Update"/> from
/// gameplay. Everything runs on the calling thread inside those calls: nothing advances between frames, and no
/// <c>async</c> code reaches gameplay.
/// </summary>
/// <remarks>
/// Yarn Spinner's VM awaits its responder for every line, option set and command. The runner hands it tasks that only
/// complete when gameplay calls in, and clears the synchronization context around those calls, so the VM's
/// continuations run inline on the caller's stack instead of being posted elsewhere.
/// </remarks>
public sealed class DialogueRunner : IDisposable
{
	/// <summary>The language lines are parsed in. Only the base language ships for now.</summary>
	private const string LanguageCode = "en";

	private readonly Yarn.Dialogue _dialogue;
	private readonly LineParser _lineParser = new();
	private readonly Dictionary<string, Action<DialogueCommand>> _commands = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Action<DialogueCommand, DialogueContinuation>> _blockingCommands = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Func<IConvertible[], IConvertible>> _functions = new(StringComparer.Ordinal);
	private readonly List<int> _optionIds = [];

	private YarnProject? _project;
	private CancellationTokenSource? _cancellation;
	private Task? _run;
	private TaskCompletionSource? _pending;
	private TaskCompletionSource<int>? _pendingOption;
	private int _pendingGeneration;
	private float _waitRemaining;
	private DialogueOption[] _options = [];

	public DialogueRunner(DialogueVariables? variables = null)
	{
		Variables = variables ?? new DialogueVariables();
		_dialogue = new Yarn.Dialogue(Variables)
		{
			Responder = new Responder(this),
			LogErrorMessage = message => Warn(message),
			LogDebugMessage = _ => { }
		};
	}

	/// <summary>Dialogue memory, kept across conversations and projects for as long as the runner lives.</summary>
	public DialogueVariables Variables { get; }

	/// <summary>Where script problems go, such as an unhandled command or a missing node. Standard error by default.</summary>
	public Action<string> LogWarning { get; set; } = message => Console.Error.WriteLine(message);

	public DialogueState State { get; private set; }

	public bool IsRunning => State != DialogueState.Idle;

	/// <summary>The project of the running conversation, or the last one run.</summary>
	public YarnProject? Project => _project;

	/// <summary>The node the conversation is in, or null when none is running.</summary>
	public string? CurrentNode => IsRunning ? _dialogue.CurrentNode : null;

	/// <summary>The line showing while <see cref="State"/> is <see cref="DialogueState.Line"/>.</summary>
	public DialogueLine? CurrentLine { get; private set; }

	/// <summary>The options showing while <see cref="State"/> is <see cref="DialogueState.Options"/>.</summary>
	public IReadOnlyList<DialogueOption> CurrentOptions => _options;

	/// <summary>The command a conversation is waiting on, while it waits on one.</summary>
	public DialogueCommand? CurrentCommand { get; private set; }

	/// <summary>The error that ended the last conversation early, or null when it ran to its end.</summary>
	public Exception? LastError { get; private set; }

	/// <summary>
	/// Registers <c>&lt;&lt;name ...&gt;&gt;</c>. The conversation moves straight on after
	/// <paramref name="handler"/> returns.
	/// </summary>
	public void AddCommand(string name, Action<DialogueCommand> handler)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(handler);
		_blockingCommands.Remove(name);
		_commands[name] = handler;
	}

	/// <summary>
	/// Registers <c>&lt;&lt;name ...&gt;&gt;</c> as a command the conversation waits on until the handler's
	/// <see cref="DialogueContinuation"/> is continued, on this frame or a later one.
	/// </summary>
	public void AddBlockingCommand(string name, Action<DialogueCommand, DialogueContinuation> handler)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(handler);
		_commands.Remove(name);
		_blockingCommands[name] = handler;
	}

	/// <summary>
	/// Registers a function scripts call, such as <c>has_item("small_key")</c>. It must also be declared, with the same
	/// signature, in the project's <c>.ysls.json</c> definitions so the importer can type-check calls to it.
	/// </summary>
	public void AddFunction(string name, Func<IConvertible[], IConvertible> function)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(function);
		_functions[name] = function;
	}

	public void AddFunction(string name, Func<bool> function) => AddFunction(name, (IConvertible[] _) => function());

	public void AddFunction(string name, Func<float> function) => AddFunction(name, (IConvertible[] _) => function());

	public void AddFunction(string name, Func<string> function) => AddFunction(name, (IConvertible[] _) => function());

	public void AddFunction(string name, Func<string, bool> function) =>
		AddFunction(name, (IConvertible[] arguments) => function(Argument<string>(name, arguments, 0)));

	public void AddFunction(string name, Func<string, float> function) =>
		AddFunction(name, (IConvertible[] arguments) => function(Argument<string>(name, arguments, 0)));

	/// <summary>
	/// Starts a conversation at <paramref name="nodeName"/>. Only one runs at a time: while one is running this warns and
	/// returns false without touching it.
	/// </summary>
	public bool Start(YarnProject project, string nodeName)
	{
		ArgumentNullException.ThrowIfNull(project);
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
		if (IsRunning)
		{
			Warn($"Ignored starting '{nodeName}': '{CurrentNode}' is already running. One conversation runs at a time.");
			return false;
		}

		if (!project.HasNode(nodeName))
		{
			Warn($"Yarn project '{project.Name}' has no node '{nodeName}'.");
			return false;
		}

		if (!ReferenceEquals(_project, project))
		{
			_project = project;
			_dialogue.Program = project.Program;
			Variables.Program = project.Program;
		}

		LastError = null;
		_cancellation = new CancellationTokenSource();
		State = DialogueState.Waiting;
		RunInline(() => _run = _dialogue.StartDialogue(nodeName, _cancellation.Token).AsTask());
		ObserveRun();
		return IsRunning;
	}

	/// <summary>Moves on from the line showing. Returns false when no line is showing.</summary>
	public bool Advance()
	{
		if (State != DialogueState.Line) return false;

		CurrentLine = null;
		Release();
		return true;
	}

	/// <summary>Picks option <paramref name="index"/> of <see cref="CurrentOptions"/>. Returns false when it can't.</summary>
	public bool SelectOption(int index)
	{
		if (State != DialogueState.Options || index < 0 || index >= _optionIds.Count || _pendingOption is not { } pending)
		{
			return false;
		}

		var id = _optionIds[index];
		_pendingOption = null;
		_options = [];
		_optionIds.Clear();
		State = DialogueState.Waiting;
		RunInline(() => pending.TrySetResult(id));
		ObserveRun();
		return true;
	}

	/// <summary>Counts down a <c>&lt;&lt;wait&gt;&gt;</c>. Call it every frame, with frame time.</summary>
	public void Update(float deltaTime)
	{
		if (State != DialogueState.Waiting || _waitRemaining <= 0) return;

		_waitRemaining -= Math.Max(0, deltaTime);
		if (_waitRemaining <= 0)
		{
			_waitRemaining = 0;
			Release();
		}
	}

	/// <summary>Abandons the running conversation where it is. Its blocking commands' continuations do nothing after.</summary>
	public void Stop()
	{
		if (!IsRunning) return;

		var cancellation = _cancellation;
		var pending = _pending;
		var pendingOption = _pendingOption;
		_run = null;
		Finish();
		RunInline(() =>
		{
			cancellation?.Cancel();
			pending?.TrySetCanceled();
			pendingOption?.TrySetCanceled();
			_ = _dialogue.Stop();
		});
		cancellation?.Dispose();
	}

	public void Dispose()
	{
		Stop();
		_lineParser.Dispose();
	}

	internal bool IsContinuationDone(int generation) => generation != _pendingGeneration || _pending is null;

	internal void ContinueFrom(int generation)
	{
		if (generation != _pendingGeneration || _pending is null || _waitRemaining > 0) return;

		Release();
	}

	private void Release()
	{
		if (_pending is not { } pending) return;

		_pending = null;
		CurrentCommand = null;
		State = DialogueState.Waiting;
		RunInline(() => pending.TrySetResult());
		ObserveRun();
	}

	private ValueTask Block()
	{
		_pendingGeneration++;
		_pending = new TaskCompletionSource();
		return new ValueTask(_pending.Task);
	}

	private void ObserveRun()
	{
		if (_run is not { IsCompleted: true } run) return;

		_run = null;
		if (run.IsFaulted)
		{
			LastError = run.Exception?.GetBaseException();
			Warn($"Conversation ended by an error: {LastError}");
		}

		Finish();
	}

	private void Finish()
	{
		State = DialogueState.Idle;
		CurrentLine = null;
		CurrentCommand = null;
		_options = [];
		_optionIds.Clear();
		_pending = null;
		_pendingOption = null;
		_waitRemaining = 0;
		_pendingGeneration++;
	}

	private DialogueLine CreateLine(Line line)
	{
		var raw = _project?.GetLineText(line.ID);
		if (raw is null)
		{
			Warn($"Line '{line.ID}' is missing from the string table.");
			return new DialogueLine(line.ID, null, $"<missing line {line.ID}>", []);
		}

		var parsed = _lineParser.ParseString(LineParser.ExpandSubstitutions(raw, line.Substitutions), LanguageCode, true);
		string? speaker = null;
		if (parsed.TryGetAttributeWithName(LineParser.CharacterAttribute, out var character))
		{
			if (character.TryGetProperty(LineParser.CharacterAttributeNameProperty, out string? name)) speaker = name;
			parsed = parsed.DeleteRange(character);
		}

		var markup = parsed.Attributes
			.Where(attribute => attribute.Name != LineParser.CharacterAttribute)
			.Select(attribute => new DialogueMarkup(attribute.Name, attribute.Position, attribute.Length))
			.ToArray();
		return new DialogueLine(line.ID, speaker, parsed.Text, markup);
	}

	private static T Argument<T>(string function, IConvertible[] arguments, int index)
	{
		if (index >= arguments.Length)
			throw new ArgumentException($"Dialogue function '{function}' expects at least {index + 1} argument(s).");

		return (T)Convert.ChangeType(arguments[index], typeof(T), CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Numbers are floats in Yarn, so an int a function returns would not compare equal to a number literal.
	/// </summary>
	private static IConvertible Normalize(IConvertible value) => value switch
	{
		bool or float or string => value,
		_ when value.GetTypeCode() is TypeCode.String => value.ToString(CultureInfo.InvariantCulture),
		_ when value.GetTypeCode() is TypeCode.Boolean => value.ToBoolean(CultureInfo.InvariantCulture),
		_ => value.ToSingle(CultureInfo.InvariantCulture)
	};

	private void Warn(string message) => LogWarning("[Dialogue] " + message);

	private static void RunInline(Action action)
	{
		var context = SynchronizationContext.Current;
		SynchronizationContext.SetSynchronizationContext(null);
		try
		{
			action();
		}
		finally
		{
			SynchronizationContext.SetSynchronizationContext(context);
		}
	}

	private sealed class Responder(DialogueRunner runner) : IDialogueResponder
	{
		public ValueTask HandleLine(Line line, CancellationToken cancellationToken)
		{
			runner.CurrentLine = runner.CreateLine(line);
			runner.State = DialogueState.Line;
			return runner.Block();
		}

		public ValueTask<int> HandleOptions(OptionSet options, CancellationToken cancellationToken)
		{
			runner._optionIds.Clear();
			var shown = new List<DialogueOption>();
			foreach (var option in options.Options)
			{
				// Options whose condition failed are hidden rather than greyed out.
				if (!option.IsAvailable) continue;

				runner._optionIds.Add(option.ID);
				shown.Add(new DialogueOption(shown.Count, runner.CreateLine(option.Line)));
			}

			if (shown.Count == 0) return new ValueTask<int>(Yarn.Dialogue.NoOptionSelected);

			runner._options = shown.ToArray();
			runner._pendingOption = new TaskCompletionSource<int>();
			runner.State = DialogueState.Options;
			return new ValueTask<int>(runner._pendingOption.Task);
		}

		public ValueTask HandleCommand(Command command, CancellationToken cancellationToken)
		{
			var parsed = DialogueCommand.Parse(command.Text);
			if (parsed.Name == "wait")
			{
				if (parsed.Arguments.Count != 1 ||
				    !float.TryParse(parsed.Arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
				{
					throw new InvalidOperationException($"<<{command.Text}>> needs one number of seconds.");
				}

				if (seconds <= 0) return ValueTask.CompletedTask;

				runner.CurrentCommand = parsed;
				runner._waitRemaining = seconds;
				runner.State = DialogueState.Waiting;
				return runner.Block();
			}

			if (runner._blockingCommands.TryGetValue(parsed.Name, out var blocking))
			{
				runner.CurrentCommand = parsed;
				runner.State = DialogueState.Waiting;
				var wait = runner.Block();
				blocking(parsed, new DialogueContinuation(runner, runner._pendingGeneration));
				return wait;
			}

			if (runner._commands.TryGetValue(parsed.Name, out var handler))
			{
				handler(parsed);
				return ValueTask.CompletedTask;
			}

			runner.Warn($"No handler for <<{command.Text}>>; the conversation skipped it.");
			return ValueTask.CompletedTask;
		}

		public bool CanCallFunction(string functionName) => runner._functions.ContainsKey(functionName);

		public ValueTask<IConvertible> InvokeFunction(string functionName, IConvertible[] parameters,
			CancellationToken cancellationToken)
		{
			if (!runner._functions.TryGetValue(functionName, out var function))
				throw new InvalidOperationException($"Dialogue function '{functionName}' is not registered.");

			return new ValueTask<IConvertible>(Normalize(function(parameters)));
		}

		public ValueTask HandleNodeStart(string nodeName, CancellationToken cancellationToken) => ValueTask.CompletedTask;

		public ValueTask HandleNodeComplete(string nodeName, CancellationToken cancellationToken) => ValueTask.CompletedTask;

		public ValueTask HandleDialogueComplete() => ValueTask.CompletedTask;

		public ValueTask PrepareForLines(List<string> lineIDs, CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
