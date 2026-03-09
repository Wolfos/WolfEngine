using WolfEngine.Rendering.Abstraction;
using WolfEngine.AssetPipeline;

namespace WolfEngine;

[RuntimeAsset(AssetType.Texture2D, typeof(TextureAsset), typeof(ITextureRuntimeAssetResolver))]
public sealed class Texture
{
    private readonly object _resourceSync = new();
    private int _width;
    private int _height;
    private bool _isSrgb;
    private byte[] _pixelData;

    public Texture(string name, int width, int height, bool isSrgb, byte[] pixelData)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _width = width;
        _height = height;
        _isSrgb = isSrgb;
        _pixelData = pixelData ?? throw new ArgumentNullException(nameof(pixelData));
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
        }
    }

    public string Name { get; }
    public int Width => _width;
    public int Height => _height;
    public bool IsSrgb => _isSrgb;
    public byte[] PixelData => _pixelData;

    internal ITextureResources? Resources
    {
        get
        {
            lock (_resourceSync)
            {
                return _resources;
            }
        }
    }

    internal int ResourceRevision
    {
        get
        {
            lock (_resourceSync)
            {
                return _resourceRevision;
            }
        }
    }

    internal bool HasGpuResources
    {
        get
        {
            lock (_resourceSync)
            {
                return _hasGpuResources;
            }
        }
    }

    internal bool ResourceRequestPending
    {
        get
        {
            lock (_resourceSync)
            {
                return _resourceRequestPending;
            }
        }
    }

    private ITextureResources? _resources;
    private int _resourceRevision;
    private bool _hasGpuResources;
    private bool _resourceRequestPending;

    internal void MarkResourceRequested()
    {
        lock (_resourceSync)
        {
            _resourceRequestPending = true;
        }
    }

    internal void ClearResourceRequestPending()
    {
        lock (_resourceSync)
        {
            _resourceRequestPending = false;
        }
    }

    internal void MarkGpuResourcesCreated(ITextureResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        lock (_resourceSync)
        {
            _resources = resources;
            _hasGpuResources = true;
            _resourceRequestPending = false;
            _resourceRevision++;
        }
    }

    internal void MarkGpuResourcesDirty()
    {
        lock (_resourceSync)
        {
            _resources = null;
            _hasGpuResources = false;
            _resourceRequestPending = false;
        }
    }

    public void ApplyImportedTexture(int width, int height, bool isSrgb, byte[] pixelData)
    {
        ArgumentNullException.ThrowIfNull(pixelData);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
        }

        _width = width;
        _height = height;
        _isSrgb = isSrgb;
        _pixelData = pixelData;
        MarkGpuResourcesDirty();
    }
}
