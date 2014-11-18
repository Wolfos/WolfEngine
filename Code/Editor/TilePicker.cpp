#include "TilePicker.h"
#include <math.h>

TilePicker::TilePicker(int x, int y, int width, int height) : Window(x, y, width, height)
{
	tilesheet = new Bitmap("Terrain.png");
	selectionRect = new Bitmap("SelectionRect.png");

	tilesheetRect.x = 0;
	tilesheetRect.y = 0;

	tilesheetRect.w = tilesheet->size.x * zoom;
	tilesheetRect.h = tilesheetRect.w;

	selRectPos.x = 0;
	selRectPos.y = 0;
}



void TilePicker::Update()
{
	int mouseX = Mouse::position.x - position.x + 2;
	int mouseY = Mouse::position.y - position.y + 2;

	//Actual size of tiles on the screen
	int screenTileWidth = tileWidth / (tilesheetRect.w / (hitbox.w - 4));
	int screenTileHeight = tileHeight / (tilesheetRect.h / (hitbox.h - 4));

	selectionRectRect.w = screenTileWidth;
	selectionRectRect.h = screenTileHeight;
	selectionRectRect.x = selRectPos.x + position.x + 2;
	selectionRectRect.y = selRectPos.y + position.y + 2;

	if (clicked)
	{
		int x = floor(mouseX / screenTileWidth);
		int y = floor(mouseY / screenTileHeight);
		selected = x + y * ((tilesheetRect.w / zoom)/tileWidth);

		selRectPos.x = x * screenTileWidth;
		selRectPos.y = y * screenTileHeight;
	}

	Rect destRect = { position.x + 2, position.y + 2, hitbox.w - 4, hitbox.h - 4 };
	tilesheet->Blit(&tilesheetRect, &destRect);

	selectionRect->Blit(selectionRect->rect, &selectionRectRect);

	position.x = WolfEngine::scene->camera->width - hitbox.w;
}
