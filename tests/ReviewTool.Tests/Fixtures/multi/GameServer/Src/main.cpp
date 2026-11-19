#include "GameHandler.h"

int main()
{
    Config::Load();
    GameHandler handler;
    handler.Init();
    Listener::Listen();
    return 0;
}
