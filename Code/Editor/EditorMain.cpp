#include "EditorMain.h"


void EditorMain::Start()
{
	spritesheet = Image::Load("Terrain.png");
	gridtex = Image::Load("Grid.png");
	map = new Map(10, 10, 3, -1);

	grid = new Map(10, 10, 1, 0);

	tilewidth = 128;
	tileheight = 128;

	tilePicker = new TilePicker(Game::scene->camera->width - 256, 0, 256, 256);

	cam = camera->gameObject->transform;
}


void EditorMain::Update()
{
	int xMPos = (Mouse::position.x + cam->position.x) / tilewidth;
	int yMPos = (Mouse::position.y + cam->position.y) / tileheight;

	int selected = tilePicker->selected;

	map->Render(Game::renderer, 0, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(Game::renderer, 1, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
	map->Render(Game::renderer, 2, spritesheet, tilewidth, tileheight, 0, camera->gameObject);
		
	if (showGrid) grid->Render(Game::renderer, 0, gridtex, tilewidth, tileheight, 0, camera->gameObject);


	//Controls
	if (Mouse::KeyDown(1) && !tilePicker->mouseOver && Mouse::position.x + cam->position.x >= 0 && Mouse::position.x + cam->position.x <= map->width * tilewidth && Mouse::position.y + cam->position.y >= 0 && Mouse::position.y + cam->position.y <= map->height * tileheight)
	{
		map->Put(xMPos, yMPos, layer, selected);
	}

	if (Keyboard::KeyClicked(Keys::G)) showGrid = !showGrid;

	if (Keyboard::KeyClicked(Keys::A)) layer = 0;
	if (Keyboard::KeyClicked(Keys::B)) layer = 1;
	if (Keyboard::KeyClicked(Keys::C)) layer = 2;

	if (Keyboard::KeyDown(Keys::W)) cam->Move(0, -10);
	if (Keyboard::KeyDown(Keys::A)) cam->Move(-10, 0);
	if (Keyboard::KeyDown(Keys::S)) cam->Move(0, 10);
	if (Keyboard::KeyDown(Keys::D)) cam->Move(10, 0);
}

void EditorMain::Exit()
{

}