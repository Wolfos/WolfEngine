using SharpMetal.Foundation;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.Metal;

internal sealed class MetalDescriptorTable : IGfxDescriptorTable
{
	private const int MaxDescriptors = 16384;
	private const int MaxUavDescriptors = 16384;
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
	private readonly Stack<int> _freeSamplerIndices = new();
	private MTLArgumentEncoder _textureEncoder;
	private MTLArgumentEncoder _rwTextureEncoder;
	private MTLArgumentEncoder _samplerEncoder;
	private MTLBuffer _textureArgumentBuffer;
	private MTLBuffer _rwTextureArgumentBuffer;
	private MTLBuffer _samplerArgumentBuffer;
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
		MarkDirty();
		EncodeSrv(index);
		return new DescriptorHandle(DescriptorKind.ShaderResourceView, index);
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

		if (_cbvCount >= MaxDescriptors)
		{
			throw new InvalidOperationException("Metal CBV descriptor table is full.");
		}

		var index = _cbvCount++;
		_cbvBuffers[index] = metalBuffer.Buffer;
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
		MarkDirty();
		EncodeSampler(index);
		return new DescriptorHandle(DescriptorKind.Sampler, index);
	}

	public int SrvCount => _srvCount;

	public int UavCount => _uavCount;

	public int SamplerCount => _samplerCount;

	internal uint BindlessVersion => _bindlessVersion;

	public MTLTexture GetSrvTexture(int index) => _srvTextures[index]?.Texture ?? default;

	public MTLTexture GetUavTexture(int index) => _uavTextures[index]?.Texture ?? default;

	public MTLSamplerState GetSampler(int index) => _samplers[index];

	public MTLBuffer TextureArgumentBuffer => _textureArgumentBuffer;

	public MTLBuffer RWTextureArgumentBuffer => _rwTextureArgumentBuffer;

	public MTLBuffer SamplerArgumentBuffer => _samplerArgumentBuffer;

	internal void Free(DescriptorHandle handle)
	{
		if (handle.IsValid == false)
		{
			return;
		}

		var index = handle.Index;
		switch (handle.Kind)
		{
			case DescriptorKind.ShaderResourceView:
				if (index < 0 || index >= _srvCount || _srvTextures[index] is null)
				{
					return;
				}
				_srvTextures[index] = null;
				MarkDirty();
				_freeSrvIndices.Push(index);
				break;
			case DescriptorKind.UnorderedAccessView:
				if (index < 0 || index >= _uavCount || _uavTextures[index] is null)
				{
					return;
				}
				_uavTextures[index] = null;
				MarkDirty();
				_freeUavIndices.Push(index);
				break;
			case DescriptorKind.Sampler:
				if (index < 0 || index >= _samplerCount || _samplers[index].NativePtr == IntPtr.Zero)
				{
					return;
				}
				_samplers[index].Dispose();
				_samplers[index] = default;
				MarkDirty();
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
