using System;
using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Utility;

namespace WolfEngine.Editor.UI;

public sealed class MaterialImporterWindow : EditorWindow
{
	private static readonly string[] SupportedTextureExtensions =
	[
		"jpg",
		"jpeg",
		"png",
		"bmp",
		"tga",
		"psd",
		"gif",
		"hdr"
	];

	private readonly IFileDialogService _fileDialogService;
	private readonly IMaterialImporter _materialImporter;
	private readonly IEditorProjectService _projectService;
	private readonly IEditorNotificationService _notificationService;
	private readonly MaterialImportRequest _request = new();
	private bool _isOpen;

	public MaterialImporterWindow(
		IFileDialogService fileDialogService,
		IMaterialImporter materialImporter,
		IEditorProjectService projectService,
		IEditorNotificationService notificationService)
	{
		_fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
		_materialImporter = materialImporter ?? throw new ArgumentNullException(nameof(materialImporter));
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
		ResetRequest();
	}

	public override string Name => "Material Importer";

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

		ImGui.SetNextWindowSize(new Vector2(620.0f, 680.0f), ImGuiCond.FirstUseEver);
		Begin(ref _isOpen);
		if (_isOpen == false)
		{
			ImGui.End();
			return;
		}

		if (_projectService.HasOpenProject == false)
		{
			ImGui.TextUnformatted("No project open.");
			ImGui.End();
			return;
		}

		var materialName = _request.MaterialName;
		if (EditorUIUtility.InputText("Material Name", ref materialName))
		{
			_request.MaterialName = materialName;
		}

		var materialType = _request.MaterialType;
		if (EditorUIUtility.EnumCombo("Material Type", ref materialType))
		{
			_request.MaterialType = materialType;
		}

		ImGui.SeparatorText("Textures");
		_request.AlbedoPath = DrawTexturePicker("Albedo", _request.AlbedoPath);
		DrawNormalTexturePicker();
		DrawOcclusionTexturePicker();
		DrawMetallicTexturePicker();
		DrawRoughnessTexturePicker();
		_request.EmissivePath = DrawTexturePicker("Emissive", _request.EmissivePath);

		ImGui.SeparatorText("Material Values");
		var baseColor = _request.BaseColor.ToVector4();
		if (EditorUIUtility.ColorEdit4("Base Color", ref baseColor))
		{
			_request.BaseColor = ColorRGBA.FromVector4(baseColor);
		}

		var emissiveFactor = _request.EmissiveFactor;
		if (EditorUIUtility.ColorEdit3("Emissive Factor", ref emissiveFactor))
		{
			_request.EmissiveFactor = emissiveFactor;
		}

		var metallicFactor = _request.MetallicFactor;
		if (EditorUIUtility.InputFloat("Metallic Factor", ref metallicFactor))
		{
			_request.MetallicFactor = metallicFactor;
		}

		var roughnessFactor = _request.RoughnessFactor;
		if (EditorUIUtility.InputFloat("Roughness Factor", ref roughnessFactor))
		{
			_request.RoughnessFactor = roughnessFactor;
		}

		var emissiveIntensity = _request.EmissiveIntensity;
		if (EditorUIUtility.InputFloat("Emissive Intensity", ref emissiveIntensity))
		{
			_request.EmissiveIntensity = emissiveIntensity;
		}

		var alphaCutoff = _request.AlphaCutoff;
		if (EditorUIUtility.InputFloat("Alpha Cutoff", ref alphaCutoff))
		{
			_request.AlphaCutoff = alphaCutoff;
		}

		ImGui.Spacing();
		if (ImGui.Button("Import", new Vector2(120.0f, 0.0f)))
		{
			var result = _materialImporter.ImportMaterial(_request);
			if (result.Success)
			{
				_notificationService.ReportInfo($"Imported material '{_request.MaterialName}'.");
				ResetRequest();
				_isOpen = false;
			}
			else
			{
				_notificationService.ReportError(result.ErrorMessage ?? "Material import failed.");
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Reset", new Vector2(120.0f, 0.0f)))
		{
			ResetRequest();
		}

		ImGui.End();
	}

	private void DrawNormalTexturePicker()
	{
		ImGui.PushID("Normal");
		ImGui.TextUnformatted("Normal");

		var path = _request.NormalPath ?? string.Empty;
		var availableWidth = MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
		ImGui.SetNextItemWidth(MathF.Max(1.0f, availableWidth - 236.0f));
		ImGui.InputText("##Path", ref path, 1024, ImGuiInputTextFlags.ReadOnly);

		ImGui.SameLine();
		if (ImGui.Button("Browse"))
		{
			var selectedPath = _fileDialogService.OpenFile(new FileDialogOptions
			{
				Title = "Select Normal Texture",
				AllowedExtensions = SupportedTextureExtensions
			});
			if (string.IsNullOrWhiteSpace(selectedPath) == false)
			{
				_request.NormalPath = selectedPath;
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Clear"))
		{
			_request.NormalPath = null;
		}

		ImGui.SameLine();
		ImGui.SetNextItemWidth(96.0f);
		var normalFormat = _request.NormalFormat;
		if (ImGui.BeginCombo("##Format", GetNormalFormatLabel(normalFormat)))
		{
			DrawNormalFormatItem(MaterialNormalMapFormat.RgbYPlus, "RGB Y+", normalFormat);
			DrawNormalFormatItem(MaterialNormalMapFormat.RgbYMinus, "RGB Y-", normalFormat);
			DrawNormalFormatItem(MaterialNormalMapFormat.RgYPlus, "RG Y+", normalFormat);
			DrawNormalFormatItem(MaterialNormalMapFormat.RgYMinus, "RG Y-", normalFormat);
			ImGui.EndCombo();
		}

		ImGui.PopID();
	}

	private void DrawRoughnessTexturePicker()
	{
		ImGui.PushID("Roughness");
		ImGui.TextUnformatted("Roughness");

		var path = _request.RoughnessPath ?? string.Empty;
		var availableWidth = MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
		ImGui.SetNextItemWidth(MathF.Max(1.0f, availableWidth - 316.0f));
		ImGui.InputText("##Path", ref path, 1024, ImGuiInputTextFlags.ReadOnly);

		ImGui.SameLine();
		if (ImGui.Button("Browse"))
		{
			var selectedPath = _fileDialogService.OpenFile(new FileDialogOptions
			{
				Title = "Select Roughness Texture",
				AllowedExtensions = SupportedTextureExtensions
			});
			if (string.IsNullOrWhiteSpace(selectedPath) == false)
			{
				_request.RoughnessPath = selectedPath;
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Clear"))
		{
			_request.RoughnessPath = null;
		}

		ImGui.SameLine();
		ImGui.SetNextItemWidth(48.0f);
		var roughnessChannel = _request.RoughnessChannel;
		DrawTextureChannelCombo(ref roughnessChannel);
		_request.RoughnessChannel = roughnessChannel;

		ImGui.SameLine();
		var invertRoughness = _request.InvertRoughness;
		if (ImGui.Checkbox("Invert", ref invertRoughness))
		{
			_request.InvertRoughness = invertRoughness;
		}
		ImGui.PopID();
	}

	private void DrawMetallicTexturePicker()
	{
		ImGui.PushID("Metallic");
		ImGui.TextUnformatted("Metallic");

		var path = _request.MetallicPath ?? string.Empty;
		var availableWidth = MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
		ImGui.SetNextItemWidth(MathF.Max(1.0f, availableWidth - 236.0f));
		ImGui.InputText("##Path", ref path, 1024, ImGuiInputTextFlags.ReadOnly);

		ImGui.SameLine();
		if (ImGui.Button("Browse"))
		{
			var selectedPath = _fileDialogService.OpenFile(new FileDialogOptions
			{
				Title = "Select Metallic Texture",
				AllowedExtensions = SupportedTextureExtensions
			});
			if (string.IsNullOrWhiteSpace(selectedPath) == false)
			{
				_request.MetallicPath = selectedPath;
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Clear"))
		{
			_request.MetallicPath = null;
		}

		ImGui.SameLine();
		ImGui.SetNextItemWidth(48.0f);
		var metallicChannel = _request.MetallicChannel;
		DrawTextureChannelCombo(ref metallicChannel);
		_request.MetallicChannel = metallicChannel;
		ImGui.PopID();
	}

	private void DrawOcclusionTexturePicker()
	{
		ImGui.PushID("AmbientOcclusion");
		ImGui.TextUnformatted("Ambient Occlusion (ORM.R)");

		var path = _request.OcclusionPath ?? string.Empty;
		var availableWidth = MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
		ImGui.SetNextItemWidth(MathF.Max(1.0f, availableWidth - 236.0f));
		ImGui.InputText("##Path", ref path, 1024, ImGuiInputTextFlags.ReadOnly);

		ImGui.SameLine();
		if (ImGui.Button("Browse"))
		{
			var selectedPath = _fileDialogService.OpenFile(new FileDialogOptions
			{
				Title = "Select Ambient Occlusion Texture",
				AllowedExtensions = SupportedTextureExtensions
			});
			if (string.IsNullOrWhiteSpace(selectedPath) == false)
			{
				_request.OcclusionPath = selectedPath;
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Clear"))
		{
			_request.OcclusionPath = null;
		}

		ImGui.SameLine();
		ImGui.SetNextItemWidth(48.0f);
		var occlusionChannel = _request.OcclusionChannel;
		DrawTextureChannelCombo(ref occlusionChannel);
		_request.OcclusionChannel = occlusionChannel;
		ImGui.PopID();
	}

	private string? DrawTexturePicker(string label, string? texturePath)
	{
		ImGui.PushID(label);
		ImGui.TextUnformatted(label);

		var path = texturePath ?? string.Empty;
		var availableWidth = MathF.Max(1.0f, ImGui.GetContentRegionAvail().X);
		ImGui.SetNextItemWidth(MathF.Max(1.0f, availableWidth - 140.0f));
		ImGui.InputText("##Path", ref path, 1024, ImGuiInputTextFlags.ReadOnly);

		ImGui.SameLine();
		if (ImGui.Button("Browse"))
		{
			var selectedPath = _fileDialogService.OpenFile(new FileDialogOptions
			{
				Title = $"Select {label} Texture",
				AllowedExtensions = SupportedTextureExtensions
			});
			if (string.IsNullOrWhiteSpace(selectedPath) == false)
			{
				texturePath = selectedPath;
			}
		}

		ImGui.SameLine();
		if (ImGui.Button("Clear"))
		{
			texturePath = null;
		}

		ImGui.PopID();
		return texturePath;
	}

	private void DrawNormalFormatItem(MaterialNormalMapFormat format, string label, MaterialNormalMapFormat currentFormat)
	{
		var selected = currentFormat == format;
		if (ImGui.Selectable(label, selected))
		{
			_request.NormalFormat = format;
		}

		if (selected)
		{
			ImGui.SetItemDefaultFocus();
		}
	}

	private static void DrawTextureChannelCombo(ref MaterialTextureChannel channel)
	{
		if (ImGui.BeginCombo("##Channel", channel.ToString()))
		{
			foreach (var candidate in Enum.GetValues<MaterialTextureChannel>())
			{
				var selected = candidate == channel;
				if (ImGui.Selectable(candidate.ToString(), selected))
				{
					channel = candidate;
				}

				if (selected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}

			ImGui.EndCombo();
		}
	}

	private static string GetNormalFormatLabel(MaterialNormalMapFormat format)
	{
		return format switch
		{
			MaterialNormalMapFormat.RgbYPlus => "RGB Y+",
			MaterialNormalMapFormat.RgbYMinus => "RGB Y-",
			MaterialNormalMapFormat.RgYPlus => "RG Y+",
			MaterialNormalMapFormat.RgYMinus => "RG Y-",
			_ => format.ToString()
		};
	}

	private void ResetRequest()
	{
		_request.MaterialName = "New Material";
		_request.MaterialType = MaterialAssetType.Opaque;
		_request.AlbedoPath = null;
		_request.NormalPath = null;
		_request.NormalFormat = MaterialNormalMapFormat.RgbYPlus;
		_request.MetallicPath = null;
		_request.MetallicChannel = MaterialTextureChannel.B;
		_request.RoughnessPath = null;
		_request.RoughnessChannel = MaterialTextureChannel.G;
		_request.InvertRoughness = false;
		_request.EmissivePath = null;
		_request.OcclusionPath = null;
		_request.OcclusionChannel = MaterialTextureChannel.R;
		_request.BaseColor = ColorRGBA.White;
		_request.EmissiveFactor = Vector3.One;
		_request.MetallicFactor = 1.0f;
		_request.RoughnessFactor = 1.0f;
		_request.EmissiveIntensity = 1.0f;
		_request.AlphaCutoff = 0.5f;
	}
}
