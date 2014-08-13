/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Input.h"
#include "Mouse.h"
#include "Keyboard.h"


void Input::Update(SDL_Event * eventHandler)
{
	Mouse::key1.released = false;
	Mouse::key2.released = false;
	Mouse::key3.released = false;

	if (!inited)
	{
		Keyboard::Init();
		inited = true;
	}

	for (int i = 0; i < Keyboard::keys.size(); i++) Keyboard::keys[i].released = false;

	while (SDL_PollEvent(eventHandler) != 0)
	{
		Keyboard::Update(eventHandler);
		Mouse::Update(eventHandler);
	}
}