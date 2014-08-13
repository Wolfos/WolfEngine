#ifndef _MOUSE_H
#define _MOUSE_H
#include "../Models/Point.h"
#include "../Includes.h"
#include "Key.h"

class Mouse
{
	public:
		static Point position;
		
		static bool KeyDown(int key);
		static bool KeyReleased(int key);
		static bool KeyClicked(int key);
		static void Update(SDL_Event* eventHandler);

		static Key key1;
		static Key key2;
		static Key key3;
};

#endif