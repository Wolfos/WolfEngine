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

extern void GUI::Box(WRect position)
{
	//boxBackground->Blit(boxBackground->rect, &position);
	if (Collision::AABB(Mouse::position, position))
	{
		Mouse::overGUI = true;
	}
}

extern bool GUI::Button(WRect position, std::string text, bool highlight)
{
	Bitmap* toRender;
	if (Collision::AABB(Mouse::position, position))
	{
		Mouse::overGUI = true;
		if (Mouse::KeyDown(0)) toRender = buttonPressed;
		else toRender = buttonHover;
	}
	else
	{
		toRender = buttonBackground;
		//buttonBackground->Blit(buttonBackground->rect, &position);
	}
	if (highlight) toRender = buttonPressed;
	//toRender->Blit(toRender->rect, &position);
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

float Clamp(float val, float min, float max)
{
	if(val < min) val = min;
	if(val > max) val = max;
	return val;
}

bool mouseDown;

extern float GUI::VerticalScrollBar(WRect position, float value, float maxValue)
{
	if (Collision::AABB(Mouse::position, position))
	{
		Mouse::overGUI = true;
	}

	float minValue = 0;
	//scrollBarBackground->Blit(scrollBarBackground->rect, &position);

	WRect scrollBarHandleRect = { position.x,
								  (int)Lerp(position.y, position.y + position.h - 64, (value - minValue) / maxValue),
								  position.w,
								  64 };
	
	if(!mouseDown && Mouse::KeyDown(0) && Collision::AABB(Mouse::position, scrollBarHandleRect))
	{
		mouseDown = true;
	}
	else if(mouseDown && Mouse::KeyDown(0) && Collision::AABB(Mouse::position, position))
	{
		value = Lerp(minValue, maxValue, (float)(Mouse::position.y - position.y) / (float)(position.h - scrollBarHandleRect.h ));
	}
	else if(!Mouse::KeyDown(0))
	{
		mouseDown = false;
	}

	value = Clamp(value, minValue, maxValue);
	scrollBarHandleRect.y = Lerp(position.y, position.y + position.h - scrollBarHandleRect.h, (value - minValue) / maxValue);
	//scrollBarHandle->Blit(scrollBarHandle->rect, &scrollBarHandleRect);

	return value;
}

extern float GUI::HorizontalScrollBar(WRect position, float value, float maxValue)
{
	if (Collision::AABB(Mouse::position, position))
	{
		Mouse::overGUI = true;
	}

	float minValue = 0;
	//scrollBarBackground->Blit(scrollBarBackground->rect, &position);

	WRect scrollBarHandleRect = { (int)Lerp(position.x, position.x + position.w - 64, (value - minValue) / maxValue),
		position.y,
		64,
		position.h };

	if (!mouseDown && Mouse::KeyDown(0) && Collision::AABB(Mouse::position, scrollBarHandleRect))
	{
		mouseDown = true;
	}
	else if (mouseDown && Mouse::KeyDown(0) && Collision::AABB(Mouse::position, position))
	{
		value = Lerp(minValue, maxValue, (float)(Mouse::position.x - position.x) / (float)(position.w - scrollBarHandleRect.w));
	}
	else if (!Mouse::KeyDown(0))
	{
		mouseDown = false;
	}

	value = Clamp(value, minValue, maxValue);
	scrollBarHandleRect.x = Lerp(position.x, position.x + position.w - scrollBarHandleRect.w, (value - minValue) / maxValue);
	//scrollBarHandle->Blit(scrollBarHandle->rect, &scrollBarHandleRect);

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
