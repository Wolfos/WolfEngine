#ifndef _GUI_H
#define _GUI_H
#include "../Includes.h"
#include "../Rendering/Bitmap.h"

namespace GUI
{
	extern void Init();
	extern void Box(Rect position);
	extern bool Button(Rect position, std::string text);
	extern float VerticalScrollBar(Rect position, float value, float minValue, float maxValue);
	extern void Exit();
};

#endif