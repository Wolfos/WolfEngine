#include "../WolfEngine/WolfEngine.h"
#include "../WolfEngine/API.h"

class Animal : public Component
{
public:
    virtual void Added()
    {
        SpriteRenderer* sr = gameObject->AddComponent<SpriteRenderer>();
        sr->Load("Animals.png", 286, 286);
        sr->frame = WolfEngine::RandomRange(0, 9);
        sr->widthInFrames = 2;

        float randomScale = WolfEngine::RandomRange(.04f, .1f);
		float randomAngle = WolfEngine::RandomRange(0.0f, 360.0f);
		float randomX = WolfEngine::RandomRange(-1.0f, 1.0f);
        float randomY = WolfEngine::RandomRange(-1.0f, 1.0f);
        gameObject->transform->localScale = {randomScale, randomScale, randomScale};
		gameObject->transform->Rotate({0,0,randomAngle});
        gameObject->transform->Translate({randomX, randomY, 1});
    }

    virtual void Update()
    {
		gameObject->transform->Rotate({0, 0, 10 * Time::frameTimeS});
    }
};

class TestScene : public Scene
{
public:
    void Start()
    {
        for(int i = 0; i < 50; i++)
        {
            GameObject* animal = new GameObject;
            animal->AddComponent<Animal>();
            AddGameObject(animal);
        }
    }

    int timer = 0;
    double totalFrameTime = 0;
    void Update()
    {
        timer++;
        totalFrameTime += Time::frameTimeS;
        if(timer == 10)
        {
            int averageFrameTime = ((totalFrameTime / 10) * 1000);
            printf("%d\n", averageFrameTime);
            totalFrameTime = 0;
            timer = 0;
        }
    }
};

int main(int argc, char* args[])
{
    if (WolfEngine::Init())
    {
        Debug::Log("WolfEngine has failed to initialize.\n");
        Debug::Log("¶¶¶¶¶¶¶¶¶_¶¶¶¶¶¶¶¶¶¶¶¶¶¶_¶¶¶¶    Wow!\n¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶_Ø¶¶¶¶¶\n¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶_Ø¶¶¶¶¶¶¶\n¶¶¶¶¶¶¶¶_Ø¶¶ØØØØ___Ø¶¶¶¶¶¶¶¶¶\n¶¶¶¶¶__Ø¶¶¶¶¶¶¶¶¶¶¶¶¶¶_¶¶¶¶¶¶\n¶¶¶_Ø¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶Ø¶¶Ø¶¶¶¶¶\n¶¶¶¶¶¶__¶¶¶¶¶¶¶¶¶¶¶¶¶¶Ø_¶¶¶¶¶        Much error :(\n¶¶¶¶¶¶¶Ø¶¶¶¶¶_Ø¶_¶¶¶¶¶¶¶¶¶¶¶¶\n¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶Ø¶¶¶¶¶¶¶¶Ø_¶¶\n¶¶¶¶_¶¶_¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶\nØ¶Ø¶_¶_¶¶_¶Ø¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶\n¶¶¶¶Ø¶Ø¶¶__¶_¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶\n¶¶¶¶ØØ__¶¶¶_¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶\n¶¶¶¶¶¶¶¶ØØØ¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶    Many wrong\n¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶_¶¶¶¶¶\n¶¶Ø_¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶_¶¶¶¶¶¶¶\n¶¶¶¶Ø_¶¶¶¶¶¶¶¶¶¶___Ø¶¶¶¶_Ø¶¶¶\n¶¶¶¶¶¶Ø______ØØØ¶¶¶¶¶__Ø¶¶¶¶¶\n¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶¶ØØ¶¶¶¶¶¶¶¶\n");
        return 1;
    }

    TestScene* scene = new TestScene();

    WolfEngine::scene = scene;

    scene->camera->width = WolfEngine::screenWidth;
    scene->camera->height = WolfEngine::screenHeight;

    scene->Start();

    WolfEngine::MainLoop();
    return 0;
}


