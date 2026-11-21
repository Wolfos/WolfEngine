using Yarn;
using Yarn.Compiler;

namespace WolfEngine.Dialogue.Tests;

public sealed class DialogueRunnerTests
{
	private const string Blacksmith = """
		title: Blacksmith
		---
		<<if $met_blacksmith>>
		Blacksmith: Back again?
		<<else>>
		Blacksmith: Welcome, traveller. [b]Sharp[/b] blades here.
		<<set $met_blacksmith to true>>
		<<endif>>
		-> Open shop
		    <<open_shop blacksmith>>
		    Blacksmith: Anything else?
		-> Ask about the key <<if has_item("small_key")>>
		    Blacksmith: That key opens the old mill.
		-> Ask about gold
		    Blacksmith: You carry {gold_count()} gold.
		-> Goodbye
		Blacksmith: Safe travels.
		===
		""";

	private DialogueRunner _runner = null!;

	[SetUp]
	public void SetUp()
	{
		_runner = new DialogueRunner();
		_runner.AddFunction("has_item", (string _) => false);
		_runner.AddFunction("gold_count", () => 42f);
	}

	[TearDown]
	public void TearDown() => _runner.Dispose();

	[Test]
	public void LineSplitsOffItsSpeakerAndMarkup()
	{
		Assert.That(_runner.Start(Compile(Blacksmith), "Blacksmith"), Is.True);

		Assert.That(_runner.State, Is.EqualTo(DialogueState.Line));
		var line = _runner.CurrentLine!;
		Assert.That(line.Speaker, Is.EqualTo("Blacksmith"));
		Assert.That(line.Text, Is.EqualTo("Welcome, traveller. Sharp blades here."));
		var bold = line.Markup.Single(markup => markup.Name == "b");
		Assert.That(line.Text.Substring(bold.Position, bold.Length), Is.EqualTo("Sharp"));
	}

	[Test]
	public void OptionsWhoseConditionFailsAreHidden()
	{
		_runner.Start(Compile(Blacksmith), "Blacksmith");
		_runner.Advance();

		Assert.That(_runner.State, Is.EqualTo(DialogueState.Options));
		Assert.That(_runner.CurrentOptions.Select(option => option.Text),
			Is.EqualTo(new[] { "Open shop", "Ask about gold", "Goodbye" }));
	}

	[Test]
	public void FunctionResultsFillLineSubstitutions()
	{
		_runner.Start(Compile(Blacksmith), "Blacksmith");
		_runner.Advance();
		_runner.SelectOption(1);

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("You carry 42 gold."));
	}

	[Test]
	public void BlockingCommandHoldsTheConversationUntilContinued()
	{
		DialogueContinuation? shop = null;
		string? shopId = null;
		_runner.AddBlockingCommand("open_shop", (command, continuation) =>
		{
			shopId = command.Arguments[0];
			shop = continuation;
		});
		_runner.Start(Compile(Blacksmith), "Blacksmith");
		_runner.Advance();
		_runner.SelectOption(0);

		Assert.That(shopId, Is.EqualTo("blacksmith"));
		Assert.That(_runner.State, Is.EqualTo(DialogueState.Waiting));
		Assert.That(_runner.CurrentCommand!.Name, Is.EqualTo("open_shop"));

		// Frames pass while the shop is open; nothing moves on by itself.
		_runner.Update(1f);
		Assert.That(_runner.State, Is.EqualTo(DialogueState.Waiting));

		shop!.Continue();
		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("Anything else?"));
		Assert.That(shop.IsDone, Is.True);
	}

	[Test]
	public void ContinuingSynchronouslyMovesStraightOn()
	{
		_runner.AddBlockingCommand("open_shop", (_, continuation) => continuation.Continue());
		_runner.Start(Compile(Blacksmith), "Blacksmith");
		_runner.Advance();
		_runner.SelectOption(0);

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("Anything else?"));
	}

	[Test]
	public void StaleContinuationDoesNothingAfterTheConversationIsStopped()
	{
		DialogueContinuation? shop = null;
		_runner.AddBlockingCommand("open_shop", (_, continuation) => shop = continuation);
		var project = Compile(Blacksmith);
		_runner.Start(project, "Blacksmith");
		_runner.Advance();
		_runner.SelectOption(0);
		_runner.Stop();

		Assert.That(_runner.State, Is.EqualTo(DialogueState.Idle));
		_runner.Start(project, "Blacksmith");
		shop!.Continue();

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("Back again?"));
	}

	[Test]
	public void WaitCountsDownWithFrameTime()
	{
		_runner.Start(Compile("""
			title: Start
			---
			<<wait 0.5>>
			Done.
			===
			"""), "Start");

		Assert.That(_runner.State, Is.EqualTo(DialogueState.Waiting));
		_runner.Update(0.3f);
		Assert.That(_runner.State, Is.EqualTo(DialogueState.Waiting));
		_runner.Update(0.3f);
		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("Done."));
	}

	[Test]
	public void NonBlockingCommandRunsAndMovesOn()
	{
		var calls = new List<string>();
		_runner.AddCommand("give", command => calls.Add(string.Join(",", command.Arguments)));
		_runner.Start(Compile("""
			title: Start
			---
			<<give gold "two coins">>
			Here.
			===
			"""), "Start");

		Assert.That(calls, Is.EqualTo(new[] { "gold,two coins" }));
		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("Here."));
	}

	[Test]
	public void UnknownCommandIsSkipped()
	{
		_runner.Start(Compile("""
			title: Start
			---
			<<not_registered>>
			After.
			===
			"""), "Start");

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("After."));
	}

	[Test]
	public void VariablesCarryAcrossConversations()
	{
		var project = Compile(Blacksmith);
		_runner.Start(project, "Blacksmith");
		RunToEnd();

		_runner.Start(project, "Blacksmith");

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("Back again?"));
		Assert.That(_runner.Variables.Snapshot()["$met_blacksmith"], Is.EqualTo(DialogueValue.From(true)));
	}

	[Test]
	public void RestoredVariablesAreWhatScriptsRead()
	{
		var project = Compile(Blacksmith);
		_runner.Variables.Restore(new Dictionary<string, DialogueValue> { ["$met_blacksmith"] = DialogueValue.From(true) });

		_runner.Start(project, "Blacksmith");

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("Back again?"));
	}

	[Test]
	public void UnsetVariablesReadTheirDeclaredDefault()
	{
		_runner.Start(Compile("""
			title: Start
			---
			<<declare $coins = 3>>
			You have {$coins} coins.
			===
			"""), "Start");

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("You have 3 coins."));
	}

	[Test]
	public void SecondStartWhileRunningIsIgnored()
	{
		var project = Compile(Blacksmith);
		_runner.Start(project, "Blacksmith");
		_runner.Advance();

		Assert.That(_runner.Start(project, "Blacksmith"), Is.False);
		Assert.That(_runner.State, Is.EqualTo(DialogueState.Options));
	}

	[Test]
	public void StartingAMissingNodeFails()
	{
		Assert.That(_runner.Start(Compile(Blacksmith), "Nobody"), Is.False);
		Assert.That(_runner.IsRunning, Is.False);
	}

	[Test]
	public void ConversationEndsIdle()
	{
		_runner.Start(Compile(Blacksmith), "Blacksmith");
		RunToEnd();

		Assert.That(_runner.State, Is.EqualTo(DialogueState.Idle));
		Assert.That(_runner.LastError, Is.Null);
	}

	[Test]
	public void AllOptionsHiddenSkipsTheOptionSet()
	{
		_runner.Start(Compile("""
			title: Start
			---
			-> Only with the key <<if has_item("small_key")>>
			    Opened.
			After.
			===
			"""), "Start");

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("After."));
	}

	[Test]
	public void JumpsReachNodesInOtherFiles()
	{
		var project = Compile(
			("a.yarn", "title: A\n---\n<<jump B>>\n==="),
			("b.yarn", "title: B\n---\nIn B.\n==="));

		_runner.Start(project, "A");

		Assert.That(_runner.CurrentLine!.Text, Is.EqualTo("In B."));
	}

	[Test]
	public void ArtifactRoundTripsTheProgramAndLines()
	{
		var project = Compile(Blacksmith);
		using var stream = new MemoryStream();
		YarnProjectArtifact.Write(stream, project.Program, project.Lines);
		stream.Position = 0;

		var read = YarnProjectArtifact.Read(stream, "Read");
		_runner.Start(read, "Blacksmith");

		Assert.That(read.Lines, Is.EquivalentTo(project.Lines));
		Assert.That(_runner.CurrentLine!.Speaker, Is.EqualTo("Blacksmith"));
	}

	[Test]
	public void CommandParsingKeepsQuotedSpaces()
	{
		var command = DialogueCommand.Parse("say \"hello there\" 2");

		Assert.That(command.Name, Is.EqualTo("say"));
		Assert.That(command.Arguments, Is.EqualTo(new[] { "hello there", "2" }));
	}

	private void RunToEnd()
	{
		for (var guard = 0; guard < 100 && _runner.IsRunning; guard++)
		{
			if (_runner.State == DialogueState.Line) _runner.Advance();
			else if (_runner.State == DialogueState.Options) _runner.SelectOption(_runner.CurrentOptions.Count - 1);
			else _runner.Update(1f);
		}

		Assert.That(_runner.IsRunning, Is.False, "The conversation did not end.");
	}

	private static YarnProject Compile(string source) => Compile(("test.yarn", source));

	private static YarnProject Compile(params (string Name, string Source)[] files)
	{
		var declarations = new[]
		{
			Function("has_item", Types.Boolean, Types.String),
			Function("gold_count", Types.Number)
		};
		var folder = Path.Combine(Path.GetTempPath(), "WolfEngineDialogueTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		CompilationResult result;
		try
		{
			var paths = files.Select(file =>
			{
				var path = Path.Combine(folder, file.Name);
				File.WriteAllText(path, file.Source);
				return path;
			}).ToArray();
			var job = CompilationJob.CreateFromFiles(paths);
			job.VariableDeclarations = declarations;
			result = Compiler.Compile(job);
		}
		finally
		{
			Directory.Delete(folder, true);
		}

		Assert.That(result.ContainsErrors, Is.False, string.Join("\n", result.Diagnostics));
		return new YarnProject("Test", result.Program!,
			result.StringTable!.ToDictionary(pair => pair.Key, pair => pair.Value.text!));
	}

	private static Declaration Function(string name, IType returns, params IType[] parameters)
	{
		var type = new FunctionTypeBuilder().WithReturnType(returns);
		foreach (var parameter in parameters) type = type.WithParameter(parameter);
		return new DeclarationBuilder().WithName(name).WithType(type.FunctionType).Declaration;
	}
}
