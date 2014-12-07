#include "Window.h"
#include "GUI.h"
#include "../Input/Mouse.h"
#include "../WolfEngine.h"

Window::Window(int x, int y, int width, int height)
{
	GUI::AddWindow<Window>(this);
	background = new Bitmap("GUI/Box.png");
	titleBar = new Bitmap("GUI/Bar.png");

	barRect = { x, y, width, barHeight };

	hitbox.x = x;
	hitbox.y = y + barHeight;
	hitbox.w = width;
	hitbox.h = height;

	position.x = x;
	position.y = y + barHeight;
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
	HandleInput();

	hitbox.x = position.x;
	hitbox.y = position.y;
	background->Blit(NULL, &hitbox);

	barRect.x = position.x;
	barRect.y = position.y - barHeight;
	titleBar->Blit(NULL, &barRect);
}

void Window::HandleInput()
{
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

	//Window dragging
	if (Collide(Mouse::position, barRect) && Mouse::KeyClicked(1))
	{
		vertSnap = None;
		horSnap = None;
		dragging = true;
		startPos = position;
		startMousePos = Mouse::position;
	}
	if (dragging)
	{
		if (Mouse::KeyReleased(1))
		{
			dragging = false;
		}

		position.x = startPos.x - (startMousePos.x - Mouse::position.x);
		position.y = startPos.y - (startMousePos.y - Mouse::position.y);

		if (!dragging) //We let go of the mouse button, shall we snap now?
		{
			if (position.x <= 0)
			{
				horSnap = Left;
			}
			if (position.x >= WolfEngine::scene->camera->width - hitbox.w)
			{
				horSnap = Right;
			}
			if (position.y <= 0 + barHeight)
			{
				vertSnap = Top;
			}
			if (position.y >= WolfEngine::scene->camera->height - hitbox.h)
			{
				vertSnap = Bottom;
			}
		}
	}

	//Snap states
	if (vertSnap == Top)
	{
		position.y = 0 + barHeight;
	}
	if (vertSnap == Bottom)
	{
		position.y = WolfEngine::scene->camera->height - hitbox.h;
	}
	if (horSnap == Left)
	{
		position.x = 0;
	}
	if (horSnap == Right)
	{
		position.x = WolfEngine::scene->camera->width - hitbox.w;
	}
}