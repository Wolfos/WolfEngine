#ifndef _GAMEMAIN_H
#define _GAMEMAIN_H
#include "../Editor/EditorMain.h"

class GameMain
{
	public:
		void Create();
		void Start();
		void Exit();
	private:
		bool isEditor;
		EditorMain* editor;
};

#endif
