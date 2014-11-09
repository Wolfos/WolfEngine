#include "Script.h"
#include <fstream>

bool Script::Exists(const std::string& name) {
	std::ifstream f(name.c_str());
	if (f.good()) {
		f.close();
		return true;
	}
	else {
		f.close();
		return false;
	}
}

Script::Script(std::string filename)
{
	builder = WolfEngine::scripter->builder;
	engine = builder->GetEngine();

	//Check if script file exists
	if (!Exists(("../Assets/Scripts/" + filename)))
	{
		printf("Fatal error: Script %d does not exist\n", ("../Assets/Scripts/" + filename).c_str());
		return;
	}

	//Compile the script
	builder->AddSectionFromFile(("../Assets/Scripts/" + filename).c_str());
	//Script failed to compile
	if (builder->BuildModule())
	{
		printf("Fatal error: Your script failed to compile, please fix.\n");
		return;
	}

	asIScriptModule* module = builder->GetModule();

	std::string classname = filename.substr(0, filename.size() - 3); //Remove '.ws' from the filename to get the classname

	asIObjectType *type = engine->GetObjectTypeById(module->GetTypeIdByDecl(classname.c_str()));

	classname += " @" + classname + "()";

	asIScriptFunction *factory = type->GetFactoryByDecl(classname.c_str());
	context = engine->CreateContext();

	context->Prepare(factory);
	context->Execute();

	script = *(asIScriptObject**)context->GetAddressOfReturnValue();
	script->AddRef();

	added = type->GetMethodByDecl("void Added()");
	update = type->GetMethodByDecl("void Update()");
}

void Script::Added()
{
	context->Prepare(added);
	context->SetObject(script);
	context->Execute();
}

void Script::Update()
{
	context->Prepare(update);
	context->SetObject(script);
	context->Execute();
}