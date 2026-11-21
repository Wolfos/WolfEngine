`bc1_compress.compute.slang`, `bc4_compress.compute.slang` and `bc6h_compress.compute.slang` are derived from Betsy's `bc1.glsl`, `bc4.glsl` and `bc6h.glsl` shader sources. Betsy's `bc6h.glsl` is itself based on [GPURealTimeBC6H](https://github.com/knarkowicz/GPURealTimeBC6H) by Krzysztof Narkowicz, which is in the public domain.

WolfEngine ports only the shader logic for offline editor texture compression. The original Betsy runtime/backend integration code is not included here.
