using System;

namespace Sample.Contracts;

public interface IWorker : IDisposable
{
    void Run();
}

public abstract class WorkerBase : IWorker
{
    public abstract void Run();

    public virtual void Dispose()
    {
    }
}

public sealed class ConcreteWorker : WorkerBase, IWorker
{
    public override void Run()
    {
    }

    public override void Dispose()
    {
    }
}
