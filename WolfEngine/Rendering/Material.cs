using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.AssetPipeline;

namespace WolfEngine;

[RuntimeAsset(AssetType.Material, typeof(MaterialAsset), typeof(IMaterialRuntimeAssetResolver))]
public sealed class Material
{
    private readonly object _stateSync = new();
    private ColorRGBA _color = ColorRGBA.White;
    private float _metallicFactor = 1.0f;
    private float _roughnessFactor = 1.0f;
    private System.Numerics.Vector3 _emissiveFactor = System.Numerics.Vector3.Zero;
    private float _emissiveIntensity = 1.0f;
    private Texture _albedoTexture = null!;
    private Texture _metallicRoughnessTexture = null!;
    private Texture _normalTexture = null!;
    private Texture _emissiveTexture = null!;
    private Texture _occlusionTexture = null!;
    private AlphaMode _alphaMode;
    private float _alphaCutoff;
    private IMaterialResources? _resources;
    private int _contentRevision = 1;
    private int _resourceRevision;
    private int _lastBuiltContentRevision;
    private int _lastBuiltTextureRevisionHash;
    private bool _hasGpuResources;
    private bool _resourceRequestPending;

    public Material(string shaderPath)
    {
        if (string.IsNullOrWhiteSpace(shaderPath))
        {
            throw new ArgumentException("Shader path cannot be empty.", nameof(shaderPath));
        }

        ShaderPath = shaderPath;
    }

    public string ShaderPath { get; }

    public ColorRGBA Color
    {
        get => _color;
        set => SetField(ref _color, value);
    }

    public float MetallicFactor
    {
        get => _metallicFactor;
        set => SetField(ref _metallicFactor, value);
    }

    public float RoughnessFactor
    {
        get => _roughnessFactor;
        set => SetField(ref _roughnessFactor, value);
    }

    public System.Numerics.Vector3 EmissiveFactor
    {
        get => _emissiveFactor;
        set => SetField(ref _emissiveFactor, value);
    }

    public float EmissiveIntensity
    {
        get => _emissiveIntensity;
        set => SetField(ref _emissiveIntensity, value);
    }

    public Texture AlbedoTexture
    {
        get => _albedoTexture;
        set => SetReferenceField(ref _albedoTexture, value);
    }

    public Texture MetallicRoughnessTexture
    {
        get => _metallicRoughnessTexture;
        set => SetReferenceField(ref _metallicRoughnessTexture, value);
    }

    public Texture NormalTexture
    {
        get => _normalTexture;
        set => SetReferenceField(ref _normalTexture, value);
    }

    public Texture EmissiveTexture
    {
        get => _emissiveTexture;
        set => SetReferenceField(ref _emissiveTexture, value);
    }

    public Texture OcclusionTexture
    {
        get => _occlusionTexture;
        set => SetReferenceField(ref _occlusionTexture, value);
    }

    public AlphaMode AlphaMode
    {
        get => _alphaMode;
        set => SetField(ref _alphaMode, value);
    }

    public float AlphaCutoff
    {
        get => _alphaCutoff;
        set => SetField(ref _alphaCutoff, value);
    }

    internal IMaterialResources? Resources
    {
        get
        {
            lock (_stateSync)
            {
                return _resources;
            }
        }
    }

    internal int ContentRevision
    {
        get
        {
            lock (_stateSync)
            {
                return _contentRevision;
            }
        }
    }

    internal int ResourceRevision
    {
        get
        {
            lock (_stateSync)
            {
                return _resourceRevision;
            }
        }
    }

    internal int LastBuiltContentRevision
    {
        get
        {
            lock (_stateSync)
            {
                return _lastBuiltContentRevision;
            }
        }
    }

    internal bool HasGpuResources
    {
        get
        {
            lock (_stateSync)
            {
                return _hasGpuResources;
            }
        }
    }

    internal bool ResourceRequestPending
    {
        get
        {
            lock (_stateSync)
            {
                return _resourceRequestPending;
            }
        }
    }

    internal void MarkResourceRequested()
    {
        lock (_stateSync)
        {
            _resourceRequestPending = true;
        }
    }

    internal void ClearResourceRequestPending()
    {
        lock (_stateSync)
        {
            _resourceRequestPending = false;
        }
    }

    internal void MarkGpuResourcesBuilt(IMaterialResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        lock (_stateSync)
        {
            _resources = resources;
            _hasGpuResources = true;
            _resourceRequestPending = false;
            _lastBuiltContentRevision = _contentRevision;
            _lastBuiltTextureRevisionHash = ComputeTextureRevisionHashUnsafe();
            _resourceRevision++;
        }
    }

    internal void MarkGpuResourcesDirty()
    {
        lock (_stateSync)
        {
            _resources = null;
            _hasGpuResources = false;
            _resourceRequestPending = false;
        }
    }

    internal bool NeedsGpuResourceRebuild()
    {
        lock (_stateSync)
        {
            if (_hasGpuResources == false || _resources is null)
            {
                return true;
            }

            return _lastBuiltContentRevision != _contentRevision ||
                   _lastBuiltTextureRevisionHash != ComputeTextureRevisionHashUnsafe();
        }
    }

    internal bool AreRequiredTextureResourcesReady()
    {
        lock (_stateSync)
        {
            return _albedoTexture?.HasGpuResources == true &&
                   _metallicRoughnessTexture?.HasGpuResources == true &&
                   _normalTexture?.HasGpuResources == true &&
                   _emissiveTexture?.HasGpuResources == true &&
                   _occlusionTexture?.HasGpuResources == true;
        }
    }

    internal Texture[] GetTrackedTextures()
    {
        lock (_stateSync)
        {
            return new[]
            {
                _albedoTexture,
                _metallicRoughnessTexture,
                _normalTexture,
                _emissiveTexture,
                _occlusionTexture
            };
        }
    }

    internal bool DependsOnTexture(Texture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        lock (_stateSync)
        {
            return ReferenceEquals(_albedoTexture, texture) ||
                   ReferenceEquals(_metallicRoughnessTexture, texture) ||
                   ReferenceEquals(_normalTexture, texture) ||
                   ReferenceEquals(_emissiveTexture, texture) ||
                   ReferenceEquals(_occlusionTexture, texture);
        }
    }

    private void MarkContentDirty()
    {
        lock (_stateSync)
        {
            _contentRevision++;
            _resources = null;
            _hasGpuResources = false;
        }
    }

    private void SetField<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        MarkContentDirty();
    }

    private void SetReferenceField<T>(ref T field, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ReferenceEquals(field, value))
        {
            return;
        }

        field = value;
        MarkContentDirty();
    }

    private int ComputeTextureRevisionHashUnsafe()
    {
        var hash = new HashCode();
        hash.Add(_albedoTexture?.ResourceRevision ?? 0);
        hash.Add(_metallicRoughnessTexture?.ResourceRevision ?? 0);
        hash.Add(_normalTexture?.ResourceRevision ?? 0);
        hash.Add(_emissiveTexture?.ResourceRevision ?? 0);
        hash.Add(_occlusionTexture?.ResourceRevision ?? 0);
        return hash.ToHashCode();
    }
}

public enum AlphaMode
{
    Opaque,
    AlphaTest,
    AlphaBlend
}
