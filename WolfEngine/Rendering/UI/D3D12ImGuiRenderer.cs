using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.D3D12;
using D3DRange = Silk.NET.Direct3D12.Range;
using D3DVertexBufferView = Silk.NET.Direct3D12.VertexBufferView;
using D3DIndexBufferView = Silk.NET.Direct3D12.IndexBufferView;
using D3DViewport = Silk.NET.Direct3D12.Viewport;
using D3DRect = Silk.NET.Maths.Box2D<int>;
using D3DFillMode = Silk.NET.Direct3D12.FillMode;
using D3DCullMode = Silk.NET.Direct3D12.CullMode;
using D3DPrimitiveTopologyType = Silk.NET.Direct3D12.PrimitiveTopologyType;

namespace WolfEngine.Rendering.UI;

internal unsafe sealed class D3D12ImGuiRenderer : IImGuiRenderer
{
	private readonly ComPtr<ID3D12Device> _device;
	private readonly IShaderCompiler _shaderCompiler;

	private ComPtr<ID3D12DescriptorHeap> _srvHeap;
	private GpuDescriptorHandle _srvGpuHandle;
	private ComPtr<ID3D12Resource> _fontTexture;
	private ComPtr<ID3D12PipelineState> _pipelineState;
	private ComPtr<ID3D12RootSignature> _rootSignature;
	private ComPtr<ID3D12Resource> _vertexBuffer;
	private ComPtr<ID3D12Resource> _indexBuffer;
	private int _vertexBufferSize;
	private int _indexBufferSize;
	private bool _fontUploaded;

	public D3D12ImGuiRenderer(ComPtr<ID3D12Device> device, IShaderCompiler shaderCompiler)
	{
		_device = device;
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
	}

	public void EnsureResources(IGfxDevice device, UiFrameData frame)
	{
		if (_pipelineState.Handle is null)
		{
			CreatePipeline();
		}

		if (_fontUploaded == false && frame.HasFontAtlas)
		{
			CreateFontTexture(frame.FontAtlas);
			CreateSrv();
			_fontUploaded = true;
		}
	}

	public void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture renderTarget)
	{
		if (_pipelineState.Handle is null || _fontTexture.Handle is null || frame.Commands.Length == 0)
		{
			return;
		}

		var backbuffer = renderTarget as ID3D12BackendTexture
		                 ?? throw new InvalidOperationException("Render target was not a D3D12 texture.");

		EnsureBuffers(frame);

		byte* vertexMapped = null;
		byte* indexMapped = null;

		SilkMarshal.ThrowHResult(_vertexBuffer.Map(0, (D3DRange*) null, (void**) &vertexMapped));
		SilkMarshal.ThrowHResult(_indexBuffer.Map(0, (D3DRange*) null, (void**) &indexMapped));

		fixed (ImDrawVert* srcVerts = frame.Vertices)
		{
			var size = frame.Vertices.Length * Unsafe.SizeOf<ImDrawVert>();
			Buffer.MemoryCopy(srcVerts, vertexMapped, size, size);
		}

		fixed (ushort* srcIndices = frame.Indices)
		{
			var size = frame.Indices.Length * sizeof(ushort);
			Buffer.MemoryCopy(srcIndices, indexMapped, size, size);
		}

		_vertexBuffer.Unmap(0, (D3DRange*) null);
		_indexBuffer.Unmap(0, (D3DRange*) null);

		var commandList = (context.CommandList as Backend.D3D12.D3D12CommandList)
		                  ?? throw new InvalidOperationException("ImGui renderer requires D3D12 command list.");
		var native = (ID3D12GraphicsCommandList*) commandList.CommandList.Handle;

		var rtvHandle = backbuffer.RenderTargetView
		                ?? throw new InvalidOperationException("Backbuffer missing RTV.");
		native->OMSetRenderTargets(1, &rtvHandle, 0, null);

		var viewport = new D3DViewport
		{
			TopLeftX = 0,
			TopLeftY = 0,
			Width = frame.FramebufferSize.X,
			Height = frame.FramebufferSize.Y,
			MinDepth = 0.0f,
			MaxDepth = 1.0f
		};
		native->RSSetViewports(1, &viewport);

		ID3D12DescriptorHeap* heaps = _srvHeap.Handle;
		native->SetDescriptorHeaps(1, &heaps);

		native->SetGraphicsRootSignature(_rootSignature.Handle);
		native->SetPipelineState(_pipelineState.Handle);

		native->IASetPrimitiveTopology(Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

		var vbView = new D3DVertexBufferView
		{
			BufferLocation = _vertexBuffer.GetGPUVirtualAddress(),
			StrideInBytes = (uint) Unsafe.SizeOf<ImDrawVert>(),
			SizeInBytes = (uint) _vertexBufferSize
		};

		var ibView = new D3DIndexBufferView
		{
			BufferLocation = _indexBuffer.GetGPUVirtualAddress(),
			SizeInBytes = (uint) _indexBufferSize,
			Format = Format.FormatR16Uint
		};

		native->IASetVertexBuffers(0, 1, &vbView);
		native->IASetIndexBuffer(&ibView);

		fixed (GpuDescriptorHandle* srvHandle = &_srvGpuHandle)
		{
			native->SetGraphicsRootDescriptorTable(0, *srvHandle);
		}

		var L = frame.DisplayPos.X;
		var R = frame.DisplayPos.X + frame.DisplaySize.X;
		var T = frame.DisplayPos.Y;
		var B = frame.DisplayPos.Y + frame.DisplaySize.Y;
		Span<float> projection = stackalloc float[16];
		projection[0] = 2.0f / (R - L);
		projection[1] = 0.0f;
		projection[2] = 0.0f;
		projection[3] = 0.0f;

		projection[4] = 0.0f;
		projection[5] = 2.0f / (T - B);
		projection[6] = 0.0f;
		projection[7] = 0.0f;

		projection[8] = 0.0f;
		projection[9] = 0.0f;
		projection[10] = 0.5f;
		projection[11] = 0.0f;

		projection[12] = (R + L) / (L - R);
		projection[13] = (T + B) / (B - T);
		projection[14] = 0.5f;
		projection[15] = 1.0f;

		fixed (float* projPtr = projection)
		{
			native->SetGraphicsRoot32BitConstants(1, 16, projPtr, 0);
		}

		var scaleX = 1.0f;
		var scaleY = 1.0f;
		if (frame.DisplaySize.X > 0.0f && frame.DisplaySize.Y > 0.0f)
		{
			scaleX = frame.FramebufferSize.X / frame.DisplaySize.X;
			scaleY = frame.FramebufferSize.Y / frame.DisplaySize.Y;
		}

		for (var i = 0; i < frame.Commands.Length; i++)
		{
			var cmd = frame.Commands[i];
			var clip = cmd.ClipRect;
			var clipX1 = (int) Math.Floor((clip.X - frame.DisplayPos.X) * scaleX);
			var clipY1 = (int) Math.Floor((clip.Y - frame.DisplayPos.Y) * scaleY);
			var clipX2 = (int) Math.Ceiling((clip.Z - frame.DisplayPos.X) * scaleX);
			var clipY2 = (int) Math.Ceiling((clip.W - frame.DisplayPos.Y) * scaleY);

			if (clipX1 < 0) clipX1 = 0;
			if (clipY1 < 0) clipY1 = 0;
			if (clipX2 > frame.FramebufferSize.X) clipX2 = (int) frame.FramebufferSize.X;
			if (clipY2 > frame.FramebufferSize.Y) clipY2 = (int) frame.FramebufferSize.Y;
			if (clipX2 <= clipX1 || clipY2 <= clipY1)
			{
				continue;
			}

			var clipRect = new D3DRect(clipX1, clipY1, clipX2, clipY2);
			native->RSSetScissorRects(1, &clipRect);

			native->DrawIndexedInstanced((uint) cmd.ElemCount, 1, (uint) cmd.IdxOffset, (int) cmd.VtxOffset, 0);
		}
	}

	private void EnsureBuffers(UiFrameData frame)
	{
		var vertexBytes = frame.VertexCount * Unsafe.SizeOf<ImDrawVert>();
		var indexBytes = frame.IndexCount * sizeof(ushort);

		if (_vertexBuffer.Handle is null || _vertexBufferSize < vertexBytes)
		{
			_vertexBuffer.Dispose();
			_vertexBufferSize = (int) Math.Max(vertexBytes, 65536);
			CreateUploadBuffer(ref _vertexBuffer, (ulong) _vertexBufferSize);
		}

		if (_indexBuffer.Handle is null || _indexBufferSize < indexBytes)
		{
			_indexBuffer.Dispose();
			_indexBufferSize = (int) Math.Max(indexBytes, 65536);
			CreateUploadBuffer(ref _indexBuffer, (ulong) _indexBufferSize);
		}
	}

	private void CreateUploadBuffer(ref ComPtr<ID3D12Resource> buffer, ulong size)
	{
		var desc = new ResourceDesc
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

		var heap = new HeapProperties(HeapType.Upload);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&heap,
			HeapFlags.None,
			in desc,
			ResourceStates.GenericRead,
			null,
			out buffer));
	}

	private void CreateFontTexture(in ImGuiFontAtlas atlas)
	{
		if (atlas.PixelsRgba.Length == 0 || atlas.Width == 0 || atlas.Height == 0)
		{
			return;
		}

		var textureDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong) atlas.Width,
			Height = (uint) atlas.Height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatR8G8B8A8Unorm,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.None
		};

		var defaultHeap = new HeapProperties(HeapType.Default);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&defaultHeap,
			HeapFlags.None,
			in textureDesc,
			ResourceStates.CopyDest,
			null,
			out _fontTexture));

		PlacedSubresourceFootprint layout = default;
		uint numRows = 0;
		ulong rowSize = 0;
		ulong uploadSize = 0;
		_device.GetCopyableFootprints(in textureDesc, 0, 1, 0, &layout, &numRows, &rowSize, &uploadSize);

		var uploadDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = uploadSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		var uploadHeap = new HeapProperties(HeapType.Upload);
		ComPtr<ID3D12Resource> uploadBuffer;
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&uploadHeap,
			HeapFlags.None,
			in uploadDesc,
			ResourceStates.GenericRead,
			null,
			out uploadBuffer));

		byte* mapped = null;
		SilkMarshal.ThrowHResult(uploadBuffer.Map(0, (D3DRange*) null, (void**) &mapped));

		fixed (byte* src = atlas.PixelsRgba)
		{
			for (var row = 0u; row < numRows; row++)
			{
				var dest = mapped + layout.Offset + row * layout.Footprint.RowPitch;
				var srcRow = src + row * (ulong) atlas.Width * 4;
				Buffer.MemoryCopy(srcRow, dest, layout.Footprint.RowPitch, (ulong) atlas.Width * 4);
			}
		}

		uploadBuffer.Unmap(0, (D3DRange*) null);

		ComPtr<ID3D12CommandAllocator> allocator;
		SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(CommandListType.Direct, out allocator));
		ComPtr<ID3D12GraphicsCommandList> uploadList;
		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				CommandListType.Direct,
				allocator,
				default,
				out uploadList));

		var dstLocation = new TextureCopyLocation
		{
			PResource = _fontTexture.Handle,
			Type = TextureCopyType.SubresourceIndex
		};
		dstLocation.Anonymous.SubresourceIndex = 0;

		var srcLocation = new TextureCopyLocation
		{
			PResource = uploadBuffer.Handle,
			Type = TextureCopyType.PlacedFootprint
		};
		srcLocation.Anonymous.PlacedFootprint = layout;

		uploadList.CopyTextureRegion(&dstLocation, 0, 0, 0, &srcLocation, (Box*) null);

		var barrier = new ResourceBarrier {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barrier.Anonymous.Transition = new()
		{
			PResource = _fontTexture.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.PixelShaderResource
		};
		uploadList.ResourceBarrier(1, &barrier);
		SilkMarshal.ThrowHResult(uploadList.Close());

		var queueDesc = new CommandQueueDesc(CommandListType.Direct, 0, CommandQueueFlags.None);
		ComPtr<ID3D12CommandQueue> queue;
		SilkMarshal.ThrowHResult(_device.CreateCommandQueue(queueDesc, out queue));

		ID3D12CommandList* lists = (ID3D12CommandList*) uploadList.Handle;
		queue.Handle->ExecuteCommandLists(1, &lists);

		ComPtr<ID3D12Fence> fence;
		SilkMarshal.ThrowHResult(_device.CreateFence(0, FenceFlags.None, out fence));
		const ulong fenceValue = 1;
		SilkMarshal.ThrowHResult(queue.Handle->Signal(fence.Handle, fenceValue));
		while (fence.Handle->GetCompletedValue() < fenceValue)
		{
			Thread.Sleep(0);
		}

		fence.Dispose();
		queue.Dispose();
		uploadList.Dispose();
		allocator.Dispose();
		uploadBuffer.Dispose();
	}

	private void CreateSrv()
	{
		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.CbvSrvUav,
			NumDescriptors = 1,
			Flags = DescriptorHeapFlags.ShaderVisible,
			NodeMask = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in heapDesc, out _srvHeap));
		_srvGpuHandle = _srvHeap.GetGPUDescriptorHandleForHeapStart();
		var srvCpuHandle = _srvHeap.GetCPUDescriptorHandleForHeapStart();

		const uint DefaultShader4ComponentMapping = 5768; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

		var srvDesc = new ShaderResourceViewDesc
		{
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
			Format = Format.FormatR8G8B8A8Unorm,
			ViewDimension = SrvDimension.Texture2D
		};
		srvDesc.Anonymous.Texture2D = new Tex2DSrv
		{
			MipLevels = 1,
			MostDetailedMip = 0,
			ResourceMinLODClamp = 0.0f
		};

		_device.CreateShaderResourceView(_fontTexture, srvDesc, srvCpuHandle);
	}

	private void CreatePipeline()
	{
		var sampler = stackalloc StaticSamplerDesc[1];
		sampler[0] = new StaticSamplerDesc
		{
			Filter = Filter.MinMagMipLinear,
			AddressU = TextureAddressMode.Clamp,
			AddressV = TextureAddressMode.Clamp,
			AddressW = TextureAddressMode.Clamp,
			ShaderVisibility = ShaderVisibility.Pixel,
			ComparisonFunc = ComparisonFunc.Always,
			ShaderRegister = 0,
			RegisterSpace = 0
		};

		var descriptorRanges = stackalloc DescriptorRange[1];
		descriptorRanges[0] = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Srv,
			NumDescriptors = 1,
			BaseShaderRegister = 0,
			RegisterSpace = 0,
			OffsetInDescriptorsFromTableStart = 0
		};

		var rootParameters = stackalloc RootParameter[2];
		rootParameters[0] = default;
		rootParameters[0].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[0].Anonymous.DescriptorTable = new()
		{
			NumDescriptorRanges = 1,
			PDescriptorRanges = descriptorRanges
		};
		rootParameters[0].ShaderVisibility = ShaderVisibility.Pixel;

		rootParameters[1] = default;
		rootParameters[1].ParameterType = RootParameterType.Type32BitConstants;
		rootParameters[1].Anonymous.Constants = new()
		{
			ShaderRegister = 0,
			RegisterSpace = 0,
			Num32BitValues = 16
		};
		rootParameters[1].ShaderVisibility = ShaderVisibility.Vertex;

		var rootSignatureDesc = new RootSignatureDesc
		{
			NumParameters = 2,
			PParameters = rootParameters,
			NumStaticSamplers = 1,
			PStaticSamplers = sampler,
			Flags = RootSignatureFlags.AllowInputAssemblerInputLayout
		};

		var versionedDesc = new VersionedRootSignatureDesc
		{
			Version = D3DRootSignatureVersion.Version10
		};
		versionedDesc.Anonymous.Desc10 = rootSignatureDesc;

		ID3D10Blob* rootSignatureBlob = null;
		ID3D10Blob* rootSignatureError = null;
		var serializeResult =
			D3D12.GetApi().SerializeVersionedRootSignature(&versionedDesc, &rootSignatureBlob, &rootSignatureError);
		try
		{
			HandleRootSignatureErrors(serializeResult, rootSignatureError, "imgui");

			SilkMarshal.ThrowHResult(_device.CreateRootSignature(
				0,
				rootSignatureBlob->GetBufferPointer(),
				rootSignatureBlob->GetBufferSize(),
				out _rootSignature));
		}
		finally
		{
			if (rootSignatureBlob is not null)
			{
				rootSignatureBlob->Release();
			}

			if (rootSignatureError is not null)
			{
				rootSignatureError->Release();
			}
		}

		var vertexShader = _shaderCompiler.GetDxil("imgui.slang", "vertexShader", "vs_6_0");
		var pixelShader = _shaderCompiler.GetDxil("imgui.slang", "fragmentShader", "ps_6_0");

		Span<byte> positionSemantic = stackalloc byte["POSITION".Length + 1];
		Span<byte> texcoordSemantic = stackalloc byte["TEXCOORD".Length + 1];
		Span<byte> colorSemantic = stackalloc byte["COLOR".Length + 1];
		"POSITION"u8.CopyTo(positionSemantic);
		"TEXCOORD"u8.CopyTo(texcoordSemantic);
		"COLOR"u8.CopyTo(colorSemantic);

		var inputElements = stackalloc InputElementDesc[3];
		fixed (byte* positionPtr = positionSemantic)
		fixed (byte* texcoordPtr = texcoordSemantic)
		fixed (byte* colorPtr = colorSemantic)
		{
			inputElements[0] = new InputElementDesc
			{
				SemanticName = positionPtr,
				SemanticIndex = 0,
				Format = Format.FormatR32G32Float,
				InputSlot = 0,
				AlignedByteOffset = 0,
				InputSlotClass = InputClassification.PerVertexData,
				InstanceDataStepRate = 0
			};

			inputElements[1] = new InputElementDesc
			{
				SemanticName = texcoordPtr,
				SemanticIndex = 0,
				Format = Format.FormatR32G32Float,
				InputSlot = 0,
				AlignedByteOffset = 8,
				InputSlotClass = InputClassification.PerVertexData,
				InstanceDataStepRate = 0
			};

			inputElements[2] = new InputElementDesc
			{
				SemanticName = colorPtr,
				SemanticIndex = 0,
				Format = Format.FormatR8G8B8A8Unorm,
				InputSlot = 0,
				AlignedByteOffset = 16,
				InputSlotClass = InputClassification.PerVertexData,
				InstanceDataStepRate = 0
			};

			var inputLayout = new InputLayoutDesc
			{
				PInputElementDescs = inputElements,
				NumElements = 3
			};

			var blendState = new BlendDesc
			{
				AlphaToCoverageEnable = 0,
				IndependentBlendEnable = 0
			};
			blendState.RenderTarget[0] = new RenderTargetBlendDesc
			{
				BlendEnable = 1,
				LogicOpEnable = 0,
				SrcBlend = Blend.SrcAlpha,
				DestBlend = Blend.InvSrcAlpha,
				BlendOp = BlendOp.Add,
				SrcBlendAlpha = Blend.One,
				DestBlendAlpha = Blend.InvSrcAlpha,
				BlendOpAlpha = BlendOp.Add,
				LogicOp = LogicOp.Noop,
				RenderTargetWriteMask = 0x0F
			};

			var rasterizerState = new RasterizerDesc
			{
				FillMode = D3DFillMode.Solid,
				CullMode = D3DCullMode.None,
				FrontCounterClockwise = 0,
				DepthBias = 0,
				DepthBiasClamp = 0.0f,
				SlopeScaledDepthBias = 0.0f,
				DepthClipEnable = 1,
				MultisampleEnable = 0,
				AntialiasedLineEnable = 0,
				ForcedSampleCount = 0,
				ConservativeRaster = ConservativeRasterizationMode.Off
			};

			var depthStencilState = new DepthStencilDesc
			{
				DepthEnable = 0,
				DepthWriteMask = DepthWriteMask.Zero,
				DepthFunc = ComparisonFunc.Always,
				StencilEnable = 0,
				StencilReadMask = 0,
				StencilWriteMask = 0
			};

			fixed (byte* vsPtr = vertexShader)
			fixed (byte* psPtr = pixelShader)
			{
				var shaderBytecodeVS = new ShaderBytecode {PShaderBytecode = vsPtr, BytecodeLength = (nuint) vertexShader.Length};
				var shaderBytecodePS = new ShaderBytecode {PShaderBytecode = psPtr, BytecodeLength = (nuint) pixelShader.Length};

				var psoDesc = new GraphicsPipelineStateDesc
				{
					PRootSignature = _rootSignature.Handle,
					VS = shaderBytecodeVS,
					PS = shaderBytecodePS,
					BlendState = blendState,
					SampleMask = uint.MaxValue,
					RasterizerState = rasterizerState,
					DepthStencilState = depthStencilState,
					InputLayout = inputLayout,
					IBStripCutValue = IndexBufferStripCutValue.ValueDisabled,
					PrimitiveTopologyType = D3DPrimitiveTopologyType.Triangle,
					NumRenderTargets = 1,
					DSVFormat = Format.FormatUnknown,
					SampleDesc = new SampleDesc(1, 0),
					NodeMask = 0,
					CachedPSO = default,
					Flags = PipelineStateFlags.None
				};

				psoDesc.RTVFormats[0] = Format.FormatB8G8R8A8Unorm;

				SilkMarshal.ThrowHResult(_device.CreateGraphicsPipelineState(in psoDesc, out _pipelineState));
			}
		}
	}

	private static void HandleRootSignatureErrors(int result, ID3D10Blob* errorBlob, string kind)
	{
		string errorMessage = string.Empty;
		if (errorBlob is not null)
		{
			errorMessage = Marshal.PtrToStringAnsi((nint) errorBlob->GetBufferPointer()) ?? string.Empty;
			errorBlob->Release();
		}

		if (result < 0)
		{
			throw new InvalidOperationException($"Failed to serialize {kind} root signature: {errorMessage}");
		}
	}

	public void Dispose()
	{
		_fontTexture.Dispose();
		_srvHeap.Dispose();
		_vertexBuffer.Dispose();
		_indexBuffer.Dispose();
		_pipelineState.Dispose();
		_rootSignature.Dispose();
	}
}
