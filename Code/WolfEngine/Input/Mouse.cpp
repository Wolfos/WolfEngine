#include "Mouse.h"

WPoint Mouse::position;
bool Mouse::overGUI = false;
Key Mouse::key0;
Key Mouse::key1;
Key Mouse::key2;

bool Mouse::KeyDown(int key)
{
	if (key == 0 && key0.down) return true;
	else if (key == 1 && key1.down) return true;
	else if (key == 2 && key2.down) return true;

	return false;
}

bool Mouse::KeyReleased(int key)
{
	if (key == 0 && key0.released) return true;
	else if (key == 2 && key1.released) return true;
	else if (key == 3 && key2.released) return true;

	return false;
}

bool Mouse::KeyClicked(int key)
{
	if (key == 1 && key0.clicked) return true;
	else if (key == 2 && key1.clicked) return true;
	else if (key == 3 && key2.clicked) return true;

	return false;
}

void Mouse::Update(SDL_Event* eventHandler)
{
	if (eventHandler->type == SDL_MOUSEBUTTONDOWN)
	{
		switch (eventHandler->button.button)
		{
			case SDL_BUTTON_LEFT:
				key0.down = true;
				key0.clicked = !key0.wasdown;
				break;
			case SDL_BUTTON_RIGHT:
				key1.down = true;
				key1.clicked = !key1.wasdown;
				break;
			case SDL_BUTTON_MIDDLE:
				key2.down = true;
				key2.clicked = !key2.wasdown;
				break;
		}
	}
	else if (eventHandler->type == SDL_MOUSEBUTTONUP)
	{
		switch (eventHandler->button.button)
		{
			case SDL_BUTTON_LEFT:
				if(!key0.released) key0.released = true;
				key0.down = false;
				key0.clicked = false;
				break;
			case SDL_BUTTON_RIGHT:
				if (!key1.released) key1.released = true;
				key1.down = false;
				key1.clicked = false;
				break;
			case SDL_BUTTON_MIDDLE:
				if (!key2.released) key2.released = true;
				key2.down = false;
				key2.clicked = false;
				break;
		}
	}
	else if (eventHandler->type == SDL_MOUSEMOTION)
	{
		SDL_GetMouseState(&position.x, &position.y);
	}

}