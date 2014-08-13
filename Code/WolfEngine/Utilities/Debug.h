#ifndef _DEBUG_H
#define _DEBUG_H
///
///	Contains functions useful for debugging
///
class Debug
{
	public:
		///	Logs a C string
		/// Works with LogCat on Android, printf on other platforms
		static void Log(char* text, ...);
};
#endif