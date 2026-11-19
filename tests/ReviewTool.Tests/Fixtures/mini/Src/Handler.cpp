#include "Handler.h"
#include "World.h"

Handler::Handler(World& world)
    : world_{world}
{
}

void Handler::Init()
{
    // 주석 속 예시는 진입점이 아니다: Register(&Handler::Init)
    Register(&Handler::HandleMove);
}

void Handler::HandleMove(int session, const C2SMove& packet)
{
    World::Log("move {");
    world_.Move(session, packet.x, packet.y);
}
