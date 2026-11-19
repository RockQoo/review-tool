#pragma once

class World
{
public:
    void Move(int session, int x, int y);
    void Broadcast(int session);
    static void Log(const char* text) { (void)text; }
};

template <typename F>
void Register(F f) { (void)f; }
