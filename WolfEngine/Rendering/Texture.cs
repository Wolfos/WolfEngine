using System;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;

namespace WolfEngine;

[RuntimeAsset(AssetType.Texture2D, typeof(TextureAsset), typeof(ITextureRuntimeAssetResolver))]
public sealed class Texture
{
    private readonly object _resourceSync = new();
    private int _width;
    private int _height;
    private bool _isSrgb;
    private TextureFormat _format;
    private TextureMipData[] _mipLevels;
    private readonly bool _isRenderTarget;

    public Texture(string name, int width, int height, bool isSrgb, TextureFormat format, TextureMipData[] mipLevels)
        : this(name, width, height, isSrgb, format, mipLevels, isRenderTarget: false)
    {
    }

    private Texture(string name, int width, int height, bool isSrgb, TextureFormat format, TextureMipData[] mipLevels,
        bool isRenderTarget)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _width = width;
        _height = height;
        _isSrgb = isSrgb;
        _format = format;
        _mipLevels = mipLevels ?? throw new ArgumentNullException(nameof(mipLevels));
        _isRenderTarget = isRenderTarget;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
        }

        if (mipLevels.Length == 0)
        {
            throw new ArgumentException("Texture must contain at least one mip level.", nameof(mipLevels));
        }
    }

    public string Name { get; }
    public int Width => _width;
    public int Height => _height;
    public bool IsSrgb => _isSrgb;
    public TextureFormat Format => _format;
    public TextureMipData[] MipLevels => _mipLevels;
    public int MipCount => _mipLevels.Length;
    public bool IsRenderTarget => _isRenderTarget;

    /// <summary>
    /// Creates a logical texture whose storage is owned by a persistent GPU render target rather than CPU mip data.
    /// The render graph does not upload the placeholder mip; the surface renderer publishes the GPU resource.
    /// </summary>
    public static Texture CreateRenderTarget(string name, int width, int height, bool isSrgb = false,
        TextureFormat format = TextureFormat.Rgba8Unorm)
    {
        return new Texture(
            name,
            width,
            height,
            isSrgb,
            format,
            [new TextureMipData(width, height, Array.Empty<byte>())],
            isRenderTarget: true);
    }

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

    internal ITextureResources? MarkGpuResourcesCreated(ITextureResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        lock (_resourceSync)
        {
            var previousResources = ReferenceEquals(_resources, resources) ? null : _resources;
            _resources = resources;
            _hasGpuResources = true;
            _resourceRequestPending = false;
            _resourceRevision++;
            return previousResources;
        }
    }

    internal void MarkGpuResourcesDirty()
    {
        lock (_resourceSync)
        {
            _hasGpuResources = false;
            _resourceRequestPending = false;
            _resourceRevision++;
        }
    }

    public void ApplyTextureData(int width, int height, bool isSrgb, TextureFormat format, TextureMipData[] mipLevels)
    {
        if (_isRenderTarget)
        {
            throw new InvalidOperationException("Render-target texture contents are GPU-owned.");
        }
        ArgumentNullException.ThrowIfNull(mipLevels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
        }

        if (mipLevels.Length == 0)
        {
            throw new ArgumentException("Texture must contain at least one mip level.", nameof(mipLevels));
        }

        _width = width;
        _height = height;
        _isSrgb = isSrgb;
        _format = format;
        _mipLevels = mipLevels;
        MarkGpuResourcesDirty();
    }
}
