#include "GameHandler.h"

void PushMsg(EGameMsg msg, int owner) { (void)msg; (void)owner; }
void PlayerCache::Update(int id) { (void)id; }
void Wallet::Spend(int id, int amount) { (void)id; (void)amount; }
void Inventory::AddItem(int id) { (void)id; }
void Mutex::lock() {}
void Mutex::unlock() {}

void GameHandler::Init()
{
    Register(&GameHandler::HandleEnter);
    Regist(EGameMsg::Tick, &GameHandler::OnTick);
}

void GameHandler::HandleEnter(Session& session, const L2GEnter& packet)
{
    cache_.Update(packet.id);
    GrantReward(session, packet.id);
    PushMsg(EGameMsg::Tick, packet.id);
    G2CEnterResult result{1};
    session.SendPacket(PacketId::G2CEnterResult, &result);
}

// 일부러 둔 문제 두 가지: 잠근 채 Flush(I/O), 차감 뒤 지급인데 되돌리는 곳 없음
void GameHandler::GrantReward(Session& session, int id)
{
    Wallet::Spend(id, 100);
    mutex_.lock();
    session.Flush();
    mutex_.unlock();
    Inventory::AddItem(id);
}

void GameHandler::OnTick(int owner)
{
    (void)owner;
    UnitOfWork::Commit();
}
