using System;
using System.Threading;
using System.Threading.Tasks.Dataflow;

public class STContext : SynchronizationContext, IDisposable
{
    private readonly object _lock = new object();
    private readonly BufferBlock<WorkItem> _queue = new BufferBlock<WorkItem>();

    public STContext()
    {
        RunQueue();
    }

    public void Execute(Action action)
    {
        using (Switcher.Switch(this))
            lock (_lock)
                action();
    }

    public T Execute<T>(Func<T> action)
    {
        using (Switcher.Switch(this))
            lock (_lock)
                return action();
    }

    public override void Send(SendOrPostCallback d, object state)
    {
        using (Switcher.Switch(this))
            lock (_lock)
                d(state);
    }

    public override void Post(SendOrPostCallback d, object state)
    {
        _queue.Post(new WorkItem(d, state));
    }

    private async void RunQueue()
    {
        while (await _queue.OutputAvailableAsync())
        {
            var wi = await _queue.ReceiveAsync();

            using (Switcher.Switch(this))
                do
                {
                    lock (_lock)
                        wi.Execute();
                } while (_queue.TryReceive(out wi));
        }
    }

    public void Dispose() => _queue.Complete();

    private class WorkItem
    {
        private readonly SendOrPostCallback d;
        private readonly object state;

        public WorkItem(SendOrPostCallback d, object state)
        {
            this.d = d;
            this.state = state;
        }

        public void Execute() => d(state);
    }

    private struct Switcher : IDisposable
    {
        private readonly SynchronizationContext old;

        private Switcher(SynchronizationContext old) { this.old = old; }

        public static Switcher Switch(SynchronizationContext target)
        {
            var old = Current;
            SetSynchronizationContext(target);
            return new Switcher(old);
        }

        public void Dispose() => SetSynchronizationContext(old);
    }
}
