#pragma once

// 두 프로세스가 같이 쓰는 패킷·세션 (예제)
enum class PacketId { C2LLogin = 1, L2GEnter = 2, G2CEnterResult = 3 };

struct C2LLogin { int id; };
struct L2GEnter { int id; };
struct G2CEnterResult { int ok; };

class Session
{
public:
    void SendPacket(PacketId id, const void* data);
    void Flush();
};

template <typename T>
void SendPacket(Session& session, const T& packet)
{
    session.SendPacket(static_cast<PacketId>(0), &packet);
}

class HandlerBase
{
public:
    virtual ~HandlerBase() = default;
    template <typename F>
    void Register(F handler) { (void)handler; }
};

struct Db { static void Query(const char* sp); };
struct DbCommand { const char* sp; };
struct UnitOfWork { static void Commit(); };
struct Config { static void Load(); };
struct Listener { static void Listen(); };
