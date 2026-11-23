#pragma once

// 분석 범위 밖(외부) 헤더 예. 같은 헤더에 이름이 같은 함수가 두 개 있다.
namespace left
{
    inline void Send()
    {
    }
}

namespace right
{
    inline void Send()
    {
    }
}
