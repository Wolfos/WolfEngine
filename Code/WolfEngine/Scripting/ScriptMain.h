#ifndef _SCRIPTMAIN_H
#define _SCRIPTMAIN_H

#include "include/angelscript.h"

class ScriptMain
{
	public:
		static int Init();
	private:
		static void MessageCallback(const asSMessageInfo *msg, void *param);
};
#endif