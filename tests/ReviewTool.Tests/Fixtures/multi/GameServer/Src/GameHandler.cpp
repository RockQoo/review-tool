#include "GameHandler.h"

void PushMsg(EGameMsg msg, int owner) { (void)msg; (void)owner; }
void PlayerCache::Update(int id) { (void)id; }

void GameHandler::Init()
{
    Register(&GameHandler::HandleEnter);
    Regist(EGameMsg::Tick, &GameHandler::OnTick);
}

void GameHandler::HandleEnter(Session& session, const L2GEnter& packet)
{
    cache_.Update(packet.id);
    PushMsg(EGameMsg::Tick, packet.id);
    G2CEnterResult result{1};
    session.SendPacket(PacketId::G2CEnterResult, &result);
}

void GameHandler::OnTick(int owner)
{
    (void)owner;
    UnitOfWork::Commit();
}
