#ifndef _TILEPICKER_H
#define _TILEPICKER_H
#include "../WolfEngine/API.h"
#include "../WolfEngine/WolfEngine.h"

class TilePicker : public Window
{
	public:
		TilePicker(int x, int y, int width, int height);
		virtual void Update();
		int selected = 0;
	private:
		int tileWidth = 128;
		int tileHeight = 128;
		float zoom = 0.5f;
		Rect tilesheetRect;
		Bitmap* tilesheet;
		Button* button;
};

#endif