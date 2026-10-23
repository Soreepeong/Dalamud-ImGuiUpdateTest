using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using Dalamud.Utility;

using Serilog;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace Dalamud.Interface.ImGuiBackend.Helpers.D3D11;

/// <summary>Finds out what the GPU was doing when the device was removed, and forwards debug layer messages to the log.
/// </summary>
/// <remarks>
/// <para>Breadcrumbs are event queries issued after each stage of Dalamud's GPU work. The GPU finishes them in order, so
/// when the device is removed, the stage after the last finished one is where it was. If all of Dalamud's stages had
/// finished, it was in other work, such as the game's.</para>
/// <para>Debug layer messages exist only if the device was made with the debug layer, such as with the injector's
/// <c>--debug-directx</c>.</para>
/// <para>All calls but <see cref="Dispose"/> come from the thread that renders, except for the removal report, which
/// comes from the thread pool.</para>
/// </remarks>
internal sealed unsafe class D3D11Diagnostics : IDisposable
{
    // Breadcrumbs beyond this many unfinished ones are dropped, as the GPU is far behind or gone.
    private const int MaxPending = 1024;

    // Messages of an ID logged in full before only their counts are, at each power of two.
    private const int MessagesLoggedInFull = 5;

    private static readonly ILogger Logger = Log.ForContext<D3D11Diagnostics>();

    private readonly Lock syncRoot = new();
    private readonly Queue<Crumb> pending = new();
    private readonly Stack<nint> freeQueries = new();
    private readonly Dictionary<nint, (byte[] Bytes, string Name)> owners = [];
    private readonly Dictionary<(bool Dxgi, int Id), int> messageCounts = [];
    private readonly EventWaitHandle removedEvent = new(false, EventResetMode.ManualReset);

    private ComPtr<ID3D11Device> device;
    private ComPtr<ID3D11DeviceContext> context;
    private ComPtr<ID3D11InfoQueue> d3dInfoQueue;
    private ComPtr<IDXGIInfoQueue> dxgiInfoQueue;
    private ComPtr<ID3D11Device4> device4;
    private RegisteredWaitHandle? removedWait;
    private uint removedCookie;
    private Crumb? lastFinished;
    private long frame;
    private bool dropped;
    private int reported;

    /// <summary>Initializes a new instance of the <see cref="D3D11Diagnostics"/> class.</summary>
    /// <param name="device">The device.</param>
    /// <param name="context">The immediate context.</param>
    public D3D11Diagnostics(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        this.device = new(device);
        this.context = new(context);

        fixed (ID3D11InfoQueue** pp = &this.d3dInfoQueue.GetPinnableReference())
            _ = device->QueryInterface(__uuidof<ID3D11InfoQueue>(), (void**)pp);

        // The DXGI queue also has the D3D11 messages, which are taken from the D3D11 queue instead.
        if (!this.d3dInfoQueue.IsEmpty())
        {
            fixed (IDXGIInfoQueue** pp = &this.dxgiInfoQueue.GetPinnableReference())
                _ = DirectX.DXGIGetDebugInterface1(0, __uuidof<IDXGIInfoQueue>(), (void**)pp);
            Logger.Information("Forwarding the messages of the D3D11 debug layer");
        }

        // Windows 10 and later.
        fixed (ID3D11Device4** pp = &this.device4.GetPinnableReference())
        {
            if (device->QueryInterface(__uuidof<ID3D11Device4>(), (void**)pp).SUCCEEDED)
            {
                uint cookie;
                if (this.device4.Get()->RegisterDeviceRemovedEvent(
                        (HANDLE)this.removedEvent.SafeWaitHandle.DangerousGetHandle(),
                        &cookie).SUCCEEDED)
                {
                    this.removedCookie = cookie;
                    this.removedWait = ThreadPool.RegisterWaitForSingleObject(
                        this.removedEvent,
                        static (state, _) => ((D3D11Diagnostics)state!).ReportRemoval("The device removal event"),
                        this,
                        Timeout.Infinite,
                        true);
                }
            }
        }
    }

    /// <summary>Finalizes an instance of the <see cref="D3D11Diagnostics"/> class.</summary>
    ~D3D11Diagnostics() => this.ReleaseUnmanagedResources();

    /// <summary>Starts a frame of Dalamud's GPU work. Collects the breadcrumbs the GPU has finished, and logs the debug
    /// layer messages so far.</summary>
    public void BeginFrame()
    {
        lock (this.syncRoot)
        {
            this.frame++;
            this.CollectFinished();
            this.DrainMessages();
        }

        this.Mark("frame begins");
    }

    /// <summary>Marks the end of a stage of Dalamud's GPU work.</summary>
    /// <param name="stage">What was done.</param>
    /// <param name="viewport">The viewport it was for, or 0.</param>
    /// <param name="index">The index of the draw list it was for, or -1.</param>
    /// <param name="owner">The name of the owner of the draw list, or null.</param>
    public void Mark(string stage, uint viewport = 0, int index = -1, byte* owner = null)
    {
        lock (this.syncRoot)
            this.Add(stage, viewport, index, this.OwnerOf(owner));
    }

    /// <summary>Marks the end of a stage of Dalamud's GPU work.</summary>
    /// <param name="stage">What was done.</param>
    /// <param name="owner">What it was done for.</param>
    public void Mark(string stage, string owner)
    {
        lock (this.syncRoot)
            this.Add(stage, 0, -1, owner);
    }

    /// <summary>Logs why the device was removed and what the GPU was doing then, once.</summary>
    /// <param name="trigger">What noticed the removal.</param>
    public void ReportRemoval(string trigger)
    {
        if (Interlocked.Exchange(ref this.reported, 1) != 0)
            return;

        lock (this.syncRoot)
        {
            if (this.device.IsEmpty())
                return;

            var reason = (int)this.device.Get()->GetDeviceRemovedReason();
            var sb = new StringBuilder();
            sb.Append($"{trigger}: the device was removed with reason 0x{reason:X08} ({NameOf(reason)}).");
            sb.Append($" The GPU had last finished: {this.lastFinished?.ToString() ?? "nothing Dalamud marked"}.");
            if (this.pending.Count == 0)
            {
                sb.Append(" All of Dalamud's work had finished, so the GPU was in other work, such as the game's.");
            }
            else
            {
                var first = this.pending.Peek();
                sb.Append(
                    first.Stage == "frame begins"
                        ? " It had not reached Dalamud's next frame, so it was in other work, such as the game's."
                        : $" It was in Dalamud's work before: {first}.");
                sb.Append($" {this.pending.Count} breadcrumbs had not finished");
                sb.Append(this.dropped ? ", and more were dropped." : ".");
            }

            Logger.Error(sb.ToString());
            this.DrainMessages();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    private static string NameOf(int reason) => reason switch
    {
        0 => "S_OK",
        DXGI.DXGI_ERROR_DEVICE_HUNG => "DXGI_ERROR_DEVICE_HUNG",
        DXGI.DXGI_ERROR_DEVICE_REMOVED => "DXGI_ERROR_DEVICE_REMOVED",
        DXGI.DXGI_ERROR_DEVICE_RESET => "DXGI_ERROR_DEVICE_RESET",
        DXGI.DXGI_ERROR_DRIVER_INTERNAL_ERROR => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
        DXGI.DXGI_ERROR_INVALID_CALL => "DXGI_ERROR_INVALID_CALL",
        _ => "unknown",
    };

    // Issues an event query for a breadcrumb.
    private void Add(string stage, uint viewport, int index, string? owner)
    {
        if (this.context.IsEmpty())
            return;
        if (this.pending.Count >= MaxPending)
        {
            this.dropped = true;
            return;
        }

        ID3D11Query* query;
        if (this.freeQueries.TryPop(out var q))
        {
            query = (ID3D11Query*)q;
        }
        else
        {
            var desc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
            if (this.device.Get()->CreateQuery(&desc, &query).FAILED)
                return;
        }

        this.context.Get()->End((ID3D11Asynchronous*)query);
        this.pending.Enqueue(new(this.frame, stage, viewport, index, owner, (nint)query));
    }

    // Takes the breadcrumbs the GPU has finished, which it does in order.
    private void CollectFinished()
    {
        while (this.pending.TryPeek(out var crumb))
        {
            var hr = this.context.Get()->GetData(
                (ID3D11Asynchronous*)crumb.Query,
                null,
                0,
                (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if (hr != S.S_OK)
                return;

            this.pending.Dequeue();
            this.freeQueries.Push(crumb.Query);
            this.lastFinished = crumb;
        }

        this.dropped = false;
    }

    private void DrainMessages()
    {
        if (!this.d3dInfoQueue.IsEmpty())
        {
            var queue = this.d3dInfoQueue.Get();
            for (ulong i = 0, n = queue->GetNumStoredMessages(); i < n; i++)
            {
                nuint length = 0;
                if (queue->GetMessage(i, null, &length).FAILED || length == 0)
                    continue;
                var message = (D3D11_MESSAGE*)NativeMemory.Alloc(length);
                try
                {
                    if (queue->GetMessage(i, message, &length).SUCCEEDED)
                    {
                        this.LogMessage(
                            false,
                            (int)message->ID,
                            message->Severity switch
                            {
                                D3D11_MESSAGE_SEVERITY.D3D11_MESSAGE_SEVERITY_CORRUPTION => Serilog.Events.LogEventLevel.Fatal,
                                D3D11_MESSAGE_SEVERITY.D3D11_MESSAGE_SEVERITY_ERROR => Serilog.Events.LogEventLevel.Error,
                                D3D11_MESSAGE_SEVERITY.D3D11_MESSAGE_SEVERITY_WARNING => Serilog.Events.LogEventLevel.Warning,
                                _ => Serilog.Events.LogEventLevel.Debug,
                            },
                            message->Category.ToString(),
                            message->pDescription,
                            message->DescriptionByteLength);
                    }
                }
                finally
                {
                    NativeMemory.Free(message);
                }
            }

            queue->ClearStoredMessages();
        }

        if (!this.dxgiInfoQueue.IsEmpty())
        {
            var queue = this.dxgiInfoQueue.Get();
            var producer = DXGI.DXGI_DEBUG_DXGI;
            for (ulong i = 0, n = queue->GetNumStoredMessages(producer); i < n; i++)
            {
                nuint length = 0;
                if (queue->GetMessage(producer, i, null, &length).FAILED || length == 0)
                    continue;
                var message = (DXGI_INFO_QUEUE_MESSAGE*)NativeMemory.Alloc(length);
                try
                {
                    if (queue->GetMessage(producer, i, message, &length).SUCCEEDED)
                    {
                        this.LogMessage(
                            true,
                            message->ID,
                            message->Severity switch
                            {
                                DXGI_INFO_QUEUE_MESSAGE_SEVERITY.DXGI_INFO_QUEUE_MESSAGE_SEVERITY_CORRUPTION => Serilog.Events.LogEventLevel.Fatal,
                                DXGI_INFO_QUEUE_MESSAGE_SEVERITY.DXGI_INFO_QUEUE_MESSAGE_SEVERITY_ERROR => Serilog.Events.LogEventLevel.Error,
                                DXGI_INFO_QUEUE_MESSAGE_SEVERITY.DXGI_INFO_QUEUE_MESSAGE_SEVERITY_WARNING => Serilog.Events.LogEventLevel.Warning,
                                _ => Serilog.Events.LogEventLevel.Debug,
                            },
                            message->Category.ToString(),
                            message->pDescription,
                            message->DescriptionByteLength);
                    }
                }
                finally
                {
                    NativeMemory.Free(message);
                }
            }

            queue->ClearStoredMessages(producer);
        }
    }

    // Logs a message in full the first few times, then only the count at each power of two, as some repeat every frame.
    private void LogMessage(
        bool dxgi, int id, Serilog.Events.LogEventLevel level, string category, sbyte* description, nuint length)
    {
        var key = (dxgi, id);
        this.messageCounts.TryGetValue(key, out var count);
        this.messageCounts[key] = ++count;
        if (count > MessagesLoggedInFull && (count & (count - 1)) != 0)
            return;

        // The length counts the terminating null.
        var text = Encoding.UTF8.GetString((byte*)description, (int)Math.Max(length, 1) - 1);
        var source = dxgi ? "DXGI" : "D3D11";
        if (count <= MessagesLoggedInFull)
            Logger.Write(level, "[{Source}] {Category} #{Id} in frame {Frame}: {Text}", source, category, id, this.frame, text);
        else
            Logger.Write(level, "[{Source}] {Category} #{Id} has been seen {Count} times: {Text}", source, category, id, count, text);
    }

    // The name of a draw list owner, cached by pointer, as window names stay where they are.
    private string? OwnerOf(byte* name)
    {
        if (name is null)
            return null;
        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
        if (this.owners.TryGetValue((nint)name, out var e) && bytes.SequenceEqual(e.Bytes))
            return e.Name;
        if (this.owners.Count >= 1024)
            this.owners.Clear();
        e = (bytes.ToArray(), Encoding.UTF8.GetString(bytes));
        this.owners[(nint)name] = e;
        return e.Name;
    }

    private void ReleaseUnmanagedResources()
    {
        this.removedWait?.Unregister(null);
        this.removedWait = null;
        lock (this.syncRoot)
        {
            if (!this.device4.IsEmpty())
            {
                this.device4.Get()->UnregisterDeviceRemoved(this.removedCookie);
                this.device4.Reset();
            }

            foreach (var crumb in this.pending)
                ((ID3D11Query*)crumb.Query)->Release();
            this.pending.Clear();
            while (this.freeQueries.TryPop(out var q))
                ((ID3D11Query*)q)->Release();

            this.d3dInfoQueue.Reset();
            this.dxgiInfoQueue.Reset();
            this.context.Reset();
            this.device.Reset();
        }

        this.removedEvent.Dispose();
    }

    private readonly record struct Crumb(
        long Frame, string Stage, uint Viewport, int Index, string? Owner, nint Query)
    {
        public override string ToString()
        {
            var sb = new StringBuilder($"frame {this.Frame}, {this.Stage}");
            if (this.Viewport != 0)
                sb.Append($", viewport 0x{this.Viewport:X}");
            if (this.Index >= 0)
                sb.Append($", draw list {this.Index}");
            if (this.Owner is not null)
                sb.Append($" ({this.Owner})");
            return sb.ToString();
        }
    }
}
