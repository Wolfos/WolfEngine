LOCAL_PATH := $(call my-dir)

include $(CLEAR_VARS)

LOCAL_MODULE := main

SDL_PATH := jni/SDL
SDL_IMAGE_PATH := jni/SDL2_image
SDL_TTF_PATH := jni/SDL2_ttf
SDL_MIXER_PATH := jni/SDL2_mixer

CODE_PATH := ../Code

LOCAL_C_INCLUDES := $(LOCAL_PATH)/$(SDL_PATH)/include \ $(LOCAL_PATH)/$(SDL_IMAGE_PATH) \ $(LOCAL_PATH)/$(SDL_TTF_PATH) \ $(LOCAL_PATH)/$(SDL_MIXER_PATH)

WOLFENGINE_FILES := $(CODE_PATH)/WolfEngine/Main_SDL.cpp \
$(CODE_PATH)/WolfEngine/Game.cpp \
$(CODE_PATH)/WolfEngine/Audio/Sound.cpp \
$(CODE_PATH)/WolfEngine/Audio/Music.cpp \
$(CODE_PATH)/WolfEngine/Components/Button.cpp \
$(CODE_PATH)/WolfEngine/Components/Camera.cpp \
$(CODE_PATH)/WolfEngine/Components/SpriteRenderer.cpp \
$(CODE_PATH)/WolfEngine/Components/Transform.cpp \
$(CODE_PATH)/WolfEngine/ECS/GameObject.cpp \
$(CODE_PATH)/WolfEngine/ECS/Scene.cpp \
$(CODE_PATH)/WolfEngine/GUI/GUI.cpp \
$(CODE_PATH)/WolfEngine/GUI/Window.cpp \
$(CODE_PATH)/WolfEngine/Input/Input.cpp \
$(CODE_PATH)/WolfEngine/Input/Keyboard.cpp \
$(CODE_PATH)/WolfEngine/Input/Mouse.cpp \
$(CODE_PATH)/WolfEngine/Models/WPoint.cpp \
$(CODE_PATH)/WolfEngine/Rendering/Bitmap.cpp \
$(CODE_PATH)/WolfEngine/Rendering/Map.cpp \
$(CODE_PATH)/WolfEngine/Utilities/Debug.cpp \
$(CODE_PATH)/WolfEngine/Utilities/Time.cpp 

# Add your application source files here...
LOCAL_SRC_FILES := $(SDL_PATH)/src/main/android/SDL_android_main.c \
	$(WOLFENGINE_FILES) \
	$(CODE_PATH)/Game/GameMain.cpp \
	$(CODE_PATH)/Editor/EditorMain.cpp \
	$(CODE_PATH)/Editor/TilePicker.cpp

LOCAL_SHARED_LIBRARIES := SDL2 SDL2_image SDL2_ttf SDL2_mixer

LOCAL_LDLIBS := -lGLESv1_CM -llog

include $(BUILD_SHARED_LIBRARY)
