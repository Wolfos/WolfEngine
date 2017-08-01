#ifndef _EDITOR_H
#define _EDITOR_H

#include "../WolfEngine/API.h"


class Editor : public Scene
{
	public:
		void Start();
		void Update();
		void OnGUI();
		void Exit();

	private:
		Map* map;
		Map* grid;
		Bitmap* spritesheet;
		Bitmap* gridtex;
		Bitmap* topBar;
		Bitmap* selectionRect;
		int tilewidth;
		int tileheight;
		bool dragging = false;
		Transform* cam;
		int selected = 0;
		int layer = 0;
		WRect selRectPos;
		bool showGrid = true;
		float yPos = 0.0f;
		float xPos = 0.0f;
		bool canDraw = true;
		WPoint initialMousePos;
		WPointF initialMapPos;
};


#endif