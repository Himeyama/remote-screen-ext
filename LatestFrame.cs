namespace RemoteScreen;

sealed class LatestFrame
{
    readonly object _gate = new();
    TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ScreenFrame? _current;

    public void Publish(ScreenFrame frame)
    {
        TaskCompletionSource notification;
        lock (_gate)
        {
            _current = frame;
            notification = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        notification.TrySetResult();
    }

    public async ValueTask<ScreenFrame> AfterAsync(ScreenFrame? previous, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task change;
            lock (_gate)
            {
                if (_current is { } frame && !ReferenceEquals(previous, frame))
                {
                    return frame;
                }
                change = _changed.Task;
            }
            await change.WaitAsync(cancellationToken);
        }
    }
}
