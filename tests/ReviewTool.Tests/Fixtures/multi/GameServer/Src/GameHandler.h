#pragma once
#include "Net.h"

enum class EGameMsg { Tick };

void PushMsg(EGameMsg msg, int owner);
template <typename F>
void Regist(EGameMsg msg, F handler) { (void)msg; (void)handler; }

class PlayerCache
{
public:
    void Update(int id);
};

class GameHandler : public HandlerBase
{
public:
    void Init();
    void HandleEnter(Session& session, const L2GEnter& packet);
    void OnTick(int owner);

private:
    PlayerCache cache_;
};
