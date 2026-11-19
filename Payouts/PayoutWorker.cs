namespace GramWalletService.Payouts;

internal sealed class PayoutWorker : BackgroundService
{
    private readonly PayoutProcessor _processor;
    private readonly PayoutStore _store;
    private readonly WalletSigningGate _gate;
    private readonly SingleWalletLock _lock;
    private readonly ILogger<PayoutWorker> _logger;

    public PayoutWorker(
        PayoutProcessor processor,
        PayoutStore store,
        WalletSigningGate gate,
        ILogger<PayoutWorker> logger)
    {
        _processor = processor;
        _store = store;
        _gate = gate;
        _logger = logger;
        _lock = new SingleWalletLock(logger);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Mutex.ReleaseMutex must run on the thread that called WaitOne.
        // A thread-pool hop throws ApplicationException and leaves the mutex owned forever.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Run(stoppingToken);
                completed.TrySetResult();
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "payout-worker"
        };
        thread.Start();
        return completed.Task;
    }

    private void Run(CancellationToken stoppingToken)
    {
        try
        {
            var paused = _store.IsPausedAsync(stoppingToken).GetAwaiter().GetResult();
            var (queued, review) = _store.CountsAsync(stoppingToken).GetAwaiter().GetResult();
            var inFlight = _store.InFlightAsync(stoppingToken).GetAwaiter().GetResult().Count;
            _logger.LogInformation(
                "Payout worker started. Signing {Signing}. Paused {Paused}. In-flight {InFlight}. Queued {Queued}. Needs review {NeedsReview}.",
                _gate.LocalOk ? "local key ready" : "disabled",
                paused,
                inFlight,
                queued,
                review);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payout store was not readable at startup ({Type}).", ex.GetType().Name);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(2);
                var holding = false;
                try
                {
                    holding = _lock.TryEnter();
                    if (!holding)
                    {
                        _logger.LogWarning("Another payout worker holds the wallet lock.");
                        delay = TimeSpan.FromSeconds(5);
                    }
                    else
                    {
                        _processor.RunOnceAsync(stoppingToken).GetAwaiter().GetResult();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Payout worker failed ({Type}).", ex.GetType().Name);
                    delay = TimeSpan.FromSeconds(5);
                }
                finally
                {
                    if (holding)
                        _lock.Exit();
                }

                if (stoppingToken.WaitHandle.WaitOne(delay))
                    break;
            }
        }
        finally
        {
            _lock.Dispose();
            _logger.LogInformation("Payout worker stopped.");
        }
    }
}

internal sealed class SingleWalletLock : IDisposable
{
    private readonly ILogger _logger;
    private readonly Mutex? _mutex;
    private int _disposed;

    public SingleWalletLock(ILogger logger)
    {
        _logger = logger;
        try
        {
            _mutex = new Mutex(false, "GramWalletService.V5R1.Payout");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Payout wallet lock could not be created ({Type}).", ex.GetType().Name);
            _mutex = null;
        }
    }

    public bool TryEnter()
    {
        if (_mutex is null || Volatile.Read(ref _disposed) != 0)
            return _mutex is null;

        try
        {
            return _mutex.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            _logger.LogWarning("Recovered an abandoned payout wallet lock.");
            return true;
        }
    }

    public void Exit()
    {
        if (_mutex is null)
            return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
        {
            _logger.LogError(ex, "Payout wallet lock release failed ({Type}).", ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _mutex?.Dispose();
    }
}
