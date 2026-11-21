`sky_atmosphere.slang` is ported from Sébastien Hillaire's reference implementation of "A Scalable and Production Ready Sky and Atmosphere Rendering Technique" (EGSR 2020): https://github.com/sebh/UnrealEngineSkyAtmosphere, files `Resources/SkyAtmosphereCommon.hlsl`, `Resources/RenderSkyCommon.hlsl` and `Resources/RenderSkyRayMarching.hlsl`.

WolfEngine ports the atmosphere model and LUT integration only. The demo application, its Bruneton and path-tracing comparison paths, shadow-map and GPU-debug code are not included. The LUT passes that drive this file live in `Shaders/Atmosphere/`.

The reference implementation's licence:

```
MIT License

Copyright (c) 2020 Epic Games, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
