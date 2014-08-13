#ifndef _GAMEMAIN_H
#define _GAMEMAIN_H
#include "Editor\EditorMain.h"

class GameMain
{
	public:
		void Start();
		void Update();
		void Exit();
	private:
		bool isEditor;
		EditorMain editor;
};

#endif
