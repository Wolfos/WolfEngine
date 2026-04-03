#nullable enable

using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Backend.D3D12;

internal sealed unsafe class D3D12DescriptorTable : IGfxDescriptorTable, IDisposable
{
	private const int MaxSrvDescriptors = 16384;
	private const int MaxUavDescriptors = 16384;
	private const int MaxCbvDescriptors = 65536;
	private const int MaxSamplerDescriptors = 2048;

	private readonly ComPtr<ID3D12Device> _device;
	private readonly ComPtr<ID3D12DescriptorHeap> _descriptorHeap;
	private readonly ComPtr<ID3D12DescriptorHeap> _samplerHeap;
	private readonly uint _descriptorIncrement;
	private readonly uint _samplerIncrement;
	private readonly int _uavBase;
	private readonly int _cbvBase;
	private readonly object _sync = new();
	private readonly Stack<int> _freeSrv = new();
	private readonly Stack<int> _freeUav = new();
	private readonly Stack<int> _freeCbv = new();
	private readonly Stack<int> _freeSampler = new();

	private int _srvCount;
	private int _uavCount;
	private int _cbvCount;
	private int _samplerCount;
	private bool _fallbackInitialized;
	private bool _fallbackInitializing;
	private BindlessFallbackHandles _fallbackHandles;

	private ComPtr<ID3D12Resource> _countsBuffer;
	private uint* _countsMapped;

	private ComPtr<ID3D12Resource> _fallbackTexture;
	private ComPtr<ID3D12Resource> _fallbackConstantBuffer;
	private ComPtr<ID3D12Resource> _fallbackUpload;

	public D3D12DescriptorTable(ComPtr<ID3D12Device> device)
	{
		_device = device;
		_uavBase = MaxSrvDescriptors;
		_cbvBase = MaxSrvDescriptors + MaxUavDescriptors;

		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.CbvSrvUav,
			NumDescriptors = (uint)(MaxSrvDescriptors + MaxUavDescriptors + MaxCbvDescriptors),
			Flags = DescriptorHeapFlags.ShaderVisible,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in heapDesc, out _descriptorHeap));

		var samplerDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Sampler,
			NumDescriptors = (uint)MaxSamplerDescriptors,
			Flags = DescriptorHeapFlags.ShaderVisible,
			NodeMask = 0
		};
		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in samplerDesc, out _samplerHeap));

		_descriptorIncrement = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);
		_samplerIncrement = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);

		CreateCountsBuffer();
	}

	public ComPtr<ID3D12DescriptorHeap> DescriptorHeap => _descriptorHeap;

	public ComPtr<ID3D12DescriptorHeap> SamplerHeap => _samplerHeap;

	public GpuDescriptorHandle SrvTableStart => GetGpuHandle(_descriptorHeap, 0, _descriptorIncrement);

	public GpuDescriptorHandle UavTableStart => GetGpuHandle(_descriptorHeap, _uavBase, _descriptorIncrement);

	public GpuDescriptorHandle SamplerTableStart => GetGpuHandle(_samplerHeap, 0, _samplerIncrement);

	public ulong CountsBufferGpuAddress =>
		_countsBuffer.Handle is null ? 0UL : _countsBuffer.Handle->GetGPUVirtualAddress();

	internal bool TryGetShaderResourceGpuHandle(uint packedHandle, out GpuDescriptorHandle gpuHandle)
	{
		const uint invalid = 0xFFFFFFFF;
		const uint kindShift = 30;
		const uint kindMask = 0b11;
		const uint indexMask = (1u << (int)kindShift) - 1u;

		if (packedHandle == invalid)
		{
			gpuHandle = default;
			return false;
		}

		var kind = (packedHandle >> (int)kindShift) & kindMask;
		if (kind != (uint)DescriptorKind.ShaderResourceView)
		{
			gpuHandle = default;
			return false;
		}

		var index = (int)(packedHandle & indexMask);
		if (index < 0 || index >= MaxSrvDescriptors)
		{
			gpuHandle = default;
			return false;
		}

		gpuHandle = GetGpuHandle(_descriptorHeap, index, _descriptorIncrement);
		return true;
	}

	public DescriptorHandle AllocateShaderResourceView(IGfxResource resource)
	{
		lock (_sync)
		{
			EnsureFallbackResources();
			var index = AllocateIndex(_freeSrv, ref _srvCount, MaxSrvDescriptors, "SRV");
			var cpuHandle = GetCpuHandle(_descriptorHeap, index, _descriptorIncrement);

			switch (resource)
			{
				case ID3D12BackendTexture texture:
				{
					var srvDesc = CreateTextureSrvDescription(texture, forceDepthSrv: false);
					_device.Handle->CreateShaderResourceView(texture.Resource, &srvDesc, cpuHandle);
					break;
				}
				case D3D12Buffer buffer:
				{
					var srvDesc = CreateBufferSrvDescription(buffer);
					_device.Handle->CreateShaderResourceView(buffer.Resource.Handle, &srvDesc, cpuHandle);
					break;
				}
				default:
					throw new InvalidOperationException("Resource was not created by the Direct3D12 backend.");
			}

			UpdateCountsBuffer();
			return new DescriptorHandle(DescriptorKind.ShaderResourceView, index);
		}
	}

	public DescriptorHandle AllocateDepthShaderResourceView(IGfxTexture texture)
	{
		lock (_sync)
		{
			EnsureFallbackResources();
			if (texture is not ID3D12BackendTexture d3dTexture)
			{
				throw new InvalidOperationException("Texture was not created by the Direct3D12 backend.");
			}

			var index = AllocateIndex(_freeSrv, ref _srvCount, MaxSrvDescriptors, "Depth SRV");
			var cpuHandle = GetCpuHandle(_descriptorHeap, index, _descriptorIncrement);
			var srvDesc = CreateTextureSrvDescription(d3dTexture, forceDepthSrv: true);
			_device.Handle->CreateShaderResourceView(d3dTexture.Resource, &srvDesc, cpuHandle);
			UpdateCountsBuffer();
			return new DescriptorHandle(DescriptorKind.ShaderResourceView, index);
		}
	}

	public DescriptorHandle AllocateUnorderedAccessView(IGfxResource resource)
	{
		lock (_sync)
		{
			EnsureFallbackResources();
			if (resource is not ID3D12BackendTexture texture)
			{
				throw new InvalidOperationException("Resource was not created by the Direct3D12 backend.");
			}

			var index = AllocateIndex(_freeUav, ref _uavCount, MaxUavDescriptors, "UAV");
			var cpuHandle = GetCpuHandle(_descriptorHeap, _uavBase + index, _descriptorIncrement);
			var uavDesc = CreateTextureUavDescription(texture);
			_device.Handle->CreateUnorderedAccessView(texture.Resource, null, &uavDesc, cpuHandle);
			UpdateCountsBuffer();
			return new DescriptorHandle(DescriptorKind.UnorderedAccessView, index);
		}
	}

	public DescriptorHandle AllocateConstantBufferView(IGfxBuffer buffer)
	{
		lock (_sync)
		{
			EnsureFallbackResources();
			if (buffer is not D3D12Buffer d3dBuffer || d3dBuffer.Resource.Handle is null)
			{
				throw new InvalidOperationException("Buffer was not created by the Direct3D12 backend.");
			}

			var index = AllocateIndex(_freeCbv, ref _cbvCount, MaxCbvDescriptors, "CBV");
			var cpuHandle = GetCpuHandle(_descriptorHeap, _cbvBase + index, _descriptorIncrement);
			var cbvDesc = new ConstantBufferViewDesc
			{
				BufferLocation = d3dBuffer.Resource.Handle->GetGPUVirtualAddress(),
				SizeInBytes = d3dBuffer.GetConstantBufferViewSizeInBytes()
			};
			_device.Handle->CreateConstantBufferView(cbvDesc, cpuHandle);
			return new DescriptorHandle(DescriptorKind.ConstantBufferView, index);
		}
	}

	public DescriptorHandle AllocateSampler(in SamplerDescriptor sampler)
	{
		lock (_sync)
		{
			EnsureFallbackResources();
			var index = AllocateIndex(_freeSampler, ref _samplerCount, MaxSamplerDescriptors, "Sampler");
			var cpuHandle = GetCpuHandle(_samplerHeap, index, _samplerIncrement);
			var samplerDesc = new Silk.NET.Direct3D12.SamplerDesc
			{
				Filter = ToFilter(sampler.Filter),
				AddressU = ToAddressMode(sampler.AddressU),
				AddressV = ToAddressMode(sampler.AddressV),
				AddressW = ToAddressMode(sampler.AddressW),
				MipLODBias = sampler.MipLodBias,
				MaxAnisotropy = (uint)Math.Clamp(sampler.MaxAnisotropy, 1.0f, 16.0f),
				ComparisonFunc = ComparisonFunc.Never,
				MinLOD = 0.0f,
				MaxLOD = float.MaxValue
			};
			_device.Handle->CreateSampler(in samplerDesc, cpuHandle);
			UpdateCountsBuffer();
			return new DescriptorHandle(DescriptorKind.Sampler, index);
		}
	}

	public BindlessFallbackHandles GetOrCreateFallbackHandles()
	{
		lock (_sync)
		{
			EnsureFallbackResources();
			return _fallbackHandles;
		}
	}

	public void Free(DescriptorHandle handle)
	{
		if (handle.IsValid == false)
		{
			return;
		}

		lock (_sync)
		{
			var index = handle.Index;
			if (index == 0)
			{
				return;
			}

			switch (handle.Kind)
			{
				case DescriptorKind.ShaderResourceView:
					_freeSrv.Push(index);
					break;
				case DescriptorKind.UnorderedAccessView:
					_freeUav.Push(index);
					break;
				case DescriptorKind.ConstantBufferView:
					_freeCbv.Push(index);
					break;
				case DescriptorKind.Sampler:
					_freeSampler.Push(index);
					break;
			}
		}
	}

	public void Dispose()
	{
		if (_countsBuffer.Handle is not null)
		{
			_countsBuffer.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
			_countsBuffer.Dispose();
			_countsBuffer = default;
		}

		if (_fallbackTexture.Handle is not null)
		{
			_fallbackTexture.Dispose();
			_fallbackTexture = default;
		}

		if (_fallbackConstantBuffer.Handle is not null)
		{
			_fallbackConstantBuffer.Dispose();
			_fallbackConstantBuffer = default;
		}

		if (_fallbackUpload.Handle is not null)
		{
			_fallbackUpload.Dispose();
			_fallbackUpload = default;
		}

		if (_descriptorHeap.Handle is not null)
		{
			_descriptorHeap.Dispose();
		}

		if (_samplerHeap.Handle is not null)
		{
			_samplerHeap.Dispose();
		}
	}

	private void EnsureFallbackResources()
	{
		if (_fallbackInitialized || _fallbackInitializing)
		{
			return;
		}

		_fallbackInitializing = true;
		try
		{
			CreateFallbackTexture();
			CreateFallbackConstantBuffer();
			CreateFallbackSampler();
			_fallbackInitialized = true;
			UpdateCountsBuffer();
		}
		finally
		{
			_fallbackInitializing = false;
		}
	}

	private void CreateFallbackTexture()
	{
		var textureDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = 1,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatR8G8B8A8Unorm,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.AllowUnorderedAccess
		};
		var heapProps = new HeapProperties(HeapType.Default);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&heapProps,
			HeapFlags.None,
			in textureDesc,
			ResourceStates.Common,
			null,
			out _fallbackTexture));

		var fallbackTexture = new FallbackTextureResource(_fallbackTexture.Handle);
		var srv = AllocateShaderResourceView(fallbackTexture);
		var uav = AllocateUnorderedAccessView(fallbackTexture);
		_fallbackHandles = new BindlessFallbackHandles(
			srv,
			uav,
			DescriptorHandle.Invalid,
			DescriptorHandle.Invalid);
	}

	private void CreateFallbackConstantBuffer()
	{
		var size = 256UL;
		var bufferDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = size,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		var heapProps = new HeapProperties(HeapType.Upload);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&heapProps,
			HeapFlags.None,
			in bufferDesc,
			ResourceStates.GenericRead,
			null,
			out _fallbackConstantBuffer));

		var fallbackBuffer = new D3D12Buffer(
			"__BindlessFallbackCBV",
			new BufferDescriptor(size, BufferUsage.Constant, BufferFlags.AllowShaderResource),
			_fallbackConstantBuffer,
			size,
			initialState: ResourceStates.GenericRead);
		var cbv = AllocateConstantBufferView(fallbackBuffer);
		_fallbackHandles = new BindlessFallbackHandles(
			_fallbackHandles.ShaderResourceView,
			_fallbackHandles.UnorderedAccessView,
			cbv,
			DescriptorHandle.Invalid);
	}

	private void CreateFallbackSampler()
	{
		var sampler = AllocateSampler(new SamplerDescriptor(
			FilterMode.Point,
			AddressMode.Clamp,
			AddressMode.Clamp,
			AddressMode.Clamp));
		_fallbackHandles = new BindlessFallbackHandles(
			_fallbackHandles.ShaderResourceView,
			_fallbackHandles.UnorderedAccessView,
			_fallbackHandles.ConstantBufferView,
			sampler);
	}

	private void CreateCountsBuffer()
	{
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = 256,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		var props = new HeapProperties(HeapType.Upload);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&props,
			HeapFlags.None,
			in desc,
			ResourceStates.GenericRead,
			null,
			out _countsBuffer));

		void* mapped = null;
		SilkMarshal.ThrowHResult(_countsBuffer.Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		_countsMapped = (uint*)mapped;
		UpdateCountsBuffer();
	}

	private void UpdateCountsBuffer()
	{
		if (_countsMapped is null)
		{
			return;
		}

		_countsMapped[0] = (uint)_srvCount;
		_countsMapped[1] = (uint)_uavCount;
		_countsMapped[2] = (uint)_samplerCount;
		_countsMapped[3] = 0;
	}

	private static int AllocateIndex(Stack<int> freeList, ref int count, int maxCount, string kind)
	{
		if (freeList.Count > 0)
		{
			return freeList.Pop();
		}

		if (count >= maxCount)
		{
			throw new InvalidOperationException($"D3D12 {kind} descriptor table is full.");
		}

		return count++;
	}

	private static CpuDescriptorHandle GetCpuHandle(
		ComPtr<ID3D12DescriptorHeap> heap,
		int index,
		uint descriptorIncrement)
	{
		var handle = heap.GetCPUDescriptorHandleForHeapStart();
		handle.Ptr += (nuint)(index * descriptorIncrement);
		return handle;
	}

	private static GpuDescriptorHandle GetGpuHandle(
		ComPtr<ID3D12DescriptorHeap> heap,
		int index,
		uint descriptorIncrement)
	{
		var handle = heap.GetGPUDescriptorHandleForHeapStart();
		handle.Ptr += (ulong)(index * descriptorIncrement);
		return handle;
	}

	private static ShaderResourceViewDesc CreateTextureSrvDescription(ID3D12BackendTexture texture, bool forceDepthSrv)
	{
		const uint defaultMapping = 5768; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
		var format = forceDepthSrv
			? Format.FormatR32Float
			: texture.Descriptor.Format switch
			{
				TextureFormat.Bgra8Unorm => texture.Descriptor.IsSrgb ? Format.FormatB8G8R8A8UnormSrgb : Format.FormatB8G8R8A8Unorm,
				TextureFormat.Rgba8Unorm => texture.Descriptor.IsSrgb ? Format.FormatR8G8B8A8UnormSrgb : Format.FormatR8G8B8A8Unorm,
				TextureFormat.Rg16Float => Format.FormatR16G16Float,
				TextureFormat.Rgba16Float => Format.FormatR16G16B16A16Float,
				TextureFormat.R32Float => Format.FormatR32Float,
				TextureFormat.D32Float => Format.FormatR32Float,
				TextureFormat.Bc5Unorm => Format.FormatBC5Unorm,
				TextureFormat.Bc7Unorm => texture.Descriptor.IsSrgb ? Format.FormatBC7UnormSrgb : Format.FormatBC7Unorm,
				_ => Format.FormatUnknown
			};
		var desc = new ShaderResourceViewDesc
		{
			Shader4ComponentMapping = defaultMapping,
			ViewDimension = SrvDimension.Texture2D,
			Format = format
		};
		desc.Anonymous.Texture2D = new Tex2DSrv
		{
			MostDetailedMip = 0,
			MipLevels = (uint)texture.Descriptor.MipLevels,
			ResourceMinLODClamp = 0.0f
		};
		return desc;
	}

	private static ShaderResourceViewDesc CreateBufferSrvDescription(D3D12Buffer buffer)
	{
		const uint defaultMapping = 5768; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
		var desc = new ShaderResourceViewDesc
		{
			Shader4ComponentMapping = defaultMapping,
			ViewDimension = SrvDimension.Buffer,
			Format = Format.FormatUnknown
		};
		desc.Anonymous.Buffer = new BufferSrv
		{
			FirstElement = 0,
			NumElements = (uint)Math.Max(1, buffer.SizeInBytes / 16),
			StructureByteStride = 16,
			Flags = BufferSrvFlags.None
		};
		return desc;
	}

	private static UnorderedAccessViewDesc CreateTextureUavDescription(ID3D12BackendTexture texture)
	{
		var format = texture.Descriptor.Format switch
		{
			TextureFormat.Bgra8Unorm => texture.Descriptor.IsSrgb ? Format.FormatB8G8R8A8UnormSrgb : Format.FormatB8G8R8A8Unorm,
			TextureFormat.Rgba8Unorm => texture.Descriptor.IsSrgb ? Format.FormatR8G8B8A8UnormSrgb : Format.FormatR8G8B8A8Unorm,
			TextureFormat.Rg16Float => Format.FormatR16G16Float,
			TextureFormat.Rgba16Float => Format.FormatR16G16B16A16Float,
			TextureFormat.R32Float => Format.FormatR32Float,
			TextureFormat.Bc5Unorm => Format.FormatBC5Unorm,
			TextureFormat.Bc7Unorm => texture.Descriptor.IsSrgb ? Format.FormatBC7UnormSrgb : Format.FormatBC7Unorm,
			_ => Format.FormatUnknown
		};
		var desc = new UnorderedAccessViewDesc
		{
			ViewDimension = UavDimension.Texture2D,
			Format = format
		};
		desc.Anonymous.Texture2D = new Tex2DUav
		{
			MipSlice = 0,
			PlaneSlice = 0
		};
		return desc;
	}

	private static Filter ToFilter(FilterMode mode)
	{
		return mode switch
		{
			FilterMode.Point => Filter.MinMagMipPoint,
			FilterMode.Bilinear => Filter.MinMagLinearMipPoint,
			FilterMode.Trilinear => Filter.MinMagMipLinear,
			FilterMode.Anisotropic => Filter.Anisotropic,
			_ => Filter.MinMagMipLinear
		};
	}

	private static TextureAddressMode ToAddressMode(AddressMode mode)
	{
		return mode switch
		{
			AddressMode.Clamp => TextureAddressMode.Clamp,
			AddressMode.Wrap => TextureAddressMode.Wrap,
			AddressMode.Mirror => TextureAddressMode.Mirror,
			AddressMode.Border => TextureAddressMode.Border,
			_ => TextureAddressMode.Clamp
		};
	}

	private sealed unsafe class FallbackTextureResource : ID3D12BackendTexture
	{
		public FallbackTextureResource(ID3D12Resource* resource)
		{
			Resource = resource;
		}

		public string? Name => "__BindlessFallbackTexture";

		public TextureDescriptor Descriptor => new(
			1,
			1,
			TextureFormat.Rgba8Unorm,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);

		public DescriptorHandle ShaderResourceView => DescriptorHandle.Invalid;
		public DescriptorHandle DepthShaderResourceView => DescriptorHandle.Invalid;
		public DescriptorHandle UnorderedAccessView => DescriptorHandle.Invalid;
		public ID3D12Resource* Resource { get; }
		public CpuDescriptorHandle? RenderTargetView => null;
		public CpuDescriptorHandle? DepthStencilView => null;
	}
}
