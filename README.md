WolfEngine
==========

A tile based 2D game engine

[Documentation](http://wolfengine.net/documentation/)

[Trello](https://trello.com/b/qcnDlhgd/wolfengine)


Setup for Windows:
==========

1. Download and install Visual Studio (should work with 2010 and up, but only 2013 is tested)
2. Download [SDL2](http://libsdl.org/download-2.0.php), [SDL2_image](https://www.libsdl.org/projects/SDL_image/), [SDL2_TTF](https://www.libsdl.org/projects/SDL_ttf/) and [SDL2_Mixer](http://www.libsdl.org/projects/SDL_mixer/) (you'll want the development libraries for Visual C++)
3. Extract these libraries into a directory (example: C:\Developer)
4. Setup environment variables for each, SDL2, SDL2_IMAGE, SDL2_MIXER and SDL2_TTF, pointing to their respective folders
5. Download and install CMake
6. Make a directory called 'build' in the WolfEngine folder
7. In your command line, cd into that build folder you just made
8. Run 'cmake -G "Visual Studio 12 2013" ..' (unless you're using a different VS version)
9. Open the resulting Visual Studio project file and compile. It will fail to run but that's okay.
10. Get the debug DLL's for each SDL addon, they're in the respective lib/x86 directory. Put them all (yes, all of them, that includes libpng and the like) where Visual Studio put your EXE
11. Copy the Asset folder into the build directory
12. Run the executable again, either through Visual Studio or by double clicking it
13. You have succesfully setup WolfEngine for development! (hopefully)

Todo:
==========
- Make the editor work

The long run:
==========
- Switch to BGFX and ditch SDL
