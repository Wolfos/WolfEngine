#ifndef _SCRIPT_H
#define _SCRIPT_H
#include "../ECS/Component.h"

///
///	Component for AngelScript scripts
///
class Script : public Component
{
public:
	virtual void Added();
	virtual void Update();
};
#endif