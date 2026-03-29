using System;
using System.IO;
using System.Text;

namespace WolfEngine.Editor.Projects;

internal static class ProjectGameplayScaffolder
{
	public const string GameplayFolderName = "Gameplay";
	public const string GameplaySourceFileName = "GameplayBootstrap.cs";
	public const string SolutionFileExtension = ".sln";
	private const string CsProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
	private const string TargetFramework = "net10.0";
	private const string EngineProjectReference = "../../WolfEngine/WolfEngine/WolfEngine.csproj";
	private const string EcsProjectReference = "../../WolfEngine/WolfEngine.ECS/WolfEngine.ECS.csproj";
	private const string PhysicsProjectReference = "../../WolfEngine/WolfEngine.Physics/WolfEngine.Physics.csproj";
	private const string EngineSolutionReference = @"..\WolfEngine\WolfEngine\WolfEngine.csproj";
	private const string EcsSolutionReference = @"..\WolfEngine\WolfEngine.ECS\WolfEngine.ECS.csproj";
	private const string PhysicsSolutionReference = @"..\WolfEngine\WolfEngine.Physics\WolfEngine.Physics.csproj";
	private const string EditorSolutionReference = @"..\WolfEngine\WolfEngine.Editor\WolfEngine.Editor.csproj";

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
			 <Project Sdk="Microsoft.NET.Sdk">
			   <PropertyGroup>
			     <TargetFramework>{TargetFramework}</TargetFramework>
			     <ImplicitUsings>enable</ImplicitUsings>
			     <Nullable>enable</Nullable>
			     <RootNamespace>{rootNamespace}</RootNamespace>
			   </PropertyGroup>

			   <ItemGroup>
			     <ProjectReference Include="{EngineProjectReference}" />
			     <ProjectReference Include="{EcsProjectReference}" />
			     <ProjectReference Include="{PhysicsProjectReference}" />
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
			  using WolfEngineGame.Gameplay.Systems;
			  
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
			  		yield return new RotateSystem();
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
