cmake_minimum_required(VERSION 2.6)
project(WolfEngine)

# Bump up warning levels appropriately for clang, gcc & msvc
# Also set debug/optimization flags depending on the build type. IDE users choose this when
# selecting the build mode in their IDE
if (${CMAKE_CXX_COMPILER_ID} STREQUAL "GNU" OR ${CMAKE_CXX_COMPILER_ID} STREQUAL "Clang")
	set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wall -Wextra -pedantic -std=c++11")
	set(CMAKE_CXX_FLAGS_DEBUG "${CMAKE_CXX_FLAGS} -g")
	set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS} -O2")
elseif (${CMAKE_CXX_COMPILER_ID} STREQUAL "MSVC")
	if (CMAKE_CXX_FLAGS MATCHES "/W[0-4]")
		string(REGEX REPLACE "/W[0-4]" "/W4" CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS}")
	else()
		set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} /W4")
	endif()
endif()

# Use our modified FindSDL2 modules
set(CMAKE_MODULE_PATH ${CMAKE_MODULE_PATH} "${WolfEngine_SOURCE_DIR}/cmake")

# Look up SDL2 and add the include directory to our include path
find_package(SDL2 REQUIRED)
include_directories(${SDL2_INCLUDE_DIR})
find_package(SDL2_image REQUIRED)
include_directories(${SDL2_IMAGE_INCLUDE_DIR})
find_package(SDL2_ttf REQUIRED)
include_directories(${SDL2_TTF_INCLUDE_DIR})
find_package(SDL2_mixer REQUIRED)
include_directories(${SDL2_MIXER_INCLUDE_DIR})

# Set an output directory for our binaries
set(BIN_DIR ${WolfEngine_SOURCE_DIR}/bin)

# Scripts
file(GLOB Scripts "Assets/Scripts/*.ws")
source_group("Scripts" FILES ${Scripts})

# Editor
file(GLOB Editor_Source "Code/Editor/*.cpp" "Code/Editor/*.h")
# source_group("Editor" FILES ${Editor_Source})

# Game
file(GLOB Game_Source "Code/Game/*.cpp" "Code/Game/*.h")
# source_group("Game" FILES ${Game_Source})

# Engine
file(GLOB WolfEngine_Source "Code/WolfEngine/*.cpp" "Code/WolfEngine/*.h")
# source_group("WolfEngine" FILES ${WolfEngine_Source})
# Audio
file(GLOB WolfEngine_Audio "Code/WolfEngine/Audio/*.cpp" "Code/WolfEngine/Audio/*.h")
source_group("Audio" FILES ${WolfEngine_Audio})
# Components
file(GLOB WolfEngine_Components "Code/WolfEngine/Components/*.cpp" "Code/WolfEngine/Components/*.h")
source_group("Components" FILES ${WolfEngine_Components})
# ECS
file(GLOB WolfEngine_ECS "Code/WolfEngine/ECS/*.cpp" "Code/WolfEngine/ECS/*.h")
source_group("ECS" FILES ${WolfEngine_ECS})
# GUI
file(GLOB WolfEngine_GUI "Code/WolfEngine/GUI/*.cpp" "Code/WolfEngine/GUI/*.h")
source_group("GUI" FILES ${WolfEngine_GUI})
# Input
file(GLOB WolfEngine_Input "Code/WolfEngine/Input/*.cpp" "Code/WolfEngine/Input/*.h")
source_group("Input" FILES ${WolfEngine_Input})
# Models
file(GLOB WolfEngine_Models "Code/WolfEngine/Models/*.cpp" "Code/WolfEngine/Models/*.h")
source_group("Models" FILES ${WolfEngine_Models})
# Rendering
file(GLOB WolfEngine_Rendering "Code/WolfEngine/Rendering/*.cpp" "Code/WolfEngine/Rendering/*.h")
source_group("Rendering" FILES ${WolfEngine_Rendering})
# Scripting
	file(GLOB WolfEngine_Scripting "Code/WolfEngine/Scripting/*.cpp" "Code/WolfEngine/Scripting/*.h")
	source_group("Scripting" FILES ${WolfEngine_Scripting})
	
	#add_on
	file(GLOB WolfEngine_Scripting_Addon_Debugger "Code/WolfEngine/Scripting/add_on/debugger/*.cpp" "Code/WolfEngine/Scripting/add_on/debugger/*.h")
	source_group("Scripting\\add_on\\debugger" FILES ${WolfEngine_Scripting_Addon_Debugger})
	
	file(GLOB WolfEngine_Scripting_Addon_ScriptArray "Code/WolfEngine/Scripting/add_on/scriptarray/*.cpp" "Code/WolfEngine/Scripting/add_on/scriptarray/*.h")
	source_group("Scripting\\add_on\\scriptarray" FILES ${WolfEngine_Scripting_Addon_ScriptArray})
	
	file(GLOB WolfEngine_Scripting_Addon_ScriptBuilder "Code/WolfEngine/Scripting/add_on/scriptbuilder/*.cpp" "Code/WolfEngine/Scripting/add_on/scriptbuilder/*.h")
	source_group("Scripting\\add_on\\scriptbuilder" FILES ${WolfEngine_Scripting_Addon_ScriptBuilder})
	
	file(GLOB WolfEngine_Scripting_Addon_ScriptFile "Code/WolfEngine/Scripting/add_on/scriptfile/*.cpp" "Code/WolfEngine/Scripting/add_on/scriptfile/*.h")
	source_group("Scripting\\add_on\\scriptfile" FILES ${WolfEngine_Scripting_Addon_ScriptFile})
	
	file(GLOB WolfEngine_Scripting_Addon_ScriptHelper "Code/WolfEngine/Scripting/add_on/scripthelper/*.cpp" "Code/WolfEngine/Scripting/add_on/scripthelper/*.h")
	source_group("Scripting\\add_on\\scripthelper" FILES ${WolfEngine_Scripting_Addon_ScriptHelper})
	
	file(GLOB WolfEngine_Scripting_Addon_ScriptMath "Code/WolfEngine/Scripting/add_on/scriptmath/*.cpp" "Code/WolfEngine/Scripting/add_on/scriptmath/*.h")
	source_group("Scripting\\add_on\\scriptmath" FILES ${WolfEngine_Scripting_Addon_ScriptMath})
	
	file(GLOB WolfEngine_Scripting_Addon_ScriptStdString "Code/WolfEngine/Scripting/add_on/scriptstdstring/*.cpp" "Code/WolfEngine/Scripting/add_on/scriptstdstring/*.h")
	source_group("Scripting\\add_on\\scriptstdstring" FILES ${WolfEngine_Scripting_Addon_ScriptStdString})
	
	#AngelScript
	file(GLOB WolfEngine_Scripting_AngelScript "Code/WolfEngine/Scripting/AngelScript/*.cpp" "Code/WolfEngine/Scripting/AngelScript/*.h")
	source_group("Scripting\\AngelScript" FILES ${WolfEngine_Scripting_AngelScript})
	
	#Include
	file(GLOB WolfEngine_Scripting_Include "Code/WolfEngine/Scripting/Include/*.cpp" "Code/WolfEngine/Scripting/Include/*.h")
	source_group("Scripting\\Include" FILES ${WolfEngine_Scripting_Include})
	
	
# Utilities
file(GLOB WolfEngine_Utilities "Code/WolfEngine/Utilities/*.cpp" "Code/WolfEngine/Utilities/*.h")
source_group("Utilities" FILES ${WolfEngine_Utilities})

add_library(WolfEngine STATIC
	${WolfEngine_Source}
	${WolfEngine_Audio}
	${WolfEngine_Components}
	${WolfEngine_ECS}
	${WolfEngine_GUI}
	${WolfEngine_Input}
	${WolfEngine_Models}
	${WolfEngine_Rendering}
	${WolfEngine_Scripting}
	
		${WolfEngine_Scripting_Addon_Debugger}
		${WolfEngine_Scripting_Addon_ScriptArray}
		${WolfEngine_Scripting_Addon_ScriptBuilder}
		${WolfEngine_Scripting_Addon_ScriptFile}
		${WolfEngine_Scripting_Addon_ScriptHelper}
		${WolfEngine_Scripting_Addon_ScriptMath}
		${WolfEngine_Scripting_Addon_ScriptStdString}
		${WolfEngine_Scripting_AngelScript}
		${WolfEngine_Scripting_Include}
		
	${WolfEngine_Utilities}
)

add_executable(Editor 
	${Editor_Source} 	
)

add_executable(Runtime
	${Scripts}
	${Game_Source}
)

target_link_libraries(WolfEngine ${SDL2_LIBRARY} ${SDL2_IMAGE_LIBRARY} ${SDL2_TTF_LIBRARY} ${SDL2_MIXER_LIBRARY})
target_link_libraries(Editor WolfEngine ${SDL2_LIBRARY} ${SDL2_IMAGE_LIBRARY} ${SDL2_TTF_LIBRARY} ${SDL2_MIXER_LIBRARY})
target_link_libraries(Runtime WolfEngine ${SDL2_LIBRARY} ${SDL2_IMAGE_LIBRARY} ${SDL2_TTF_LIBRARY} ${SDL2_MIXER_LIBRARY})
install(TARGETS Editor Runtime WolfEngine 
RUNTIME DESTINATION ${BIN_DIR}
ARCHIVE DESTINATION ${BIN_DIR}
)



#Unit tests
file(GLOB Tests "Tests/*.cpp" "Tests/*.h")
add_executable(Tests ${Tests})