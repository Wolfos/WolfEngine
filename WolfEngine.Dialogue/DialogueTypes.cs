namespace WolfEngine.Dialogue;

/// <summary>What the runner is waiting on.</summary>
public enum DialogueState
{
	/// <summary>No conversation is running.</summary>
	Idle,

	/// <summary>A line is showing; <see cref="DialogueRunner.Advance"/> moves on.</summary>
	Line,

	/// <summary>Options are showing; <see cref="DialogueRunner.SelectOption"/> picks one.</summary>
	Options,

	/// <summary>A blocking command or <c>&lt;&lt;wait&gt;&gt;</c> holds the conversation until it continues.</summary>
	Waiting
}

/// <summary>A markup span such as <c>[b]Sharp[/b]</c>, over the <see cref="DialogueLine.Text"/> it was parsed from.</summary>
public readonly record struct DialogueMarkup(string Name, int Position, int Length);

/// <summary>One line ready to show: its speaker split off, substitutions filled in and markup parsed out.</summary>
/// <param name="Id">The Yarn line ID, stable across edits, for localization later.</param>
/// <param name="Speaker">The <c>Name:</c> prefix of the line, or null when it has none.</param>
/// <param name="Text">The plain text to show, without its speaker or markup tags.</param>
/// <param name="Markup">The markup spans over <paramref name="Text"/>.</param>
public sealed record DialogueLine(string Id, string? Speaker, string Text, IReadOnlyList<DialogueMarkup> Markup);

/// <summary>An option the player may pick. Options whose condition failed are never offered.</summary>
public sealed record DialogueOption(int Index, DialogueLine Line)
{
	public string Text => Line.Text;
}

/// <summary>A <c>&lt;&lt;command&gt;&gt;</c> from a script, split into its name and arguments.</summary>
public sealed record DialogueCommand(string Name, IReadOnlyList<string> Arguments, string Text)
{
	/// <summary>
	/// Splits a command's text on spaces. Double quotes keep spaces inside one argument, as Yarn Spinner's own command
	/// dispatch does.
	/// </summary>
	public static DialogueCommand Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		var parts = new List<string>();
		var current = new System.Text.StringBuilder();
		var quoted = false;
		var any = false;
		foreach (var character in text)
		{
			if (character == '"')
			{
				quoted = !quoted;
				any = true;
			}
			else if (char.IsWhiteSpace(character) && !quoted)
			{
				Flush();
			}
			else
			{
				current.Append(character);
				any = true;
			}
		}

		Flush();
		return new DialogueCommand(parts.Count > 0 ? parts[0] : string.Empty, parts.Skip(1).ToArray(), text);

		void Flush()
		{
			if (!any) return;
			parts.Add(current.ToString());
			current.Clear();
			any = false;
		}
	}
}

/// <summary>
/// Handed to a blocking command. The conversation stays on that command until <see cref="Continue"/> is called, from
/// whichever later frame the command finishes on, such as the one a shop closes on.
/// </summary>
public sealed class DialogueContinuation
{
	private readonly DialogueRunner _runner;
	private readonly int _generation;

	internal DialogueContinuation(DialogueRunner runner, int generation)
	{
		_runner = runner;
		_generation = generation;
	}

	/// <summary>Whether <see cref="Continue"/> has been called, or the conversation it belongs to has ended.</summary>
	public bool IsDone => _runner.IsContinuationDone(_generation);

	/// <summary>Resumes the conversation. Later calls, and calls after the conversation ended, do nothing.</summary>
	public void Continue() => _runner.ContinueFrom(_generation);
}
