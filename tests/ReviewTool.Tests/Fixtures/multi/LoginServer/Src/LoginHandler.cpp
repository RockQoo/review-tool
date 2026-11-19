#include "LoginHandler.h"

void LoginHandler::Init()
{
    Register(&LoginHandler::HandleLogin);
}

void LoginHandler::HandleLogin(Session& session, const C2LLogin& packet)
{
    Db::Query("usp_Login");
    DbCommand load{"dbo.usp_LoadPlayer"};
    (void)load;
    L2GEnter enter{};
    enter.id = packet.id;
    SendPacket(gameSession_, enter);
    (void)session;
}
