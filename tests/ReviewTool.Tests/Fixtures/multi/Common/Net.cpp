#include "Net.h"

void Session::SendPacket(PacketId id, const void* data) { (void)id; (void)data; }
void Db::Query(const char* sp) { (void)sp; }
void UnitOfWork::Commit() {}
void Config::Load() {}
void Listener::Listen() {}
