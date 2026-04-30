using System.Collections.Generic;

namespace Sample.Generics;

public class Repository<TKey, TValue> where TKey : notnull
{
    public TValue? Find<TArg>(TKey key, TArg arg) where TArg : class
    {
        return default;
    }
}
