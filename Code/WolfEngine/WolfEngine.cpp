#include "WolfEngine.h"
#include "GUI/GUI.h"
#include "Input/Input.h"
#include "Utilities/Time.h"
#ifdef __APPLE__
#include "CoreFoundation/CoreFoundation.h"
#endif

using namespace WolfEngine;

int WolfEngine::maxFPS = 60;
int WolfEngine::screenWidth = 1280;
int WolfEngine::screenHeight = 720;

SDL_Window* WolfEngine::window;
Scene* WolfEngine::scene;
SDL_GLContext WolfEngine::context;


int InitSDL()
{
	//Initialize SDL
	if (SDL_Init(SDL_INIT_EVENTS | SDL_INIT_VIDEO | SDL_INIT_AUDIO) < 0)
    {
        printf("SDL could not initialize! SDL_Error: %s\n", SDL_GetError());
        return 1;
    }
	// Request OpenGL 3.3 context
	SDL_GL_SetAttribute(SDL_GL_CONTEXT_PROFILE_MASK, SDL_GL_CONTEXT_PROFILE_CORE);
	SDL_GL_SetAttribute(SDL_GL_CONTEXT_MAJOR_VERSION, 3);
	SDL_GL_SetAttribute(SDL_GL_CONTEXT_MINOR_VERSION, 3);

	//SDL_GL_SetAttribute(SDL_GL_DOUBLEBUFFER, 1);
	//SDL_GL_SetAttribute(SDL_GL_DEPTH_SIZE, 24);
#ifdef ANDROID
	// Find ideal screen resolution
	// Android's screen resolution isn't actually set by CreateWindow, but this also sets the camera's width and height correctly
	SDL_DisplayMode* mode = new SDL_DisplayMode;
	SDL_GetDisplayMode(0, 0, mode);
	screenWidth = mode->w;
	screenHeight = mode->h;
#endif
	window = SDL_CreateWindow("WolfEngine", SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, screenWidth, screenHeight, SDL_WINDOW_SHOWN | SDL_WINDOW_MAXIMIZED | SDL_WINDOW_RESIZABLE | SDL_WINDOW_OPENGL);
	if (window == NULL)
	{
		printf("Fatal error: Window could not be created! SDL_Error: %s\n", SDL_GetError());
		return 1;
	}
	SDL_GetWindowSize(window, &screenWidth, &screenHeight);

	// Create OpenGL context
	context = SDL_GL_CreateContext(window);
	printf("GL Version: %s\n", glGetString(GL_VERSION));
	printf("Shading language version: %s\n", glGetString(GL_SHADING_LANGUAGE_VERSION));

	// Initialize SDL_Image
	int imgflags = IMG_INIT_PNG;
	if (!(IMG_Init(imgflags) & imgflags))
	{
		printf("Fatal error: SDL_image could not initialize! SDL_image Error: %s\n", IMG_GetError());
		return 1;
	}

	// Initialize SDL_TTF
	if (TTF_Init())
	{
		printf("Fatal error: SDL_ttf could not initialize! SDL_ttf Error: %s\n", SDL_GetError());
		return 1;
	}

	if(Mix_OpenAudio(44100, MIX_DEFAULT_FORMAT, 2, 1024)==-1)
	{
		printf("Fatal error: SDL_Mixer could not initialize! SDL_mixer Error: %s\n", Mix_GetError());
		return 1;
	}

	// Initialize SDL_Mixer
	if (!Mix_Init(MIX_INIT_OGG))
	{
		printf("Fatal error: SDL_Mixer could not initialize! SDL_mixer Error: %s\n", Mix_GetError());
		return 1;
	}

    return 0;
}

int WolfEngine::Init()
{
    if (InitSDL()) return 1;
    
    //GUI::Init();
    
    return 0;
}

#include "Rendering/Shader.h"
#include "Rendering/Mesh.h"
void WolfEngine::MainLoop()
{
    int quit = 0;
    SDL_Event eventHandler;
    Uint32 curFrameTime = 0;
    Uint32 lastFrameTime = 0;
    Input input;

	//glEnable(GL_CULL_FACE);
	glEnable(GL_BLEND);
	glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
	Vector3<float> rotation = {0, 0, 0};

	Bitmap* bmp = new Bitmap("test.png");

    Shader* shader = new Shader( "Sprite.vert", "Sprite.frag" );

	Mesh* mesh = new Mesh();
	mesh->CreateQuad();

    while (!quit)
    {
        curFrameTime = SDL_GetTicks();
        
        Time::frameTimeS = (double)(curFrameTime - lastFrameTime) / 1000;
        
        // Clear the screen
		glClearColor ( 0.392, 0.584, 0.929, 1.0 );
		glClear ( GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT );

		//rotation.z += 0.01f;
		Quaternion* quat = Quaternion::FromEuler(rotation);
		//Quaternion* quat = new Quaternion();
		Matrix rot;
		rot.FromQuat(quat, {0, 0, 0});

		// Set shader
        glUseProgram(shader->id);

	    // Matrices
	    Matrix projection;
		float aspect = (float)screenWidth / (float)screenHeight;
		float size = 2;
		projection.SetOrtho(-aspect * size, aspect * size, -1 * size, 1 * size, 0.01f, 100.0f);

	    Matrix view;
	    view.SetIdentity();

	    Matrix model;
	    model.SetIdentity();
	    model.Translate({0, 0, -1});
		model = model * rot;

	    Matrix mvp = model * view * projection;

	    glUniformMatrix4fv(glGetUniformLocation(shader->id, "mvp"), 1, GL_FALSE, &mvp.GetData()[0]);


		glActiveTexture(GL_TEXTURE0);
		glBindTexture(GL_TEXTURE_2D, bmp->textureID);
		glUniform1i(glGetUniformLocation(shader->id,  "sampler"), 0);

		// Vertices
        glEnableVertexAttribArray(0);
        glBindBuffer(GL_ARRAY_BUFFER, mesh->vertexBuffer);
        glVertexAttribPointer(0, 3, GL_FLOAT, false, 0, (void*)0);

		// UVs
		glEnableVertexAttribArray(1);
		glBindBuffer(GL_ARRAY_BUFFER, mesh->uvBuffer);
		glVertexAttribPointer(1, 2, GL_FLOAT, false, 0, (void*)0);

		glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, mesh->indexBuffer);

		// Draw
		glDrawElements(GL_TRIANGLES, mesh->indices.size(), GL_UNSIGNED_INT, (void*)0);
		glDisableVertexAttribArray(0);
		glDisableVertexAttribArray(1);


        input.Update(&eventHandler);
        
        if (eventHandler.type == SDL_WINDOWEVENT_RESIZED)
        {
            scene->camera->width = eventHandler.window.data1;
            scene->camera->height = eventHandler.window.data2;
        }
        
        if (eventHandler.type == SDL_QUIT)
        {
            quit = 1;
        }
        
        //scene->Update();
        
        //Update the gameObjects
        //scene->UpdateObjects();
        
        //Render the SpriteRenderers
        //scene->RenderObjects();
        
        //Late update
        //scene->LateUpdateObjects();
        
        //OnGUI
        //scene->OnGUI();

        lastFrameTime = curFrameTime;

		SDL_GL_SwapWindow(window);
        
        if (maxFPS != -1 && SDL_GetTicks() - curFrameTime < 1000 / maxFPS)
            SDL_Delay((1000 / maxFPS) - (SDL_GetTicks() - curFrameTime));
    }
}

int WolfEngine::Quit()
{
    delete scene;

    SDL_DestroyWindow(window);
    
    SDL_Quit();
    
    return 0;
}


void Pause()
{
    //Pause all sounds and music
    Mix_Pause(-1);
    Mix_PauseMusic();
}

void Resume()
{
    //Resume all sounds and music
    Mix_Resume(-1);
    Mix_ResumeMusic();
}

#ifdef ANDROID
#include <jni.h>
extern "C"
{
    void Java_nl_rvanee_wolfengine_WolfEngine_Pause()
    {
        Pause();
    }
    void Java_nl_rvanee_wolfengine_WolfEngine_Resume()
    {
        Resume();
    }
}
#endif

// Utility functions
std::string WolfEngine::FindAssetFolder()
{
#ifdef __APPLE__
    //Find the resources folder
    CFBundleRef mainBundle = CFBundleGetMainBundle();
    CFURLRef resourcesURL = CFBundleCopyResourcesDirectoryURL(mainBundle);
    char path[PATH_MAX];
    CFURLGetFileSystemRepresentation(resourcesURL, TRUE, (UInt8 *)path, PATH_MAX);
    std::string cppPath(path);
    return cppPath + "/../Assets/";
#elif defined ANDROID
    return "";
#else
	return "../../Assets/";
#endif
}