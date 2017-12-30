#ifndef _DEBUG_H
#define _DEBUG_H
#include <string>
///
///	Contains functions useful for debugging
///
class Debug
{
	public:
		///	Logs a C string
		/// Works with LogCat on Android, printf on other platforms
		static void Log(char* text, ...);
        static void Log(std::string text);
};
#endif
