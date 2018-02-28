WolfEngine
==========
[Trello](https://trello.com/b/qcnDlhgd/wolfengine)

A tile-based 2D game engine made for fun and learning

The engine is currently undergoing a partial rewrite and will only run on MacOS and Linux at the moment. Support for Windows is on the roadmap.

[Documentation (outdated)](http://rvanee.nl/documentation/)


Setup for Windows
==========

1. Download and install Visual Studio
2. Download [SDL2](http://libsdl.org/download-2.0.php), [SDL2_TTF](https://www.libsdl.org/projects/SDL_ttf/), [SDL2_Mixer](http://www.libsdl.org/projects/SDL_mixer/) and [GLEW](http://glew.sourceforge.net) (you'll want the development libraries for Visual C++)
3. Extract these libraries into a directory (example: C:\Developer)
4. Setup environment variables for each, SDL2, SDL2_MIXER, SDL2_TTF and GLEW_ROOT_DIR, pointing to their respective folders
5. Download and install CMake
6. Make a directory called 'build' in the WolfEngine folder
7. In your command line, cd into that build folder you just made
8. Run 'cmake -G "Visual Studio 12 2013" ..' (unless you're using a different VS version)
9. Open the resulting Visual Studio project file and compile. It will fail to run but that's okay.
10. Get the debug DLL's for each SDL addon, they're in the respective lib/x86 directory. Put them all (yes, all of them, that includes libpng and the like) where Visual Studio put your EXE
11. Copy the Asset folder into the build directory
12. Run the executable again, either through Visual Studio or by double clicking it
13. You have succesfully setup WolfEngine for development!

Setup for MacOS
==========
(TODO: more detailed instructions)

Install the required libraries (SDL2, SDL2_TTF and SDL2_Mixer) through a package manager and the XCode build tools, then build using CMake.

Note: GLEW is not required on MacOS

Setup for Ubuntu
==========

Just run Setup/Ubuntu.sh and it should build
