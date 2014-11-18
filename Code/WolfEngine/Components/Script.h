#ifndef _SCRIPT_H
#define _SCRIPT_H
#include "../ECS/Component.h"
#include <string>
#include "../Scripting/Include/angelscript.h"
#include "../Scripting/add_on/scriptbuilder/scriptbuilder.h"
#include "../WolfEngine.h"

///
///	Component for AngelScript scripts
///
class Script : public Component
{
	public:
		Script(std::string filename);

		virtual void Added();
		virtual void Update();

		~Script();

	private:
		bool Exists(const std::string& name);

		CScriptBuilder* builder;
		asIScriptEngine* engine;
		asIScriptObject* script;
		asIScriptContext* context;

		asIScriptFunction* added;
		asIScriptFunction* update;
};
#endif