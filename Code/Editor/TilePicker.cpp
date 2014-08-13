#include "TilePicker.h"
#include <math.h>

TilePicker::TilePicker(int x, int y, int width, int height) : Window(x, y, width, height)
{
	tilesheet = Image::Load("Terrain.png");

	tilesheetRect.x = 0;
	tilesheetRect.y = 0;
	SDL_QueryTexture(tilesheet, NULL, NULL, &tilesheetRect.w, &tilesheetRect.h);

	tilesheetRect.w = tilesheetRect.w * zoom;
	tilesheetRect.h = tilesheetRect.w;
}



void TilePicker::Update()
{
	int mouseX = Mouse::position.x - position.x + 2;
	int mouseY = Mouse::position.y - position.y + 2;

	//Actual size of tiles on the screen
	int screentileWidth = tileWidth / (tilesheetRect.w / (hitbox.w - 4));
	int screentileHeight = tileHeight / (tilesheetRect.h / (hitbox.h - 4));

	if (clicked)
	{
		int x = floor(mouseX / screentileWidth);
		int y = floor(mouseY / screentileHeight);
		selected = x + y * ((tilesheetRect.w / zoom)/tileWidth);
	}

	SDL_Rect destRect = { position.x + 2, position.y + 2, hitbox.w - 4, hitbox.h - 4 };
	SDL_RenderCopy(Screen::mainCamera->screen, tilesheet, &tilesheetRect, &destRect);
}
