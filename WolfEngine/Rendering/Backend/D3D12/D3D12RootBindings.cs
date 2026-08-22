namespace WolfEngine.Rendering.Backend.D3D12;

internal static class D3D12RootBindings
{
	internal static class Graphics
	{
		internal const uint BindlessSrvTable = 0;
		internal const uint BindlessUavTable = 1;
		internal const uint BindlessSamplerTable = 2;
		internal const uint BindlessCountsCbv = 3;
		internal const uint CbvB0 = 4;
		internal const uint CbvB2 = 5;
		internal const uint CbvB3 = 6;
		internal const uint CbvB4 = 7;
		internal const uint CbvB14 = 8;
		internal const uint SrvT10 = 9;
		internal const uint SrvT11 = 10;
		internal const uint SrvT12 = 11;
		internal const uint SrvT13 = 12;
		internal const uint SrvT14 = 13;
		internal const uint SrvT15 = 14;
		internal const uint SrvT16 = 15;
		internal const uint CbvB16 = 16;
		internal const uint CbvB17 = 17;
		internal const uint CbvB18 = 18;
		internal const uint CbvB19 = 19;

		/// <summary>
		/// One dword of root constants at b1, holding the draw index that a shared-draw shader uses to
		/// find its entry in the draw args table. This is the only per-command argument in the indirect
		/// command signature: a root constant costs the command processor a single user-data write,
		/// where the root descriptors it replaced each cost an address write plus validation.
		/// </summary>
		internal const uint DrawIndexConstants = 20;

		/// <summary>
		/// Number of root parameters in the graphics root signature. Every register a graphics shader
		/// declares must appear here and in <see cref="TryGetGraphicsCbvIndex"/>/<see cref="TryGetGraphicsSrvIndex"/>,
		/// or its pipeline fails to create with a bare E_INVALIDARG.
		/// </summary>
		internal const uint ParameterCount = 21;

		/// <summary>Shader register backing <see cref="DrawIndexConstants"/>.</summary>
		internal const uint DrawIndexConstantsRegister = 1;
	}

	internal static class Compute
	{
		internal const uint BindlessSrvTable = 0;
		internal const uint BindlessUavTable = 1;
		internal const uint BindlessSamplerTable = 2;
		internal const uint BindlessCountsCbv = 3;
		internal const uint CbvB0 = 4;
		internal const uint CbvB1 = 5;
		internal const uint CbvB2 = 6;
		internal const uint CbvB11 = 7;
		internal const uint CbvB12 = 8;
		internal const uint UavU0 = 9;
		internal const uint SrvT2 = 21;
	}

	internal static bool TryGetGraphicsCbvIndex(uint register, out uint rootIndex)
	{
		switch (register)
		{
			case 0:
				rootIndex = Graphics.CbvB0;
				return true;
			case 2:
				rootIndex = Graphics.CbvB2;
				return true;
			case 3:
				rootIndex = Graphics.CbvB3;
				return true;
			case 4:
				rootIndex = Graphics.CbvB4;
				return true;
			case 14:
				rootIndex = Graphics.CbvB14;
				return true;
			case 16:
				rootIndex = Graphics.CbvB16;
				return true;
			// Screen-space decals bind their own vertex stream, which occupies buffer index 0 on Metal,
			// so their constant buffers sit above the shared graphics registers.
			case 17:
				rootIndex = Graphics.CbvB17;
				return true;
			case 18:
				rootIndex = Graphics.CbvB18;
				return true;
			case 19:
				rootIndex = Graphics.CbvB19;
				return true;
			case 27:
				rootIndex = Graphics.BindlessCountsCbv;
				return true;
			default:
				rootIndex = 0;
				return false;
		}
	}

	/// <summary>
	/// Resolves a shader register to an SRV root parameter. <c>BindConstantBuffer</c> consults this
	/// before <see cref="TryGetGraphicsCbvIndex"/>, so registers 10-16 always bind as SRVs: a graphics
	/// shader that declares a <c>cbuffer</c> in that range silently receives nothing. Declare constant
	/// buffers outside 10-16 (b14 and b16 exist as root parameters but are unreachable for this reason).
	/// </summary>
	internal static bool TryGetGraphicsSrvIndex(uint register, out uint rootIndex)
	{
		switch (register)
		{
			case 10:
				rootIndex = Graphics.SrvT10;
				return true;
			case 11:
				rootIndex = Graphics.SrvT11;
				return true;
			case 12:
				rootIndex = Graphics.SrvT12;
				return true;
			case 13:
				rootIndex = Graphics.SrvT13;
				return true;
			case 14:
				rootIndex = Graphics.SrvT14;
				return true;
			case 15:
				rootIndex = Graphics.SrvT15;
				return true;
			case 16:
				rootIndex = Graphics.SrvT16;
				return true;
			default:
				rootIndex = 0;
				return false;
		}
	}

	internal static bool TryGetComputeCbvIndex(uint register, out uint rootIndex)
	{
		switch (register)
		{
			case 0:
				rootIndex = Compute.CbvB0;
				return true;
			case 1:
				rootIndex = Compute.CbvB1;
				return true;
			case 2:
				rootIndex = Compute.CbvB2;
				return true;
			case 11:
				rootIndex = Compute.CbvB11;
				return true;
			case 12:
				rootIndex = Compute.CbvB12;
				return true;
			case 27:
				rootIndex = Compute.BindlessCountsCbv;
				return true;
			default:
				rootIndex = 0;
				return false;
		}
	}

	internal static bool TryGetComputeUavIndex(uint register, out uint rootIndex)
	{
		if (register <= 11)
		{
			rootIndex = Compute.UavU0 + register;
			return true;
		}

		rootIndex = 0;
		return false;
	}

	internal static bool TryGetComputeSrvIndex(uint register, out uint rootIndex)
	{
		if (register is >= 2 and <= 12)
		{
			rootIndex = Compute.SrvT2 + register - 2;
			return true;
		}

		rootIndex = 0;
		return false;
	}
}
