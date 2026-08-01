# WolfEngine

WolfEngine is a bespoke, C# game engine for building large, seamless open-world games. It is in active development: a working editor, runtime, and renderer already exist, while the engine continues to grow toward production-scale world building.

## Features

- Cross-platform rendering on Direct3D 12 and Metal.
- A modern real-time pipeline with GPU-driven draws, clustered lighting, shadows, ambient occlusion, decals, temporal anti-aliasing, bloom, tone mapping, and optional DDGI.
- An ECS-based gameplay foundation, Jolt-powered physics and vehicles, input, and runtime scene loading.
- An ImGui-based editor with scene hierarchy, component inspection, transform tools, terrain authoring, asset browsing, material and prefab workflows, and profiling tools.

## Current status
WolfEngine is **NOT** production-ready. While it's certainly capable enough to ship a small game, the engine is still under heavy development and the API is simply not yet stable. 

## The vision

WolfEngine aims to fill the gap between Unity and Unreal: a game engine built specifically for PC and current-generation console development. It aims to extract maximum performance from modern hardware, rather than being developed for the constraints posed by low-end mobile devices or web browsers.

WolfEngine is designed for small to medium-sized teams that value sensible defaults and systems that scale. Instead of choosing between multiple input systems, render pipelines, UI frameworks, or foliage solutions, WolfEngine provides one integrated approach for each.
The goal is to spend less time assembling the engine and less time worrying about whether its core systems will hold up as the project grows.

When a built-in system does not fit your game, there's no need to replace the entire stack or work around a closed implementation. Simply change the code.

As part of this vision, WolfEngine is not distributed as a binary and never will be. An engine should be modified to fit the game, not the other way around.



## Technology

- C# and .NET
- Slang for GPU shaders
- Direct3D 12 and Metal backends
- Silk.NET, ImGui, and Jolt Physics
