using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Platform;

namespace WolfEngine.Rendering.Backend.Metal;

[SupportedOSPlatform("macos")]
internal sealed class MetalDescriptorTable : IGfxDescriptorTable
{
	private const int MaxDescriptors = 16384;
	private const int MaxUavDescriptors = 16384;
	internal const int BindlessArgumentBufferIndexCounts = 27;
	internal const int BindlessArgumentBufferIndexTextures = 28;
	internal const int BindlessArgumentBufferIndexRWTextures = 29;
	internal const int BindlessArgumentBufferIndexSamplers = 30;
	private readonly MTLDevice _device;
	private readonly MetalTexture[] _srvTextures = new MetalTexture[MaxDescriptors];
	private readonly MetalTexture[] _uavTextures = new MetalTexture[MaxDescriptors];
	private readonly MTLBuffer[] _cbvBuffers = new MTLBuffer[MaxDescriptors];
	private readonly MTLSamplerState[] _samplers = new MTLSamplerState[MaxDescriptors];
	private readonly MTLTexture[] _singleTexture = new MTLTexture[1];
	private readonly MTLSamplerState[] _singleSampler = new MTLSamplerState[1];
	private readonly Stack<int> _freeSrvIndices = new();
	private readonly Stack<int> _freeUavIndices = new();
	private readonly Stack<int> _freeCbvIndices = new();
	private readonly Stack<int> _freeSamplerIndices = new();
	private MTLArgumentEncoder _textureEncoder;
	private MTLArgumentEncoder _rwTextureEncoder;
	private MTLArgumentEncoder _samplerEncoder;
	private MTLBuffer _textureArgumentBuffer;
	private MTLBuffer _rwTextureArgumentBuffer;
	private MTLBuffer _samplerArgumentBuffer;
	private MTLBuffer _countBuffer;
	private int _srvCount;
	private int _uavCount;
	private int _cbvCount;
	private int _samplerCount;
	private uint _bindlessVersion;
	private uint _encodedSrvVersion;
	private uint _encodedUavVersion;
	private uint _encodedSamplerVersion;
	private bool _forceEncode;
	private int _forceEncodeFrames;
	private bool _fallbackHandlesInitialized;
	private BindlessFallbackHandles _fallbackHandles;
	private MetalTexture? _fallbackTexture;
	private MetalBuffer? _fallbackConstantBuffer;

	public MetalDescriptorTable(MTLDevice device)
	{
		_device = device;
	}

	public DescriptorHandle AllocateShaderResourceView(IGfxResource resource)
	{
		if (resource is not MetalTexture metalTexture)
		{
			throw new InvalidOperationException("Resource was not created by the Metal backend.");
		}

		int index;
		if (_freeSrvIndices.Count > 0)
		{
			index = _freeSrvIndices.Pop();
		}
		else
		{
			if (_srvCount >= MaxDescriptors)
			{
				throw new InvalidOperationException("Metal SRV descriptor table is full.");
			}

			index = _srvCount++;
		}
		_srvTextures[index] = metalTexture;
		UpdateCountBuffer();
		MarkDirty();
		EncodeSrv(index);
		return new DescriptorHandle(DescriptorKind.ShaderResourceView, index);
	}

	public DescriptorHandle AllocateDepthShaderResourceView(IGfxTexture texture)
	{
		if (texture is not MetalTexture metalTexture)
		{
			throw new InvalidOperationException("Texture was not created by the Metal backend.");
		}

		return AllocateShaderResourceView(metalTexture);
	}

	public DescriptorHandle AllocateUnorderedAccessView(IGfxResource resource)
	{
		if (resource is not MetalTexture metalTexture)
		{
			throw new InvalidOperationException("Resource was not created by the Metal backend.");
		}

		int index;
		if (_freeUavIndices.Count > 0)
		{
			index = _freeUavIndices.Pop();
		}
		else
		{
			if (_uavCount >= MaxUavDescriptors)
			{
				throw new InvalidOperationException("Metal UAV descriptor table is full.");
			}

			index = _uavCount++;
		}
		_uavTextures[index] = metalTexture;
		UpdateCountBuffer();
		MarkDirty();
		EncodeUav(index);
		return new DescriptorHandle(DescriptorKind.UnorderedAccessView, index);
	}

	public DescriptorHandle AllocateConstantBufferView(IGfxBuffer buffer)
	{
		if (buffer is not MetalBuffer metalBuffer)
		{
			throw new InvalidOperationException("Buffer was not created by the Metal backend.");
		}

		int index;
		if (_freeCbvIndices.Count > 0)
		{
			index = _freeCbvIndices.Pop();
		}
		else
		{
			if (_cbvCount >= MaxDescriptors)
			{
				throw new InvalidOperationException("Metal CBV descriptor table is full.");
			}

			index = _cbvCount++;
		}

		_cbvBuffers[index] = metalBuffer.Buffer;
		UpdateCountBuffer();
		return new DescriptorHandle(DescriptorKind.ConstantBufferView, index);
	}

	public DescriptorHandle AllocateSampler(in SamplerDescriptor sampler)
	{
		if (_freeSamplerIndices.Count == 0 && _samplerCount >= MaxDescriptors)
		{
			throw new InvalidOperationException("Metal sampler descriptor table is full.");
		}

		var descriptor = new MTLSamplerDescriptor();
		descriptor.MinFilter = sampler.Filter switch
		{
			FilterMode.Point => MTLSamplerMinMagFilter.Nearest,
			FilterMode.Bilinear => MTLSamplerMinMagFilter.Linear,
			FilterMode.Trilinear => MTLSamplerMinMagFilter.Linear,
			FilterMode.Anisotropic => MTLSamplerMinMagFilter.Linear,
			_ => MTLSamplerMinMagFilter.Linear
		};
		descriptor.MagFilter = descriptor.MinFilter;
		descriptor.MipFilter = sampler.Filter switch
		{
			FilterMode.Point => MTLSamplerMipFilter.NotMipmapped,
			FilterMode.Bilinear => MTLSamplerMipFilter.NotMipmapped,
			FilterMode.Trilinear => MTLSamplerMipFilter.Linear,
			FilterMode.Anisotropic => MTLSamplerMipFilter.Linear,
			_ => MTLSamplerMipFilter.Linear
		};
		descriptor.SAddressMode = ToAddressMode(sampler.AddressU);
		descriptor.TAddressMode = ToAddressMode(sampler.AddressV);
		descriptor.RAddressMode = ToAddressMode(sampler.AddressW);
		descriptor.NormalizedCoordinates = true;
		descriptor.MaxAnisotropy = (uint)Math.Clamp(sampler.MaxAnisotropy, 1.0f, 16.0f);
		descriptor.SupportArgumentBuffers = true;

		var samplerState = _device.NewSamplerState(descriptor);
		descriptor.Dispose();
		if (samplerState.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to create Metal sampler state.");
		}

		var index = _freeSamplerIndices.Count > 0 ? _freeSamplerIndices.Pop() : _samplerCount++;
		_samplers[index] = samplerState;
		UpdateCountBuffer();
		MarkDirty();
		EncodeSampler(index);
		return new DescriptorHandle(DescriptorKind.Sampler, index);
	}

	public BindlessFallbackHandles GetOrCreateFallbackHandles()
	{
		if (_fallbackHandlesInitialized)
		{
			return _fallbackHandles;
		}

		if (_srvCount != 0 || _uavCount != 0 || _cbvCount != 0 || _samplerCount != 0)
		{
			throw new InvalidOperationException("Bindless fallback handles must be initialized before other descriptor allocations.");
		}

		var textureDescriptor = new TextureDescriptor(
			1,
			1,
			TextureFormat.Rgba8Unorm,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
		var metalTextureDescriptor = new MTLTextureDescriptor
		{
			Width = 1,
			Height = 1,
			Depth = 1,
			MipmapLevelCount = 1,
			PixelFormat = MTLPixelFormat.RGBA8Unorm,
			TextureType = MTLTextureType.Type2D,
			StorageMode = MTLStorageMode.Managed,
			Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite
		};

		var texture = _device.NewTexture(metalTextureDescriptor);
		metalTextureDescriptor.Dispose();
		if (texture.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to create Metal bindless fallback texture.");
		}

		UploadFallbackTexture(texture);
		_fallbackTexture = new MetalTexture("__BindlessFallbackTexture", textureDescriptor, texture, this);
		var srvHandle = AllocateShaderResourceView(_fallbackTexture);
		var uavHandle = AllocateUnorderedAccessView(_fallbackTexture);
		_fallbackTexture.SetHandles(srvHandle, DescriptorHandle.Invalid, uavHandle);

		var fallbackBuffer = _device.NewBuffer(16, MTLResourceOptions.ResourceStorageModeShared);
		if (fallbackBuffer.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to create Metal bindless fallback constant buffer.");
		}

		_fallbackConstantBuffer = new MetalBuffer(
			"__BindlessFallbackBuffer",
			new BufferDescriptor(16, BufferUsage.Constant, BufferFlags.AllowShaderResource),
			fallbackBuffer);
		var cbvHandle = AllocateConstantBufferView(_fallbackConstantBuffer);

		var samplerHandle = AllocateSampler(new SamplerDescriptor(
			FilterMode.Point,
			AddressMode.Clamp,
			AddressMode.Clamp,
			AddressMode.Clamp));

		_fallbackHandles = new BindlessFallbackHandles(srvHandle, uavHandle, cbvHandle, samplerHandle);
		_fallbackHandlesInitialized = true;
		return _fallbackHandles;
	}

	internal void UpdateCountBuffer()
	{
		if (_countBuffer.NativePtr == IntPtr.Zero)
		{
			_countBuffer = _device.NewBuffer(16, MTLResourceOptions.ResourceStorageModeShared);
		}

		var counts = new uint[4];
		counts[0] = (uint)_srvCount;
		counts[1] = (uint)_uavCount;
		counts[2] = (uint)_samplerCount;
		counts[3] = 0;
		BufferHelper.CopyToBuffer(counts, _countBuffer);
	}

	public int SrvCount => _srvCount;

	public int UavCount => _uavCount;

	public int SamplerCount => _samplerCount;

	internal uint BindlessVersion => _bindlessVersion;

	internal int FreeSrvCount => _freeSrvIndices.Count;

	internal int FreeUavCount => _freeUavIndices.Count;

	internal int FreeSamplerCount => _freeSamplerIndices.Count;

	internal ulong TextureArgumentBufferBytes => _textureArgumentBuffer.NativePtr == IntPtr.Zero ? 0UL : _textureArgumentBuffer.Length;

	internal ulong RwTextureArgumentBufferBytes => _rwTextureArgumentBuffer.NativePtr == IntPtr.Zero ? 0UL : _rwTextureArgumentBuffer.Length;

	internal ulong SamplerArgumentBufferBytes => _samplerArgumentBuffer.NativePtr == IntPtr.Zero ? 0UL : _samplerArgumentBuffer.Length;

	public MTLTexture GetSrvTexture(int index) => _srvTextures[index]?.Texture ?? default;

	public MTLTexture GetUavTexture(int index) => _uavTextures[index]?.Texture ?? default;

	public MTLSamplerState GetSampler(int index) => _samplers[index];

	public MTLBuffer TextureArgumentBuffer => _textureArgumentBuffer;

	public MTLBuffer RWTextureArgumentBuffer => _rwTextureArgumentBuffer;

	public MTLBuffer SamplerArgumentBuffer => _samplerArgumentBuffer;

	public MTLBuffer CountBuffer => _countBuffer;

	public void Free(DescriptorHandle handle)
	{
		if (handle.IsValid == false)
		{
			return;
		}

		var index = handle.Index;
		switch (handle.Kind)
		{
			case DescriptorKind.ShaderResourceView:
				if (index == 0 || index < 0 || index >= _srvCount || _srvTextures[index] is null)
				{
					return;
				}
				_srvTextures[index] = _srvTextures[0];
				MarkDirty();
				EncodeSrv(index);
				_freeSrvIndices.Push(index);
				break;
			case DescriptorKind.UnorderedAccessView:
				if (index == 0 || index < 0 || index >= _uavCount || _uavTextures[index] is null)
				{
					return;
				}
				_uavTextures[index] = _uavTextures[0];
				MarkDirty();
				EncodeUav(index);
				_freeUavIndices.Push(index);
				break;
			case DescriptorKind.ConstantBufferView:
				if (index == 0 || index < 0 || index >= _cbvCount || _cbvBuffers[index].NativePtr == IntPtr.Zero)
				{
					return;
				}
				_cbvBuffers[index] = _cbvBuffers[0];
				_freeCbvIndices.Push(index);
				break;
			case DescriptorKind.Sampler:
				if (index == 0 || index < 0 || index >= _samplerCount || _samplers[index].NativePtr == IntPtr.Zero)
				{
					return;
				}
				_samplers[index].Dispose();
				_samplers[index] = _samplers[0];
				MarkDirty();
				EncodeSampler(index);
				_freeSamplerIndices.Push(index);
				break;
		}
	}

	public void SetArgumentEncoders(MTLArgumentEncoder textureEncoder, MTLArgumentEncoder rwTextureEncoder, MTLArgumentEncoder samplerEncoder)
	{
		_textureEncoder = textureEncoder;
		_rwTextureEncoder = rwTextureEncoder;
		_samplerEncoder = samplerEncoder;

		var forceEncode = _forceEncode || _forceEncodeFrames > 0;
		var needsEncodeSrv = forceEncode || _bindlessVersion != _encodedSrvVersion;
		var needsEncodeUav = forceEncode || _bindlessVersion != _encodedUavVersion;
		var needsEncodeSampler = forceEncode || _bindlessVersion != _encodedSamplerVersion;
		var encodedAny = false;

		if (_textureEncoder.NativePtr != IntPtr.Zero)
		{
			if (_textureEncoder.EncodedLength == 0)
			{
				_textureEncoder = default;
			}
			else
			{
				EnsureArgumentBuffer(ref _textureArgumentBuffer, _textureEncoder);
				if (needsEncodeSrv)
				{
					for (var i = 0; i < _srvCount; i++)
					{
						EncodeSrv(i);
					}

					_encodedSrvVersion = _bindlessVersion;
					encodedAny = true;
				}
			}
		}

		if (_rwTextureEncoder.NativePtr != IntPtr.Zero)
		{
			if (_rwTextureEncoder.EncodedLength == 0)
			{
				_rwTextureEncoder = default;
			}
			else
			{
				EnsureArgumentBuffer(ref _rwTextureArgumentBuffer, _rwTextureEncoder);
				if (needsEncodeUav)
				{
					for (var i = 0; i < _uavCount; i++)
					{
						EncodeUav(i);
					}

					_encodedUavVersion = _bindlessVersion;
					encodedAny = true;
				}
			}
		}

		if (_samplerEncoder.NativePtr != IntPtr.Zero)
		{
			if (_samplerEncoder.EncodedLength == 0)
			{
				_samplerEncoder = default;
			}
			else
			{
				EnsureArgumentBuffer(ref _samplerArgumentBuffer, _samplerEncoder);
				if (needsEncodeSampler)
				{
					for (var i = 0; i < _samplerCount; i++)
					{
						EncodeSampler(i);
					}

					_encodedSamplerVersion = _bindlessVersion;
					encodedAny = true;
				}
			}
		}

		if (encodedAny)
		{
			_forceEncode = false;
			if (_forceEncodeFrames > 0)
			{
				_forceEncodeFrames--;
			}
		}
	}

	private static unsafe void UploadFallbackTexture(MTLTexture texture)
	{
		var color = stackalloc byte[4];
		color[0] = 255;
		color[1] = 0;
		color[2] = 255;
		color[3] = 255;

		var region = new MTLRegion
		{
			origin = new MTLOrigin { x = 0, y = 0, z = 0 },
			size = new MTLSize { width = 1, height = 1, depth = 1 }
		};

		texture.ReplaceRegion(region, 0, (nint)color, 4);
	}

	private static MTLSamplerAddressMode ToAddressMode(AddressMode mode) => mode switch
	{
		AddressMode.Clamp => MTLSamplerAddressMode.ClampToEdge,
		AddressMode.Wrap => MTLSamplerAddressMode.Repeat,
		AddressMode.Mirror => MTLSamplerAddressMode.MirrorRepeat,
		AddressMode.Border => MTLSamplerAddressMode.ClampToBorderColor,
		_ => MTLSamplerAddressMode.ClampToEdge
	};

	internal void MarkDirty()
	{
		_bindlessVersion++;
	}

	internal void ForceEncode()
	{
		_forceEncode = true;
	}

	internal void ForceEncodeForFrames(int count)
	{
		if (count <= 0)
		{
			return;
		}

		_forceEncodeFrames = Math.Max(_forceEncodeFrames, count);
	}

	private void EncodeSrv(int index)
	{
		if (_textureEncoder.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var srvTexture = _srvTextures[index];
		if (srvTexture is null || srvTexture.IsDisposed)
		{
			return;
		}

		var texture = srvTexture.Texture;
		if (texture.NativePtr == IntPtr.Zero)
		{
			return;
		}

		_singleTexture[0] = texture;
		_textureEncoder.SetTextures(_singleTexture, new NSRange { location = (ulong)index, length = 1 });
	}

	private void EncodeUav(int index)
	{
		if (_rwTextureEncoder.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var uavTexture = _uavTextures[index];
		if (uavTexture is null || uavTexture.IsDisposed)
		{
			return;
		}

		var texture = uavTexture.Texture;
		if (texture.NativePtr == IntPtr.Zero)
		{
			return;
		}

		_singleTexture[0] = texture;
		_rwTextureEncoder.SetTextures(_singleTexture, new NSRange { location = (ulong)index, length = 1 });
	}

	private void EncodeSampler(int index)
	{
		if (_samplerEncoder.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var sampler = _samplers[index];
		if (sampler.NativePtr == IntPtr.Zero)
		{
			return;
		}

		_singleSampler[0] = sampler;
		_samplerEncoder.SetSamplerStates(_singleSampler, new NSRange { location = (ulong)index, length = 1 });
	}

	private void EnsureArgumentBuffer(ref MTLBuffer buffer, MTLArgumentEncoder encoder)
	{
		var requiredSize = encoder.EncodedLength;
		if (buffer.NativePtr == IntPtr.Zero || buffer.Length < requiredSize)
		{
			if (buffer.NativePtr != IntPtr.Zero)
			{
				buffer.Dispose();
			}
			buffer = _device.NewBuffer(requiredSize, MTLResourceOptions.ResourceStorageModeShared);
			if (buffer.NativePtr == IntPtr.Zero)
			{
				throw new InvalidOperationException("Failed to create Metal bindless argument buffer.");
			}
		}

		encoder.SetArgumentBuffer(buffer, 0);
	}
}
