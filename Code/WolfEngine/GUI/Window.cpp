#include "Window.h"
#include "GUI.h"
#include "../Rendering/Image.h"
#include "../Rendering/Screen.h"
#include "../Input/Mouse.h"

Window::Window(int x, int y, int width, int height)
{
	GUI::AddWindow<Window>(this);
	background = Image::Load("GUI/Box.png");

	hitbox.x = x;
	hitbox.y = y;
	hitbox.w = width;
	hitbox.h = height;

	position.x = x;
	position.y = y;
}

bool Window::Collide(Point point, SDL_Rect rect)
{
	if (point.x < rect.x) return false;
	if (point.y < rect.y) return false;
	if (point.x > rect.x + rect.w) return false;
	if (point.y > rect.y + rect.h) return false;

	return true;
}

void Window::Render()
{
	hitbox.x = position.x;
	hitbox.y = position.y;
	SDL_RenderCopy(Screen::mainCamera->screen, background, NULL, &hitbox);

	if (Collide(Mouse::position, hitbox))
	{
		mouseOver = true;

		if (Mouse::KeyReleased(1)) clicked = true;
		else clicked = false;
	}
	else
	{
		mouseOver = false;
		clicked = false;
	}
}