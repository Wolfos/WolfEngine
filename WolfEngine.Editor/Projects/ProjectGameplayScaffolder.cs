using System.Text;

namespace WolfEngine.Editor.Projects;

internal static class ProjectGameplayScaffolder
{
	public const string GameplayFolderName = "Gameplay";
	public const string GameplaySourceFileName = "GameplayBootstrap.cs";
	public const string SolutionFileExtension = ".sln";
	private const string CsProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
	private const string SolutionFolderTypeGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
	private const string TargetFramework = "net10.0";
	private const string EngineProjectReference = "../../WolfEngine/WolfEngine/WolfEngine.csproj";
	private const string EcsProjectReference = "../../WolfEngine/WolfEngine.ECS/WolfEngine.ECS.csproj";
	private const string PhysicsProjectReference = "../../WolfEngine/WolfEngine.Physics/WolfEngine.Physics.csproj";
	private const string AudioProjectReference = "../../WolfEngine/WolfEngine.Audio/WolfEngine.Audio.csproj";
	private const string UiProjectReference = "../../WolfEngine/WolfEngine.UI/WolfEngine.UI.csproj";
	private const string EngineSolutionReference = @"..\WolfEngine\WolfEngine\WolfEngine.csproj";
	private const string EcsSolutionReference = @"..\WolfEngine\WolfEngine.ECS\WolfEngine.ECS.csproj";
	private const string PhysicsSolutionReference = @"..\WolfEngine\WolfEngine.Physics\WolfEngine.Physics.csproj";
	private const string EditorSolutionReference = @"..\WolfEngine\WolfEngine.Editor\WolfEngine.Editor.csproj";
	private const string EcsTestsSolutionReference = @"..\WolfEngine\WolfEngine.ECS.Tests\WolfEngine.ECS.Tests.csproj";
	private const string EditorTestsSolutionReference = @"..\WolfEngine\WolfEngine.Editor.Tests\WolfEngine.Editor.Tests.csproj";
	private const string PhysicsTestsSolutionReference = @"..\WolfEngine\WolfEngine.Physics.Tests\WolfEngine.Physics.Tests.csproj";
	private const string EngineTestsSolutionReference = @"..\WolfEngine\WolfEngine.Tests\WolfEngine.Tests.csproj";
	private const string EditorAutomationSolutionReference = @"..\WolfEngine\WolfEngine.Editor.Automation\WolfEngine.Editor.Automation.csproj";
	private const string RuntimeSolutionReference = @"..\WolfEngine\WolfEngine.Runtime\WolfEngine.Runtime.csproj";
	private const string BuildSolutionReference = @"..\WolfEngine\WolfEngine.Build\WolfEngine.Build.csproj";
	private const string AudioTestsSolutionReference = @"..\WolfEngine\WolfEngine.Audio.Tests\WolfEngine.Audio.Tests.csproj";
	private const string AudioSolutionReference = @"..\WolfEngine\WolfEngine.Audio\WolfEngine.Audio.csproj";
	private const string UiSolutionReference = @"..\WolfEngine\WolfEngine.UI\WolfEngine.UI.csproj";
	private const string UiTestsSolutionReference = @"..\WolfEngine\WolfEngine.UI.Tests\WolfEngine.UI.Tests.csproj";

	public static string GetGameplayProjectRelativePath(string projectName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
		return ProjectPathUtility.NormalizeRelativePath(Path.Combine(GameplayFolderName, $"{projectName}.Gameplay.csproj"));
	}

	public static string GetSolutionFileName(string projectName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
		return $"{projectName}{SolutionFileExtension}";
	}

	public static void Scaffold(string projectRootPath, string projectName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

		var gameplayFolderPath = Path.Combine(projectRootPath, GameplayFolderName);
		Directory.CreateDirectory(gameplayFolderPath);

		var rootNamespace = BuildRootNamespace(projectName);
		var projectPath = Path.Combine(projectRootPath, GetGameplayProjectRelativePath(projectName).Replace('/', Path.DirectorySeparatorChar));
		var sourcePath = Path.Combine(gameplayFolderPath, GameplaySourceFileName);
		var solutionPath = Path.Combine(projectRootPath, GetSolutionFileName(projectName));

		File.WriteAllText(projectPath, CreateProjectFileContents(rootNamespace));
		File.WriteAllText(sourcePath, CreateBootstrapSourceContents(rootNamespace));
		File.WriteAllText(solutionPath, CreateSolutionFileContents(projectName));
	}

	private static string CreateProjectFileContents(string rootNamespace)
	{
		return
			$"""
			 <Project Sdk="Microsoft.NET.Sdk.Razor">
			   <PropertyGroup>
			     <TargetFramework>{TargetFramework}</TargetFramework>
			     <ImplicitUsings>enable</ImplicitUsings>
			     <Nullable>enable</Nullable>
			     <RootNamespace>{rootNamespace}</RootNamespace>
			   </PropertyGroup>

			   <ItemGroup>
			     <FrameworkReference Include="Microsoft.AspNetCore.App" />
			     <EmbeddedResource Include="UI\**\*.css" />
			   </ItemGroup>

			   <ItemGroup>
			     <ProjectReference Include="{EngineProjectReference}" />
			     <ProjectReference Include="{EcsProjectReference}" />
			     <ProjectReference Include="{PhysicsProjectReference}" />
			     <ProjectReference Include="{AudioProjectReference}" />
			     <ProjectReference Include="{UiProjectReference}" />
			   </ItemGroup>
			 </Project>
			 """;
	}

	private static string CreateBootstrapSourceContents(string rootNamespace)
	{
		return
			$$"""
			  using WolfEngine.ECS;
			  using WolfEngine.Gameplay;
			  
			  namespace {{rootNamespace}};
			  
			  // ReSharper disable once UnusedType.Global
			  public static class GameplayEntrypoint
			  {
			  	// ReSharper disable once UnusedMember.Global
			  	public static IGameplayModule? CreateModule() => new GameplayModule();
			  }
			  
			  
			  public sealed class GameplayModule : IGameplayModule
			  {
			  	public IEnumerable<ISystem> CreateSystems()
			  	{
			  		yield break;
			  	}
			  
			  	public void OnLoaded(World world)
			  	{
			  	}
			  
			  	public void OnUnloading(World world)
			  	{
			  	}

			  	public void PhysicsUpdate(float fixedDeltaTime, World world)
			  	{
			  	}
			  
			  	public void Update(float deltaTime, World world)
			  	{
			  	}
			  }
			  """;
	}

	private static string CreateSolutionFileContents(string projectName)
	{
		var gameplayProjectName = $"{projectName}.Gameplay";
		var gameplayProjectPath = $@"{GameplayFolderName}\{projectName}.Gameplay.csproj";
		var gameplayProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var engineProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var ecsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var physicsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var editorProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var ecsTestsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var editorTestsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var physicsTestsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var engineTestsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var editorAutomationProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var runtimeProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var buildProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var audioTestsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var audioProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var uiProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var uiTestsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var engineFolderGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
		var testsFolderGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();

		return
			$$"""
			  Microsoft Visual Studio Solution File, Format Version 12.00
			  Project("{{CsProjectTypeGuid}}") = "{{gameplayProjectName}}", "{{gameplayProjectPath}}", "{{gameplayProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine", "{{EngineSolutionReference}}", "{{engineProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.ECS", "{{EcsSolutionReference}}", "{{ecsProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Physics", "{{PhysicsSolutionReference}}", "{{physicsProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Editor", "{{EditorSolutionReference}}", "{{editorProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.ECS.Tests", "{{EcsTestsSolutionReference}}", "{{ecsTestsProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Editor.Tests", "{{EditorTestsSolutionReference}}", "{{editorTestsProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Physics.Tests", "{{PhysicsTestsSolutionReference}}", "{{physicsTestsProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Tests", "{{EngineTestsSolutionReference}}", "{{engineTestsProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Editor.Automation", "{{EditorAutomationSolutionReference}}", "{{editorAutomationProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Runtime", "{{RuntimeSolutionReference}}", "{{runtimeProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Build", "{{BuildSolutionReference}}", "{{buildProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Audio.Tests", "{{AudioTestsSolutionReference}}", "{{audioTestsProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.Audio", "{{AudioSolutionReference}}", "{{audioProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.UI", "{{UiSolutionReference}}", "{{uiProjectGuid}}"
			  EndProject
			  Project("{{CsProjectTypeGuid}}") = "WolfEngine.UI.Tests", "{{UiTestsSolutionReference}}", "{{uiTestsProjectGuid}}"
			  EndProject
			  Project("{{SolutionFolderTypeGuid}}") = "Engine", "Engine", "{{engineFolderGuid}}"
			  EndProject
			  Project("{{SolutionFolderTypeGuid}}") = "Tests", "Tests", "{{testsFolderGuid}}"
			  EndProject
			  Global
			  	GlobalSection(SolutionConfigurationPlatforms) = preSolution
			  		Debug|Any CPU = Debug|Any CPU
			  		Release|Any CPU = Release|Any CPU
			  	EndGlobalSection
			  	GlobalSection(ProjectConfigurationPlatforms) = postSolution
			  		{{gameplayProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{gameplayProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{gameplayProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{gameplayProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{engineProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{engineProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{engineProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{engineProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{ecsProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{ecsProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{ecsProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{ecsProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{physicsProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{physicsProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{physicsProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{physicsProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{editorProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{editorProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{editorProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{editorProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{ecsTestsProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{ecsTestsProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{ecsTestsProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{ecsTestsProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{editorTestsProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{editorTestsProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{editorTestsProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{editorTestsProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{physicsTestsProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{physicsTestsProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{physicsTestsProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{physicsTestsProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{engineTestsProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{engineTestsProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{engineTestsProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{engineTestsProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{editorAutomationProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{editorAutomationProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{editorAutomationProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{editorAutomationProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{runtimeProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{runtimeProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{runtimeProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{runtimeProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{buildProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{buildProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{buildProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{buildProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{audioTestsProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{audioTestsProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{audioTestsProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{audioTestsProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{audioProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{audioProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{audioProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{audioProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{uiProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{uiProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{uiProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{uiProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  		{{uiTestsProjectGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
			  		{{uiTestsProjectGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
			  		{{uiTestsProjectGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
			  		{{uiTestsProjectGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
			  EndGlobalSection
			  GlobalSection(NestedProjects) = preSolution
			  		{{engineProjectGuid}} = {{engineFolderGuid}}
			  		{{ecsProjectGuid}} = {{engineFolderGuid}}
			  		{{editorProjectGuid}} = {{engineFolderGuid}}
			  		{{physicsProjectGuid}} = {{engineFolderGuid}}
			  		{{editorAutomationProjectGuid}} = {{engineFolderGuid}}
			  		{{runtimeProjectGuid}} = {{engineFolderGuid}}
			  		{{buildProjectGuid}} = {{engineFolderGuid}}
			  		{{audioProjectGuid}} = {{engineFolderGuid}}
			  		{{uiProjectGuid}} = {{engineFolderGuid}}
			  		{{testsFolderGuid}} = {{engineFolderGuid}}
			  		{{ecsTestsProjectGuid}} = {{testsFolderGuid}}
			  		{{editorTestsProjectGuid}} = {{testsFolderGuid}}
			  		{{physicsTestsProjectGuid}} = {{testsFolderGuid}}
			  		{{engineTestsProjectGuid}} = {{testsFolderGuid}}
			  		{{audioTestsProjectGuid}} = {{testsFolderGuid}}
			  		{{uiTestsProjectGuid}} = {{testsFolderGuid}}
			  EndGlobalSection
			  EndGlobal
			  """;
	}

	private static string BuildRootNamespace(string projectName)
	{
		var sanitized = SanitizeIdentifier(projectName);
		return string.IsNullOrWhiteSpace(sanitized)
			? "WolfProject.Gameplay"
			: $"{sanitized}.Gameplay";
	}

	private static string SanitizeIdentifier(string value)
	{
		var builder = new StringBuilder();
		var capitalizeNext = true;

		for (var i = 0; i < value.Length; i++)
		{
			var character = value[i];
			if (char.IsLetterOrDigit(character) == false)
			{
				capitalizeNext = true;
				continue;
			}

			if (builder.Length == 0 && char.IsDigit(character))
			{
				builder.Append('_');
			}

			builder.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
			capitalizeNext = false;
		}

		return builder.ToString();
	}
}
