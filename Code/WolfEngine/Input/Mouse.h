#ifndef _MOUSE_H
#define _MOUSE_H
#include "../Models/Point.h"
#include "../Includes.h"
#include "Key.h"

///
///	A class for mouse input
///
class Mouse
{
	public:
		///	Mouse position on the screen, in pixels
		static Point position;
		
		/// Is a key down?
		static bool KeyDown(int key);
		///	Was a key released this frame?
		static bool KeyReleased(int key);
		///	Was a key pressed down this frame?
		static bool KeyClicked(int key);
		/// This function runs at the start of every frame. For internal use only
		static void Update(SDL_Event* eventHandler);

		///	Left mouse button, direct usage not recommended
		static Key key1;
		///	Right mouse button, direct usage not recommended
		static Key key2;
		/// Middle mouse button, direct usage not recommended
		static Key key3;
};

#endif