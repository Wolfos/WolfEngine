#include "Editor.h"


void Editor::Start()
{
	spritesheet = new Bitmap("Terrain.png");
	gridtex = new Bitmap("Grid.png");
	topBar = new Bitmap("GUI/Bar.png");

	map = new Map(5, 5, 3, -1);
	grid = new Map(5, 5, 1, 0);

	tilewidth = 128;
	tileheight = 128;

	tilePicker = new TilePicker(camera->width - 256, 32, 256, 256);

	cam = camera->gameObject->transform;
	cam->position.y -= 32;
}


void Editor::Update()
{
	int xMPos = (Mouse::position.x + cam->position.x) / tilewidth;
	int yMPos = (Mouse::position.y + cam->position.y) / tileheight;

	int selected = tilePicker->selected;

	map->Render(0, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(1, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(2, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
		
	if (showGrid) grid->Render(0, gridtex, tilewidth, tileheight, 0, camera->gameObject);

	Rect tbRect = { 0, 0, camera->width, 32 };
	topBar->Blit(topBar->rect, &tbRect);

	//Controls
	if (Mouse::KeyDown(1) && !tilePicker->mouseOver && Mouse::position.x + cam->position.x >= 0 && Mouse::position.x + cam->position.x <= map->width * tilewidth && Mouse::position.y + cam->position.y >= 0 && Mouse::position.y + cam->position.y <= map->height * tileheight)
	{
		if(!Mouse::overGUI) map->Put(xMPos, yMPos, layer, selected);
	}
	if (Mouse::KeyDown(2) && !tilePicker->mouseOver && Mouse::position.x + cam->position.x >= 0 && Mouse::position.x + cam->position.x <= map->width * tilewidth && Mouse::position.y + cam->position.y >= 0 && Mouse::position.y + cam->position.y <= map->height * tileheight)
	{
		if (!Mouse::overGUI) map->Put(xMPos, yMPos, layer, -1);
	}

	if (Keyboard::KeyClicked(Keys::G)) showGrid = !showGrid;

	if (Keyboard::KeyClicked(Keys::A)) layer = 0;
	if (Keyboard::KeyClicked(Keys::B)) layer = 1;
	if (Keyboard::KeyClicked(Keys::C)) layer = 2;

	if (Keyboard::KeyDown(Keys::W)) cam->Move(0, -10);
	if (Keyboard::KeyDown(Keys::A)) cam->Move(-10, 0);
	if (Keyboard::KeyDown(Keys::S)) cam->Move(0, 10);
	if (Keyboard::KeyDown(Keys::D)) cam->Move(10, 0);

	if (Keyboard::KeyDown(Keys::P)) map->Write("Map.wolfmap");
	if (Keyboard::KeyDown(Keys::L)) map->Load("Map.wolfmap");
}

void Editor::Exit()
{

}