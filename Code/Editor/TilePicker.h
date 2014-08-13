#ifndef _TILEPICKER_H
#define _TILEPICKER_H
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
		SDL_Rect tilesheetRect;
		SDL_Texture* tilesheet;
		Button* button;
};

#endif