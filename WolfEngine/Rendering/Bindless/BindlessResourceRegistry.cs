#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using WolfEngine.Rendering.Abstraction;

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
	private DescriptorHandle _errorRwTextureHandle = DescriptorHandle.Invalid;
	private DescriptorHandle _errorSamplerHandle = DescriptorHandle.Invalid;
	private DescriptorHandle _errorBufferHandle = DescriptorHandle.Invalid;
	private bool _loggedSrvTableFull;
	private bool _loggedUavTableFull;
	private bool _loggedCbvTableFull;
	private bool _loggedSamplerTableFull;
	private bool _loggedDepthSrvFallback;

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
		_loggedDepthSrvFallback = false;
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
			var currentSrv = texture.ShaderResourceView;
			if (currentSrv.IsValid && currentSrv.Value != handle.Value)
			{
				_srvHandles[texture] = currentSrv;
				return currentSrv;
			}

			if (handle.IsValid)
			{
				return handle;
			}
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
			var currentDepthSrv = texture.DepthShaderResourceView;
			if (currentDepthSrv.IsValid && currentDepthSrv.Value != handle.Value)
			{
				_depthSrvHandles[texture] = currentDepthSrv;
				return currentDepthSrv;
			}

			if (handle.IsValid)
			{
				return handle;
			}
		}

		try
		{
			if (texture.DepthShaderResourceView.IsValid)
			{
				handle = texture.DepthShaderResourceView;
			}
			else if (texture.ShaderResourceView.IsValid)
			{
				handle = texture.ShaderResourceView;
				LogDepthSrvFallbackOnce();
			}
			else
			{
				handle = _device.GlobalTable.AllocateDepthShaderResourceView(texture);
			}
		}
		catch (InvalidOperationException ex) when (IsTableFullException(ex))
		{
			LogTableFullOnce(
				ref _loggedSrvTableFull,
				"Depth SRV",
				GpuDrawResources.MaxDrawCount);
			return _errorTextureHandle;
		}

		if (handle.IsValid == false && texture.ShaderResourceView.IsValid)
		{
			handle = texture.ShaderResourceView;
			LogDepthSrvFallbackOnce();
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

		// D3D12 rejects a UAV over a resource created without ALLOW_UNORDERED_ACCESS, but only at the
		// point the command list is closed, as a bare E_INVALIDARG with no indication of which resource.
		// Metal accepts the write and produces undefined results. Checking here fails both backends
		// identically, at the call site that made the mistake.
		if ((texture.Descriptor.Usage & TextureUsage.UnorderedAccess) == 0)
		{
			throw new InvalidOperationException(
				$"A {texture.Descriptor.Width}x{texture.Descriptor.Height} {texture.Descriptor.Format} texture " +
				$"(usage {texture.Descriptor.Usage}) cannot be bound as a read-write texture because it was not " +
				"created with TextureUsage.UnorderedAccess.");
		}

		if (_uavHandles.TryGetValue(texture, out var handle))
		{
			var currentUav = texture.UnorderedAccessView;
			if (currentUav.IsValid && currentUav.Value != handle.Value)
			{
				_uavHandles[texture] = currentUav;
				return currentUav;
			}

			if (handle.IsValid)
			{
				return handle;
			}
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
			return _errorRwTextureHandle;
		}
		_uavHandles[texture] = handle;
		return handle.IsValid ? handle : _errorRwTextureHandle;
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
		if (handle.IsValid)
		{
			_device?.GlobalTable.Free(handle);
		}
	}

	private void CreateErrorResources(IGfxDevice device)
	{
		_errorTextureHandle = DescriptorHandle.Invalid;
		_errorRwTextureHandle = DescriptorHandle.Invalid;
		_errorSamplerHandle = DescriptorHandle.Invalid;
		_errorBufferHandle = DescriptorHandle.Invalid;

		var fallback = device.GlobalTable.GetOrCreateFallbackHandles();
		_errorTextureHandle = fallback.ShaderResourceView;
		_errorRwTextureHandle = fallback.UnorderedAccessView.IsValid
			? fallback.UnorderedAccessView
			: fallback.ShaderResourceView;
		_errorBufferHandle = fallback.ConstantBufferView;
		_errorSamplerHandle = fallback.Sampler;
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

	private void LogDepthSrvFallbackOnce()
	{
		if (_loggedDepthSrvFallback)
		{
			return;
		}

		_loggedDepthSrvFallback = true;
		Console.WriteLine("Bindless depth texture fallback: using regular SRV handle.");
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
