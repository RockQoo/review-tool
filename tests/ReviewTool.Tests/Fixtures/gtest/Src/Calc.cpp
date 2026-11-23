#include "Calc.h"

int Calc::Add(int a, int b) const
{
    return a + b;
}

Calc& Calc::operator<<(int value)
{
    sum += Twice(value);
    return *this;
}

int Twice(int value)
{
    return value * 2;
}
