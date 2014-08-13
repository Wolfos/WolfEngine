#ifndef _KEYBOARD_H
#define _KEYBOARD_H

#include "../Includes.h"
#include "Key.h"
#include "Keys.h"
#include <vector>

///
///	Class for working with keyboard input, uses the Keys enum found in Keys.h
/// Although you can use the keys vector directly, recommended usage is to use the provided functions
///
class Keyboard
{
public:
	///	Initializes some values, for internal use only
	static void Init();
	///	Is a key down?
	static bool KeyDown(int key);
	///	Was a key released this frame?
	static bool KeyReleased(int key);
	///	Was a key pressed down this frame?
	static bool KeyClicked(int key);
	/// This function runs at the start of every frame. For internal use only
	static void Update(SDL_Event* eventHandler);
	/// A list of all the keys
	static std::vector<Key> keys;

private:
	static void Down(int key);
	static void Up(int key);
};

#endif