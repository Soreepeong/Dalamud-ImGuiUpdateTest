using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Utility;

namespace Dalamud.Interface.ManagedFontAtlas.Internals;

/// <summary>
/// Default implementation for <see cref="FontHandle"/>.
/// </summary>
internal abstract class FontHandle : IFontHandle
{
    private IFontHandleManager? manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FontHandle"/> class.
    /// </summary>
    /// <param name="manager">An instance of <see cref="IFontHandleManager"/>.</param>
    protected FontHandle(IFontHandleManager manager)
    {
        this.manager = manager;
    }

    /// <inheritdoc/>
    public event IFontHandle.ImFontChangedDelegate? ImFontChanged;

    /// <summary>
    /// Event to be called on the first <see cref="IDisposable.Dispose"/> call.
    /// </summary>
    protected event Action? Disposed;

    /// <inheritdoc/>
    public Exception? LoadException => null;

    /// <inheritdoc/>
    public bool Available => false;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.manager is null)
            return;

        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Obtains an instance of <see cref="ImFontPtr"/> corresponding to this font handle,
    /// to be released after rendering the current frame.
    /// </summary>
    /// <returns>The font pointer, or default if unavailble.</returns>
    /// <remarks>
    /// Behavior is undefined on access outside the main thread.
    /// </remarks>
    public ImFontPtr LockUntilPostFrame() => default;

    /// <summary>
    /// Attempts to lock the fully constructed instance of <see cref="ImFontPtr"/> corresponding to the this
    /// <see cref="IFontHandle"/>, for use in any thread.<br />
    /// Modification of the font will exhibit undefined behavior if some other thread also uses the font.
    /// </summary>
    /// <param name="errorMessage">The error message, if any.</param>
    /// <returns>
    /// An instance of <see cref="ILockedImFont"/> that <b>must</b> be disposed after use on success;
    /// <c>null</c> with <paramref name="errorMessage"/> populated on failure.
    /// </returns>
    public ILockedImFont? TryLock(out string? errorMessage)
    {
        errorMessage = "The font atlas has not been built yet.";
        return null;
    }

    /// <inheritdoc/>
    public ILockedImFont Lock() =>
        this.TryLock(out var errorMessage) ?? throw new InvalidOperationException(errorMessage);

    /// <inheritdoc/>
    public IDisposable Push() => Disposable.Empty;

    /// <inheritdoc/>
    public void Pop()
    {
    }

    /// <inheritdoc/>
    public Task<IFontHandle> WaitAsync() => this.WaitAsync(CancellationToken.None);

    /// <inheritdoc/>
    public Task<IFontHandle> WaitAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IFontHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled());

        this.Disposed += OnDisposed;
        return tcs.Task;

        void OnDisposed()
        {
            if (tcs.Task.IsCompletedSuccessfully)
                return;

            this.Disposed -= OnDisposed;
            try
            {
                tcs.SetException(new ObjectDisposedException(nameof(GamePrebakedFontHandle)));
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Implementation for <see cref="IDisposable.Dispose"/>.
    /// </summary>
    /// <param name="disposing">If <c>true</c>, then the function is being called from <see cref="IDisposable.Dispose"/>.</param>
    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (Interlocked.Exchange(ref this.manager, null) is not { } managerToDisassociate)
                return;

            managerToDisassociate.FreeFontHandle(this);
            this.Disposed?.InvokeSafely();
            this.Disposed = null;
            this.ImFontChanged = null;
        }
    }
}
