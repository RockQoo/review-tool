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

// 점검 규칙 예제용: 재화·인벤토리·뮤텍스
struct Wallet { static void Spend(int id, int amount); };
struct Inventory { static void AddItem(int id); };
struct Mutex
{
    void lock();
    void unlock();
};

class GameHandler : public HandlerBase
{
public:
    void Init();
    void HandleEnter(Session& session, const L2GEnter& packet);
    void OnTick(int owner);

private:
    void GrantReward(Session& session, int id);

    PlayerCache cache_;
    Mutex mutex_;
};
