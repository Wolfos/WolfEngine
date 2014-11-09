#ifndef _SCRIPTMAIN_H
#define _SCRIPTMAIN_H

#include "include/angelscript.h"
#include "add_on/scriptbuilder/scriptbuilder.h"

class ScriptMain
{
	public:
		int Init();
		CScriptBuilder* builder;
		~ScriptMain();

	private:
		static void MessageCallback(const asSMessageInfo *msg, void *param);
		static void Print(std::string &msg);

};
#endif