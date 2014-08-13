#ifndef _WOLFTIME_H //Apparently _TIME_H is defined in the file <Time.h> from the default C library.
#define _WOLFTIME_H

///
///	Class with time based variables and functions
///
class Time
{
public:
	/// Time in seconds it took the last frame to render
	static double frameTimeS; 
};
#endif
