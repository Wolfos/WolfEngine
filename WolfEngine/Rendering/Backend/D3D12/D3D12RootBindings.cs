#nullable enable

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
		internal const uint CbvB14 = 7;
		internal const uint SrvT10 = 8;
		internal const uint SrvT11 = 9;
		internal const uint SrvT12 = 10;
		internal const uint SrvT13 = 11;
		internal const uint SrvT14 = 12;
		internal const uint SrvT15 = 13;
		internal const uint SrvT16 = 14;
		internal const uint CbvB16 = 15;
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
			case 14:
				rootIndex = Graphics.CbvB14;
				return true;
			case 16:
				rootIndex = Graphics.CbvB16;
				return true;
			case 27:
				rootIndex = Graphics.BindlessCountsCbv;
				return true;
			default:
				rootIndex = 0;
				return false;
		}
	}

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
}
