#pragma once

struct C2SMove { int x; int y; };

class World;

class Handler
{
public:
    explicit Handler(World& world);
    void Init();
    void HandleMove(int session, const C2SMove& packet);

private:
    World& world_;
};
