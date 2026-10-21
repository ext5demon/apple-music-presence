namespace AppleMusicPresence;

// Keep apartment-bound WinRT proxies on one STA, with their own message pump.
// Synchronous Windows/Apple media calls must never block the settings window.
public sealed class MediaWorker : IMediaReader, IAsyncDisposable
{
    private sealed record Worker(Control Dispatcher, ApplicationContext Context, MediaReader Reader);
    private readonly TaskCompletionSource<Worker> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim readGate = new(1, 1);
    private int disposed;

    public MediaWorker()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Apple Music media reader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Run()
    {
        try
        {
            using var context = new ApplicationContext();
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ready.TrySetResult(new(dispatcher, context, new MediaReader()));
            Application.Run(context);
        }
        catch (Exception e) { ready.TrySetException(e); }
        finally { stopped.TrySetResult(); }
    }

    public async Task<MediaState> ReadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await readGate.WaitAsync(ct);
        var completion = new TaskCompletionSource<MediaState>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var worker = await ready.Task.WaitAsync(ct);
            worker.Dispatcher.BeginInvoke(async () =>
            {
                try { completion.TrySetResult(await worker.Reader.ReadAsync(ct)); }
                catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
                catch (Exception e) { completion.TrySetException(e); }
                finally { readGate.Release(); }
            });
        }
        catch { readGate.Release(); throw; }
        return await completion.Task.WaitAsync(ct);
    }

    public void Reset()
    {
        if (!ready.Task.IsCompletedSuccessfully || Volatile.Read(ref disposed) != 0) return;
        var worker = ready.Task.Result;
        try { worker.Dispatcher.BeginInvoke(worker.Reader.Reset); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            var worker = await ready.Task.WaitAsync(TimeSpan.FromSeconds(1));
            worker.Dispatcher.BeginInvoke(worker.Context.ExitThread);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
    }
}
