#include "GameMain.h"
#include "WolfEngine/WolfEngine.h"

void GameMain::Start()
{
	isEditor = true;
	if (isEditor) editor.Start();
}

void GameMain::Update()
{
	if (isEditor) editor.Update();
}

void GameMain::Exit()
{
	if (isEditor) editor.Exit();
}
