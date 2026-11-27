// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CollectibleCacheProbe.Fixture;

public class Dto : DefaultBase
{
    public string Text { get; set; } = "collectible";
    public int Number = 42;
}

public class Matrix<T0, T1, T2> : DefaultBase
{
    public string Text { get; set; } = "matrix";
    public int Number = 23;
}

public class SmallConstructor
{
    public SmallConstructor(string text, int number) => (Text, Number) = (text, number);
    public string Text { get; }
    public int Number { get; }
}

public class LargeConstructor
{
    public LargeConstructor(int a, int b, int c, int d, string e) => (A, B, C, D, E) = (a, b, c, d, e);
    public int A { get; }
    public int B { get; }
    public int C { get; }
    public int D { get; }
    public string E { get; }
}
