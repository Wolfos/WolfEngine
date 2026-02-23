#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SharpMetal.Metal;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;

namespace WolfEngine.Rendering;

public sealed class BindlessResourceRegistry
{
	private IGfxDevice? _device;
	private readonly Dictionary<IGfxResource, DescriptorHandle> _srvHandles = new(new ReferenceComparer<IGfxResource>());
	private readonly Dictionary<IGfxTexture, DescriptorHandle> _depthSrvHandles = new(new ReferenceComparer<IGfxTexture>());
	private readonly Dictionary<IGfxResource, DescriptorHandle> _uavHandles = new(new ReferenceComparer<IGfxResource>());
	private readonly Dictionary<IGfxBuffer, DescriptorHandle> _cbvHandles = new(new ReferenceComparer<IGfxBuffer>());
	private readonly Dictionary<SamplerDescriptor, DescriptorHandle> _samplerHandles = new(new SamplerDescriptorComparer());
	private DescriptorHandle _errorTextureHandle = DescriptorHandle.Invalid;
	private DescriptorHandle _errorSamplerHandle = DescriptorHandle.Invalid;
	private DescriptorHandle _errorBufferHandle = DescriptorHandle.Invalid;
	private IGfxTexture? _errorTexture;
	private IGfxBuffer? _errorBuffer;
	private bool _loggedSrvTableFull;
	private bool _loggedUavTableFull;
	private bool _loggedCbvTableFull;
	private bool _loggedSamplerTableFull;

	public void EnsureInitialized(IGfxDevice device)
	{
		if (device is null)
		{
			throw new ArgumentNullException(nameof(device));
		}

		if (ReferenceEquals(_device, device))
		{
			return;
		}

		_device = device;
		_srvHandles.Clear();
		_depthSrvHandles.Clear();
		_cbvHandles.Clear();
		_uavHandles.Clear();
		_samplerHandles.Clear();
		_loggedSrvTableFull = false;
		_loggedUavTableFull = false;
		_loggedCbvTableFull = false;
		_loggedSamplerTableFull = false;
		CreateErrorResources(device);
	}

	public DescriptorHandle GetTextureHandle(ITextureResources? textureResources)
	{
		if (textureResources is null || textureResources.ShaderResourceView.IsValid == false)
		{
			return _errorTextureHandle;
		}

		return textureResources.ShaderResourceView;
	}

	public DescriptorHandle GetTextureHandle(IGfxTexture? texture)
	{
		if (texture is null)
		{
			return _errorTextureHandle;
		}

		return texture.ShaderResourceView.IsValid ? texture.ShaderResourceView : _errorTextureHandle;
	}

	public DescriptorHandle RegisterTexture(IGfxTexture texture)
	{
		if (_device is null)
		{
			throw new InvalidOperationException("Bindless registry is not initialized.");
		}

		if (_srvHandles.TryGetValue(texture, out var handle))
		{
			return handle;
		}

		try
		{
			handle = texture.ShaderResourceView.IsValid
				? texture.ShaderResourceView
				: _device.GlobalTable.AllocateShaderResourceView(texture);
		}
		catch (InvalidOperationException ex) when (IsTableFullException(ex))
		{
			LogTableFullOnce(
				ref _loggedSrvTableFull,
				"SRV",
				GpuDrawResources.MaxDrawCount);
			return _errorTextureHandle;
		}
		_srvHandles[texture] = handle;
		return handle.IsValid ? handle : _errorTextureHandle;
	}

	public DescriptorHandle RegisterDepthTexture(IGfxTexture texture)
	{
		if (_device is null)
		{
			throw new InvalidOperationException("Bindless registry is not initialized.");
		}

		if (_depthSrvHandles.TryGetValue(texture, out var handle))
		{
			return handle;
		}

		try
		{
			handle = texture.DepthShaderResourceView.IsValid
				? texture.DepthShaderResourceView
				: _device.GlobalTable.AllocateDepthShaderResourceView(texture);
		}
		catch (InvalidOperationException ex) when (IsTableFullException(ex))
		{
			LogTableFullOnce(
				ref _loggedSrvTableFull,
				"Depth SRV",
				GpuDrawResources.MaxDrawCount);
			return _errorTextureHandle;
		}

		_depthSrvHandles[texture] = handle;
		return handle.IsValid ? handle : _errorTextureHandle;
	}

	public DescriptorHandle RegisterRwTexture(IGfxTexture texture)
	{
		if (_device is null)
		{
			throw new InvalidOperationException("Bindless registry is not initialized.");
		}

		if (_uavHandles.TryGetValue(texture, out var handle))
		{
			return handle;
		}

		try
		{
			handle = texture.UnorderedAccessView.IsValid
				? texture.UnorderedAccessView
				: _device.GlobalTable.AllocateUnorderedAccessView(texture);
		}
		catch (InvalidOperationException ex) when (IsTableFullException(ex))
		{
			LogTableFullOnce(
				ref _loggedUavTableFull,
				"UAV",
				GpuDrawResources.MaxDrawCount);
			return _errorTextureHandle;
		}
		_uavHandles[texture] = handle;
		return handle.IsValid ? handle : _errorTextureHandle;
	}

	public DescriptorHandle RegisterBuffer(IGfxBuffer buffer)
	{
		if (_device is null)
		{
			throw new InvalidOperationException("Bindless registry is not initialized.");
		}

		if (_cbvHandles.TryGetValue(buffer, out var handle))
		{
			return handle;
		}

		try
		{
			handle = _device.GlobalTable.AllocateConstantBufferView(buffer);
		}
		catch (InvalidOperationException ex) when (IsTableFullException(ex))
		{
			LogTableFullOnce(
				ref _loggedCbvTableFull,
				"CBV",
				GpuDrawResources.MaxDrawCount);
			return _errorBufferHandle;
		}
		_cbvHandles[buffer] = handle;
		return handle.IsValid ? handle : _errorBufferHandle;
	}

	public DescriptorHandle GetSamplerHandle(in SamplerDescriptor descriptor)
	{
		if (_device is null)
		{
			throw new InvalidOperationException("Bindless registry is not initialized.");
		}

		if (_samplerHandles.TryGetValue(descriptor, out var handle))
		{
			return handle;
		}

		try
		{
			handle = _device.GlobalTable.AllocateSampler(descriptor);
		}
		catch (InvalidOperationException ex) when (IsTableFullException(ex))
		{
			LogTableFullOnce(
				ref _loggedSamplerTableFull,
				"Sampler",
				GpuDrawResources.MaxDrawCount);
			return _errorSamplerHandle;
		}
		_samplerHandles[descriptor] = handle;
		return handle.IsValid ? handle : _errorSamplerHandle;
	}

	public DescriptorHandle ErrorTextureHandle => _errorTextureHandle;

	public DescriptorHandle ErrorSamplerHandle => _errorSamplerHandle;

	public DescriptorHandle ErrorBufferHandle => _errorBufferHandle;

	public void UnregisterBuffer(IGfxBuffer? buffer)
	{
		if (buffer is null)
		{
			return;
		}

		if (_cbvHandles.TryGetValue(buffer, out var handle) == false)
		{
			return;
		}

		_cbvHandles.Remove(buffer);
		if (handle.IsValid && _device?.GlobalTable is MetalDescriptorTable metalTable)
		{
			metalTable.Free(handle);
		}
	}

	public DescriptorHandle GetDepthTextureHandle(IGfxTexture? texture)
	{
		if (texture is null)
		{
			return _errorTextureHandle;
		}

		if (texture.DepthShaderResourceView.IsValid)
		{
			return texture.DepthShaderResourceView;
		}

		if (_depthSrvHandles.TryGetValue(texture, out var handle))
		{
			return handle;
		}

		return _errorTextureHandle;
	}

	private void CreateErrorResources(IGfxDevice device)
	{
		_errorTextureHandle = DescriptorHandle.Invalid;
		_errorSamplerHandle = DescriptorHandle.Invalid;
		_errorBufferHandle = DescriptorHandle.Invalid;

		_errorTexture = device.CreateTexture(new TextureDescriptor(
			1,
			1,
			TextureFormat.Rgba8Unorm,
			TextureUsage.ShaderResource));
		_errorTextureHandle = RegisterTexture(_errorTexture);

		_errorBuffer = device.CreateBuffer(new BufferDescriptor(
			16,
			BufferUsage.Constant,
			BufferFlags.AllowShaderResource));
		_errorBufferHandle = RegisterBuffer(_errorBuffer);

		var sampler = new SamplerDescriptor(FilterMode.Point, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
		_errorSamplerHandle = GetSamplerHandle(sampler);

		if (_errorTexture is MetalTexture metalTexture)
		{
			UploadErrorTexture(metalTexture.Texture);
		}
	}

	private static unsafe void UploadErrorTexture(MTLTexture texture)
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

	private static bool IsTableFullException(InvalidOperationException exception)
	{
		return exception.Message.Contains("descriptor table is full", StringComparison.OrdinalIgnoreCase);
	}

	private static void LogTableFullOnce(ref bool flag, string tableKind, int drawLimit)
	{
		if (flag)
		{
			return;
		}

		flag = true;
		Console.WriteLine(
			$"Bindless {tableKind} table is full. Falling back to error resources for new bindings. Draw limit={drawLimit}.");
	}

	private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
	{
		public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

		public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
	}

	private sealed class SamplerDescriptorComparer : IEqualityComparer<SamplerDescriptor>
	{
		public bool Equals(SamplerDescriptor x, SamplerDescriptor y)
		{
			return x.Filter == y.Filter &&
			       x.AddressU == y.AddressU &&
			       x.AddressV == y.AddressV &&
			       x.AddressW == y.AddressW &&
			       x.MipLodBias.Equals(y.MipLodBias) &&
			       x.MaxAnisotropy.Equals(y.MaxAnisotropy);
		}

		public int GetHashCode(SamplerDescriptor obj)
		{
			var hash = (int)obj.Filter;
			hash = (hash * 397) ^ (int)obj.AddressU;
			hash = (hash * 397) ^ (int)obj.AddressV;
			hash = (hash * 397) ^ (int)obj.AddressW;
			hash = (hash * 397) ^ obj.MipLodBias.GetHashCode();
			hash = (hash * 397) ^ obj.MaxAnisotropy.GetHashCode();
			return hash;
		}
	}
}
