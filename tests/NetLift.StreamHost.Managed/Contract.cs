using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Generic;
using NetLift;

[LiftDto("pulse", Id = 21, Version = 1)]
public sealed record Pulse([property: LiftField(1)] int Value);

[LiftService("feed", Id = 20, Version = 1)]
public interface IFeed
{
    [LiftStream(4)]
    IAsyncEnumerable<Pulse> Watch(CancellationToken cancellationToken);

    [LiftStream(6)]
    IAsyncEnumerable<Pulse> FailAfterOne(CancellationToken cancellationToken);

    [LiftEvent(5)]
    event Action<Pulse>? Changed;
}

internal sealed class Feed : IFeed
{
    private Action<Pulse>? _changed;
    private readonly ManualResetEventSlim _eventAddEntered = new();
    private readonly ManualResetEventSlim _allowEventAdd = new();
    private int _blockEventAdd;

    public event Action<Pulse>? Changed
    {
        add
        {
            if (Volatile.Read(ref _blockEventAdd) != 0)
            {
                _eventAddEntered.Set();
                _allowEventAdd.Wait();
            }
            _changed += value;
            for (var pulse = 1; pulse <= 65; pulse++)
                _changed?.Invoke(new Pulse(pulse));
        }
        remove => _changed -= value;
    }

    public async IAsyncEnumerable<Pulse> Watch(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var value = 1; value <= 3; value++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new Pulse(value);
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<Pulse> FailAfterOne(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new Pulse(77);
        await Task.Yield();
        throw new InvalidOperationException("Synthetic managed stream failure.");
    }

    public void Publish(Pulse pulse) => _changed?.Invoke(pulse);

    public void BlockNextEventAdd()
    {
        _eventAddEntered.Reset();
        _allowEventAdd.Reset();
        Volatile.Write(ref _blockEventAdd, 1);
    }

    public bool WaitForEventAdd(TimeSpan timeout) => _eventAddEntered.Wait(timeout);

    public void ReleaseEventAdd()
    {
        Volatile.Write(ref _blockEventAdd, 0);
        _allowEventAdd.Set();
    }
}
