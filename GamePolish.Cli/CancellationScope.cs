namespace GamePolish.Cli;

/// <summary>
/// Turns Ctrl-C into a cancelled <see cref="CancellationToken"/> rather than an abrupt exit, so the pipelines
/// can run their rollback.
/// </summary>
/// <remarks>
/// <para>
/// The handler sets <see cref="ConsoleCancelEventArgs.Cancel"/> and cancels the token; it never calls
/// <c>Environment.Exit</c>. That is the whole point: an install that is mid-swap must be allowed to put the old
/// addon folder back, and a process that dies inside the handler leaves a half-written addon in the folder the
/// game reads. A second Ctrl-C is left unhandled, because a user asking twice for a stuck process to stop means
/// it, and the escape hatch has to work.
/// </para>
/// <para>
/// Disposal unsubscribes: <see cref="Console.CancelKeyPress"/> is process-wide, and a handler that outlives its
/// command would cancel a token nobody is waiting on.
/// </para>
/// </remarks>
public sealed class CancellationScope : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly ConsoleCancelEventHandler _handler;
    private int _presses;

    /// <summary>Subscribes to Ctrl-C for the lifetime of this object.</summary>
    public CancellationScope()
    {
        _handler = (_, args) =>
        {
            if (Interlocked.Increment(ref _presses) > 1)
            {
                return;
            }

            args.Cancel = true;
            _source.Cancel();
        };

        Console.CancelKeyPress += _handler;
    }

    /// <summary>The token a command and its pipelines observe.</summary>
    public CancellationToken Token => _source.Token;

    /// <inheritdoc/>
    public void Dispose()
    {
        Console.CancelKeyPress -= _handler;
        _source.Dispose();
    }
}
