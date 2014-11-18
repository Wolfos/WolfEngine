#ifndef _SCRIPTECS_H
#define _SCRIPTECS_H

#include "Include/angelscript.h"
#include "../ECS/GameObject.h"
#include "../ECS/Scene.h"

///
/// Registers the entity component system for scripts
///
class ScriptECS
{
	ScriptECS(asIScriptEngine* engine);
};

#endif