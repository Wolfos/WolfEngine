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
    private TextureMipData[]? _mipLevels;
    private int _mipCount;
    private TextureDimension _dimension;
    private int _depth;
    private readonly bool _isRenderTarget;

    public Texture(string name, int width, int height, bool isSrgb, TextureFormat format, TextureMipData[] mipLevels)
        : this(name, width, height, isSrgb, format, mipLevels, isRenderTarget: false)
    {
    }

    private Texture(string name, int width, int height, bool isSrgb, TextureFormat format, TextureMipData[] mipLevels,
        bool isRenderTarget, TextureDimension dimension = TextureDimension.Texture2D, int depth = 1)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ArgumentNullException.ThrowIfNull(mipLevels);
        ValidateTextureData(width, height, mipLevels, dimension, depth);
        _width = width;
        _height = height;
        _isSrgb = isSrgb;
        _format = format;
        _mipLevels = mipLevels;
        _mipCount = mipLevels.Length;
        _dimension = dimension;
        _depth = depth;
        _isRenderTarget = isRenderTarget;
    }

    public string Name { get; }
    public int Width => _width;
    public int Height => _height;
    public int Depth => _depth;
    public TextureDimension Dimension => _dimension;
    public bool IsSrgb => _isSrgb;
    public TextureFormat Format => _format;
    public TextureMipData[] MipLevels => GetMipLevels();
    public int MipCount => _mipCount;
    public bool IsRenderTarget => _isRenderTarget;

    /// <summary>
    /// Creates a linear volume texture. Each mip's data holds its depth slices back to back.
    /// </summary>
    public static Texture Create3D(string name, int width, int height, int depth, TextureFormat format,
        TextureMipData[] mipLevels)
    {
        return new Texture(name, width, height, isSrgb: false, format, mipLevels, isRenderTarget: false,
            TextureDimension.Texture3D, depth);
    }

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

    /// <summary>
    /// Reads this texture's mip data again from immutable content, such as a cooked pack. Set only when that
    /// content cannot change; it lets the CPU copy go once the renderer has uploaded it.
    /// </summary>
    internal Func<TextureMipData[]>? MipDataSource { get; set; }

    /// <summary>Diagnostic: whether the CPU copy of the mip data is currently held.</summary>
    internal bool HasCpuMipData => Volatile.Read(ref _mipLevels) is not null;

    /// <summary>
    /// Drops the CPU copy of the mip data once the renderer has uploaded it, when it can be read again from
    /// <see cref="MipDataSource"/>.
    /// </summary>
    internal void ReleaseMipDataAfterUpload()
    {
        if (MipDataSource is null)
        {
            return;
        }

        lock (_resourceSync)
        {
            _mipLevels = null;
        }
    }

    private TextureMipData[] GetMipLevels()
    {
        var mipLevels = Volatile.Read(ref _mipLevels);
        if (mipLevels is not null)
        {
            return mipLevels;
        }

        lock (_resourceSync)
        {
            if (_mipLevels is not null)
            {
                return _mipLevels;
            }

            var source = MipDataSource
                ?? throw new InvalidOperationException($"Texture '{Name}' mip data was released and has no source to reload it from.");
            var reloaded = source();
            if (reloaded.Length != _mipCount)
            {
                throw new InvalidOperationException(
                    $"Texture '{Name}' reloaded {reloaded.Length} mips where {_mipCount} were uploaded.");
            }

            // Kept from then on: a reader that needed it once is likely to need it again.
            Volatile.Write(ref _mipLevels, reloaded);

            return reloaded;
        }
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

    internal void DetachGpuResources()
    {
        lock (_resourceSync)
        {
            _resources = null;
            _hasGpuResources = false;
            _resourceRequestPending = false;
            _resourceRevision++;
        }
    }

    public void ApplyTextureData(int width, int height, bool isSrgb, TextureFormat format, TextureMipData[] mipLevels,
        TextureDimension dimension = TextureDimension.Texture2D, int depth = 1)
    {
        if (_isRenderTarget)
        {
            throw new InvalidOperationException("Render-target texture contents are GPU-owned.");
        }
        ArgumentNullException.ThrowIfNull(mipLevels);
        ValidateTextureData(width, height, mipLevels, dimension, depth);

        _width = width;
        _height = height;
        _isSrgb = isSrgb;
        _format = format;
        lock (_resourceSync)
        {
            // The new data did not come from the old source, so it cannot be reloaded from there.
            MipDataSource = null;
            _mipLevels = mipLevels;
            _mipCount = mipLevels.Length;
        }

        _dimension = dimension;
        _depth = depth;
        MarkGpuResourcesDirty();
    }

    private static void ValidateTextureData(int width, int height, TextureMipData[] mipLevels,
        TextureDimension dimension, int depth)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
        }

        if (dimension == TextureDimension.Texture2D && depth != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "2D textures must have a depth of one.");
        }

        if (mipLevels.Length == 0)
        {
            throw new ArgumentException("Texture must contain at least one mip level.", nameof(mipLevels));
        }
    }
}
