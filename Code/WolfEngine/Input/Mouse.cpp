#include "Mouse.h"

Point Mouse::position;
Key Mouse::key1;
Key Mouse::key2;
Key Mouse::key3;

bool Mouse::KeyDown(int key)
{
	if (key == 1 && key1.down) return true;
	else if (key == 2 && key2.down) return true;
	else if (key == 3 && key3.down) return true;

	return false;
}

bool Mouse::KeyReleased(int key)
{
	if (key == 1 && key1.released) return true;
	else if (key == 2 && key2.released) return true;
	else if (key == 3 && key3.released) return true;

	return false;
}

bool Mouse::KeyClicked(int key)
{
	if (key == 1 && key1.clicked) return true;
	else if (key == 2 && key2.clicked) return true;
	else if (key == 3 && key3.clicked) return true;

	return false;
}

void Mouse::Update(SDL_Event* eventHandler)
{
	if (key1.down) key1.wasdown = true;
	else key1.wasdown = false;
	if (key2.down) key2.wasdown = true;
	else key2.wasdown = false;
	if (key3.down) key3.wasdown = true;
	else key3.wasdown = false;

	if (eventHandler->type == SDL_MOUSEBUTTONDOWN)
	{
		switch (eventHandler->button.button)
		{
			case SDL_BUTTON_LEFT:
				key1.down = true;
				if (!key1.wasdown) key1.clicked = true;
				else key1.clicked = false;
				break;
			case SDL_BUTTON_RIGHT:
				key2.down = true;
				if(!key2.wasdown) key2.clicked = true;
				else key2.clicked = false;
				break;
			case SDL_BUTTON_MIDDLE:
				key3.down = true;
				if (!key3.wasdown) key3.clicked = true;
				else key3.clicked = false;
				break;
		}
	}
	else if (eventHandler->type == SDL_MOUSEBUTTONUP)
	{
		switch (eventHandler->button.button)
		{
			case SDL_BUTTON_LEFT:
				if(!key1.released) key1.released = true;
				key1.down = false;
				break;
			case SDL_BUTTON_RIGHT:
				if (!key2.released) key2.released = true;
				key2.down = false;
				break;
			case SDL_BUTTON_MIDDLE:
				if (!key3.released) key3.released = true;
				key3.down = false;
				break;
		}
	}
	else if (eventHandler->type == SDL_MOUSEMOTION)
	{
		SDL_GetMouseState(&position.x, &position.y);
	}

}