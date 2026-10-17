# WolfEngine

WolfEngine is a bespoke, C# game engine for building large, seamless open-world games. It is in active development: a working editor, runtime, and renderer already exist, while the engine continues to grow toward production-scale world building.

## Features

- Familiar editor workflows - Build worlds with an intuitive scene editor, featuring terrain painting, prefab and material editing, asset management, and integrated profiling tools.
- Build your games in C# - WolfEngine is built on top of the modern dotnet ecosystem. Use familiar tooling such as Visual Studio or Jetbrains Rider and benefit from the convenience of managed code.
- Modern, high-fidelity rendering - Bring worlds to life with dynamic global illumination, realistic lighting and shadows, volumetric effects, and modern post-processing.
- Designed for large, detailed worlds - GPU-driven rendering and an ECS-based architecture provide a foundation for complex scenes and expansive environments.
- Physics and gameplay out of the box - Create interactive worlds with integrated physics, vehicles, input handling, and a flexible gameplay foundation.
- Cross-platform from the start - Native Direct3D 12 and Metal rendering backends for Windows and macOS.

## Platform support
| Platform   | Status |
|--------|---------|
| Windows | Actively supported |
| MacOS | Actively supported |
| iOS   | A successful proof-of-concept port was made, but currently unsupported |
| Linux | Viable future addition with Vulkan or VKD3D |
| PS5/XSX | Viable future additions |
| Switch 2 | Viable future addition |
| Last-gen consoles and PC's (pre-2020) | Infeasible as WolfEngine requires bindless rendering |
| Android | Infeasible due to poor Vulkan adoption of popular chipsets |
| WebGPU | Undesired as lagging feature support would hold back other platforms |

## Current status
WolfEngine is **NOT** production-ready. While it's certainly capable enough to ship a small game, the engine is still under heavy development and the API is simply not yet stable. 

## The vision
WolfEngine aims to fill the gap between Unity and Unreal: a game engine built specifically for PC and current-generation console development. It aims to extract maximum performance from modern hardware, rather than being developed for the constraints posed by low-end mobile devices or web browsers.

WolfEngine is designed for small to medium-sized teams that value sensible defaults and systems that scale. Instead of choosing between multiple input systems, render pipelines, UI frameworks, or foliage solutions, WolfEngine provides one integrated approach for each.
The goal is to spend less time assembling the engine and less time worrying about whether its core systems will hold up as the project grows.

When a built-in system does not fit your game, there's no need to replace the entire stack or work around a closed implementation. Simply change the code.

As part of this vision, WolfEngine is distributed as source code only. An engine should be modified to fit the game, not the other way around.


## Technology
- C# and .NET
- Slang for GPU shaders
- Direct3D 12 and Metal backends
- Silk.NET, ImGui, and Jolt Physics

## Manual

- [Screen points and world rays](Manual/ScreenToRay.md)
- [Animation graphs](Manual/Animation.md)
- [Audio](Manual/Audio.md)
- [Spawning prefabs at runtime](Manual/Prefabs.md)
