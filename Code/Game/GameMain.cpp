#include "GameMain.h"
#include "../WolfEngine/WolfEngine.h"

void GameMain::Create()
{
	editor = new EditorMain();
	Game::scene = editor;
	isEditor = true;
}

void GameMain::Start()
{
	if (isEditor) editor->Start();
}

void GameMain::Exit()
{
	if (isEditor) editor->Exit();
}
