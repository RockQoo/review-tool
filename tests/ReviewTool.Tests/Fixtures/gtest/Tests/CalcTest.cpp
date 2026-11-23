#include "Calc.h"

// 진짜 gtest 없이 같은 모양만 흉내 낸다: TEST(Suite, Name) 이 클래스와 TestBody 를 만들고 뒤에 본문이 붙는다.
#define TEST(suite, name)                                                                                              \
    class suite##_##name##_Test                                                                                        \
    {                                                                                                                  \
    public:                                                                                                            \
        void TestBody();                                                                                               \
    };                                                                                                                 \
    void suite##_##name##_Test::TestBody()
#define EXPECT_EQ(a, b) ((void)((a) == (b)))

TEST(Calc, Adds)
{
    const Calc calc;
    EXPECT_EQ(calc.Add(1, 2), 3); // 주석 속 Twice(1) 은 호출이 아니다
}

TEST(Calc, Streams)
{
    Calc calc;
    calc << 5;
    EXPECT_EQ(Twice(4), 8);
    const char* text = "Add(1, 2)";
    (void)text;
}
