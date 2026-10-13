using System.Diagnostics.CodeAnalysis;

// SharpMetal marks its whole API [SupportedOSPlatform("macos")], although it is plain objc_msgSend against Metal and
// runs unchanged on iOS. The Metal backend is correctly marked for both macOS and iOS, which CA1416 then reports as
// calling macOS-only APIs from iOS. The suppression covers this namespace only, so the check still guards the rest of
// the engine, the Windows-only D3D12 backend included.
[assembly: SuppressMessage(
	"Interoperability",
	"CA1416:Validate platform compatibility",
	Scope = "namespaceanddescendants",
	Target = "~N:WolfEngine.Rendering.Backend.Metal",
	Justification = "SharpMetal's macOS-only annotation is inaccurate; the Metal backend runs on iOS.")]
