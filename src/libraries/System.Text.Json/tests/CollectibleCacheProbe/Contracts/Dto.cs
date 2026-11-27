// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CollectibleCacheProbe;

public class DefaultBase
{
    public string Inherited { get; set; } = "base";
    public int BaseField = 7;
}

public class Pair<TLeft, TRight>
{
    public TLeft? Left { get; set; }
    public TRight? Right { get; set; }
}

public class Nest<T>
{
    public T? Value { get; set; }
}
