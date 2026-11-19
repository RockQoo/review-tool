#pragma once
#include "Net.h"

class LoginHandler : public HandlerBase
{
public:
    void Init();
    void HandleLogin(Session& session, const C2LLogin& packet);

private:
    Session gameSession_;
    int count_ = 0;
};
