#include "World.h"

void World::Move(int session, int x, int y)
{
    if (x < 0 || y < 0)
    {
        return;
    }
    Broadcast(session);
}

void World::Broadcast(int session)
{
    (void)session;
}
