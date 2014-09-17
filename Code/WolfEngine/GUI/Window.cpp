#include "Window.h"
#include "GUI.h"
#include "../Input/Mouse.h"
#include "../Game.h"

Window::Window(int x, int y, int width, int height)
{
	GUI::AddWindow<Window>(this);
	background = new Bitmap("GUI/Box.png");

	hitbox.x = x;
	hitbox.y = y;
	hitbox.w = width;
	hitbox.h = height;

	position.x = x;
	position.y = y;
}

bool Window::Collide(Point point, Rect rect)
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
	background->Blit(NULL, &hitbox);

	if (Collide(Mouse::position, hitbox))
	{
		mouseOver = true;

		if (Mouse::KeyClicked(1)) clicked = true;
		else clicked = false;
	}
	else
	{
		mouseOver = false;
		clicked = false;
	}
}