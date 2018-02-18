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
find_package(SDL2_ttf REQUIRED)
include_directories(${SDL2_TTF_INCLUDE_DIR})
find_package(SDL2_mixer REQUIRED)
include_directories(${SDL2_MIXER_INCLUDE_DIR})

find_package(OpenGL)
include_directories(${OPENGL_INCLUDE_DIRS})

# Set an output directory for our binaries
set(BIN_DIR ${WolfEngine_SOURCE_DIR}/bin)

# Editor
file(GLOB Editor_Source "Code/Editor/*.cpp" "Code/Editor/*.h")
# source_group("Editor" FILES ${Editor_Source})

# Test application
file(GLOB Test_Source "Code/Test/*.cpp" "Code/Test/*.h")
# source_group("Test" FILES ${Test_Source})

# Shaders
file(GLOB Shaders "Assets/Shaders/*.vert" "Assets/Shaders/*.frag")

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
# Math
file(GLOB WolfEngine_Models "Code/WolfEngine/Math/*.cpp" "Code/WolfEngine/Math/*.h")
source_group("Models" FILES ${WolfEngine_Models})
# Rendering
file(GLOB WolfEngine_Rendering "Code/WolfEngine/Rendering/*.cpp" "Code/WolfEngine/Rendering/*.h")
source_group("Rendering" FILES ${WolfEngine_Rendering})
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
		${WolfEngine_Utilities}
		${Shaders}
		)

add_executable(Editor 
	${Editor_Source} 	
)

add_executable(Test
	${Test_Source}
)

target_link_libraries(WolfEngine ${OPENGL_LIBRARIES} ${SDL2_LIBRARY} ${SDL2_TTF_LIBRARY} ${SDL2_MIXER_LIBRARY})
target_link_libraries(Editor WolfEngine)
target_link_libraries(Test WolfEngine)
install(TARGETS Editor WolfEngine 
RUNTIME DESTINATION ${BIN_DIR}
ARCHIVE DESTINATION ${BIN_DIR}
)

