#include "GUI.h"
#include "../Utilities/Collision.h"
#include "../Input/Mouse.h"
#include "../Rendering/Font.h"

Bitmap* boxBackground;
Bitmap* buttonBackground;
Bitmap* buttonHover;
Bitmap* buttonPressed;
Bitmap* scrollBarBackground;
Bitmap* scrollBarHandle;
Font* font;
bool mousePressed = false;
int initMouseX;
int initMouseY;

extern void GUI::Init()
{
	boxBackground = new Bitmap("GUI/Box.png");
	buttonBackground = new Bitmap("GUI/Button.png");
	buttonHover = new Bitmap("GUI/ButtonHover.png");
	buttonPressed = new Bitmap("GUI/ButtonPressed.png");
	scrollBarBackground = new Bitmap("GUI/scrollBarBackground.png");
	scrollBarHandle = new Bitmap("GUI/scrollBarHandle.png");
	font = new Font("Oregon LDO.ttf",32);
}

extern void GUI::Box(Rect position)
{
	boxBackground->Blit(boxBackground->rect, &position);
	if (Collision::AABB(Mouse::position, position))
	{
		Mouse::overGUI = true;
	}
}

extern bool GUI::Button(Rect position, std::string text)
{
	if (Collision::AABB(Mouse::position, position))
	{
		Mouse::overGUI = true;
		if (Mouse::KeyDown(0)) buttonPressed->Blit(buttonHover->rect, &position);
		else buttonHover->Blit(buttonHover->rect, &position);
	}
	else
	{
		buttonBackground->Blit(buttonBackground->rect, &position);
	}
	font->Blit(position.x + position.w / 2, position.y + position.h / 2, text, { 255, 255, 255, 255 });

	if (Collision::AABB(Mouse::position, position) && Mouse::KeyReleased(0))
	{
		return true;
	}
	else
	{
		return false;
	}
}

float Lerp(float a, float b, float t){
	return (1 - t)*a + t*b;
}

int initPos;

extern float GUI::VerticalScrollBar(Rect position, float value, float minValue, float maxValue)
{
	scrollBarBackground->Blit(scrollBarBackground->rect, &position);

	if (value < minValue) value = minValue;
	if (value > maxValue) value = maxValue;

	Point minPoint = { position.x + 4, position.y + 32 };
	Point maxPoint = { position.x + 4, position.y + position.h - 32 };
	Point scrollBarHandlePos = Point::Lerp(minPoint, maxPoint, value / (minValue + maxValue));
	
	Rect scrollBarHandleRect = { scrollBarHandlePos.x, scrollBarHandlePos.y - 32, position.w - 8, 64 };
	scrollBarHandle->Blit(scrollBarHandle->rect, &scrollBarHandleRect);

	int nextYPos;

	if (Mouse::KeyDown(0))
	{
		if (Collision::AABB(Mouse::position, scrollBarHandleRect))
		{
			if (!mousePressed)
			{
				mousePressed = true;
				initMouseY = Mouse::position.y;
				initPos = scrollBarHandlePos.y;
			}
			else
			{
				nextYPos = initPos + Mouse::position.y - initMouseY;
				value = Lerp(minValue, maxValue, nextYPos / ((float)minPoint.y + (float)maxPoint.y));

				if (value < minValue) value = minValue;
				if (value > maxValue) value = maxValue;
			}
		}
	}
	else
	{
		mousePressed = false;
	}
	return value;
}

extern void GUI::Exit()
{
	delete boxBackground;
	delete buttonBackground;
	delete buttonHover;
	delete buttonPressed;
	delete font;
}