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
	//TODO: Don't compile scripts when the script gets added for obvious reasons
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
	if (!type)
	{
		printf("Fatal error: Could not find class %s in file %s", classname, filename);
		return;
	}

	classname += " @" + classname + "()";

	//Factory returns a new object of our type (which is our script)
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

Script::~Script()
{
	//Decreases the object's refcount and releases if that number has reached 0
	script->Release();
}