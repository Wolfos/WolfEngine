#include "ScriptMain.h"
#include <assert.h>
#include "add_on/scriptstdstring/scriptstdstring.h"

void ScriptMain::MessageCallback(const asSMessageInfo *msg, void *param)
{
	const char *type = "ERR ";
	if (msg->type == asMSGTYPE_WARNING)
		type = "WARN";
	else if (msg->type == asMSGTYPE_INFORMATION)
		type = "INFO";
	printf("%s (%d, %d) : %s : %s\n", msg->section, msg->row, msg->col, type, msg->message);
}

void ScriptMain::Print(std::string &msg)
{
	printf("%s", msg.c_str());
}

int ScriptMain::Init()
{
	// Create the script engine
	asIScriptEngine *engine = asCreateScriptEngine(ANGELSCRIPT_VERSION);

	// Set the message callback to receive information on errors in human readable form.
	int r = engine->SetMessageCallback(asFUNCTION(MessageCallback), 0, asCALL_CDECL); assert(r >= 0);

	// AngelScript doesn't have a built-in string type, as there is no definite standard
	// string type for C++ applications. Every developer is free to register it's own string type.
	// The SDK do however provide a standard add-on for registering a string type, so it's not
	// necessary to implement the registration yourself if you don't want to.
	RegisterStdString(engine);

	r = engine->RegisterGlobalFunction("void Print(const string &in)", asFUNCTION(Print), asCALL_CDECL); assert(r > 0);

	// The CScriptBuilder helper is an add-on that loads the file,
	// performs a pre-processing pass if necessary, and then tells
	// the engine to build a script module.
	builder = new CScriptBuilder();

	r = builder->StartNewModule(engine, "Components");
	if (r < 0)
	{
		// If the code fails here it is usually because there
		// is no more memory to allocate the module
		printf("Fatal error: could not start a new scripting module.\n");
		return 1;
	}

	return 0;
}

ScriptMain::~ScriptMain()
{
	delete builder;
}