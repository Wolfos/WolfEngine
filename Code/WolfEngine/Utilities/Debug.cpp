/*
WolfEngine © 2013-2014 Robin van Ee
http://wolfengine.net
Contact:
rvanee@wolfengine.net
*/
#include "Debug.h"
#ifdef __ANDROID__
	#include <android/log.h>
#else
	#include <stdio.h>
#endif
void Debug::Log(char* text, ...)
{
	#ifdef __ANDROID__
		__android_log_write(ANDROID_LOG_DEBUG, "WolfEngine", text);
	#else
		printf(text);
	#endif
}