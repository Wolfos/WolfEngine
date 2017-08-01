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
	Mouse::key0.released = false;
	Mouse::key1.released = false;
	Mouse::key2.released = false;
	Mouse::scrollX = 0;
	Mouse::scrollY = 0;

	//We need to do all of this here because SDL_MOUSEBUTTONDOWN doesn't call an event every frame, unless the mouse also moved
	if (Mouse::key0.down)
	{
		Mouse::key0.wasdown = true;
		Mouse::key0.clicked = false;
	}
	else Mouse::key0.wasdown = false;
	if (Mouse::key1.down)
	{
		Mouse::key1.wasdown = true;
		Mouse::key1.clicked = false;
	}
	else Mouse::key1.wasdown = false;
	if (Mouse::key2.down)
	{
		Mouse::key2.wasdown = true;
		Mouse::key2.clicked = false;
	}
	else Mouse::key2.wasdown = false;

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