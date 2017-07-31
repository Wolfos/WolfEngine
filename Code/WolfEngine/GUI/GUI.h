#ifndef _GUI_H
#define _GUI_H
#include "../Includes.h"
#include "../Rendering/Bitmap.h"

namespace GUI
{
	extern void Init();
	extern void Box(WRect position);
	extern bool Button(WRect position, std::string text);
	extern float VerticalScrollBar(WRect position, float value, float maxValue);
	extern void Exit();
};

#endif