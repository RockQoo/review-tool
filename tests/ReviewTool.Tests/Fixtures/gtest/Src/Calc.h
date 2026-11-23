#pragma once

// gtest 진입점 예제의 라이브러리 쪽. 테스트가 부르는 함수들.
struct Calc
{
    int Add(int a, int b) const;
    Calc& operator<<(int value);

    int sum{};
};

int Twice(int value);

template <typename T>
struct Box
{
    T value{};
};

template <typename T>
int Count(int n)
{
    return n;
}
