using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Interface.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Makes fonts from font specs, without font atlases.</summary>
[ServiceManager.EarlyLoadedService]
internal sealed class FontResolver : IInternalDisposableService
{
    // Frames a font is kept after its last use, once no ImFontRef holds it. It covers the frame being drawn, and fonts
    // that are pushed every frame without being held.
    private const int GraceFrames = 120;

    // Frames between looks for fonts to release.
    private const int SweepFrames = 60;

    private static readonly ModuleLog Log = ModuleLog.Create<FontResolver>();

    private readonly FontFactory factory;
    private readonly InterfaceManager interfaceManager;
    private readonly Lock syncRoot = new();
    private readonly Dictionary<IFontSpec, Entry> entries = [];
    private readonly Dictionary<nint, Entry> byPointer = [];
    private long frame;
    private int defaultGeneration;

    [ServiceManager.ServiceConstructor]
    private FontResolver(FontFactory factory, InterfaceManager interfaceManager)
    {
        this.factory = factory;
        this.interfaceManager = interfaceManager;
        factory.DefaultChainChanged += this.OnDefaultChainChanged;
    }

    /// <summary>Raised once the fonts that depend on the default font have been made again.</summary>
    /// <remarks>Raised on the main thread, before a frame. Pointers stay the same, but text sizes and metrics may
    /// change.</remarks>
    public event Action? DefaultFontChanged;

    /// <summary>Gets the number of fonts alive.</summary>
    public int Count
    {
        get
        {
            using (this.syncRoot.EnterScope())
                return this.entries.Count;
        }
    }

    /// <summary>Gets the number of frames drawn so far.</summary>
    public long Frame => Interlocked.Read(ref this.frame);

    /// <summary>Gets the font of a spec, loading it if needed.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>The font. If it could not be loaded, <see cref="ImFontRef.Exception"/> says why.</returns>
    /// <remarks>Blocks until loaded.</remarks>
    public ImFontRef Get(IFontSpec spec)
    {
        var entry = this.GetEntry(spec, false);
        WaitForLoad(entry);
        return this.RefOf(entry);
    }

    /// <summary>Gets the font of a spec, loading it in the background if needed.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="retry">Whether to try loading again if an earlier attempt failed.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The font. If it could not be loaded, <see cref="ImFontRef.Exception"/> says why.</returns>
    /// <remarks>Cancelling does not stop the load itself, as others may wait for it too.</remarks>
    public async Task<ImFontRef> GetAsync(
        IFontSpec spec, bool retry = false, CancellationToken cancellationToken = default)
    {
        var entry = this.GetEntry(spec, retry);
        await ((Task)entry.Load.WaitAsync(cancellationToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();
        return this.RefOf(entry);
    }

    /// <summary>Gets the font behind an ImGui font made by this resolver.</summary>
    /// <param name="font">The ImGui font.</param>
    /// <returns>The font, or null if <paramref name="font"/> was not made here, or has been released.</returns>
    public unsafe ImFontRef? Find(ImFontPtr font)
    {
        Entry? entry;
        using (this.syncRoot.EnterScope())
        {
            if (font.IsNull || !this.byPointer.TryGetValue((nint)font.Handle->Impl, out entry))
                return null;
        }

        return this.RefOf(entry);
    }

    /// <summary>Pushes the font of a spec into the ImGui font stack, loading it if needed.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="sizePx">The size of the glyphs, or null for the spec's size.</param>
    /// <param name="style">The style to push the font with, or null for its own.</param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <remarks>A positive size is in pixels before global scaling, else a ratio of the current font size. Blocks until
    /// loaded. If the font could not be loaded, logs that once and pushes the current font at the size instead.
    /// </remarks>
    public ImPopFontDisposable Push(IFontSpec spec, float? sizePx = null, ImTextStyle? style = null)
    {
        var entry = this.GetEntry(spec, false);
        WaitForLoad(entry);
        if (entry.Exception is { } e && !entry.FailureLogged)
        {
            entry.FailureLogged = true;
            Log.Error(e, "Using the current font instead of {Spec}", entry.Spec);
        }

        return this.Push(entry, sizePx, style);
    }

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.factory.DefaultChainChanged -= this.OnDefaultChainChanged;
        using (this.syncRoot.EnterScope())
        {
            this.entries.Clear();
            this.byPointer.Clear();
        }
    }

    /// <summary>Tests whether a font is made again when the default font changes.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>Whether it has a relative size or the default font in it.</returns>
    internal static bool FollowsDefaultFont(IFontSpec spec) => spec.SizePx <= 0 || FontFactory.UsesDefaultFont(spec);

    /// <summary>Pushes a font into the ImGui font stack.</summary>
    /// <param name="entry">The font.</param>
    /// <param name="sizePx">The size of the glyphs, or null for the spec's.</param>
    /// <param name="style">The style to push the font with, or null for its own.</param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <remarks>Pushes the current font if <paramref name="entry"/> has not loaded.</remarks>
    internal unsafe ImPopFontDisposable Push(Entry entry, float? sizePx, ImTextStyle? style = null)
    {
        ThreadSafety.AssertMainThread();
        var slot = entry.Slot;
        var target = slot?.Target;
        var push = target is null ? sizePx ?? 0f : sizePx is { } s ? target.ToPushSize(s) : target.PushSize;

        // ImGui applies the global scale to pixel sizes.
        var size = push > 0 ? push : ImGui.GetCurrentContext().Handle->FontSizeBase * (push == 0 ? 1f : -push);

        this.Touch(entry);
        // A null font pushes the current one as it is, style and all.
        ImFontPtr font = slot is null ? ImFontPtr.Null : slot.NativePointer;
        if (style is { } st)
            font = (font.IsNull ? ImGui.Font : font).WithStyleForFrame(st);
        ImGui.PushFont(font, size);
        return new(font: true);
    }

    /// <summary>Gets the ImGui font of a loaded font.</summary>
    /// <remarks>Keeps it alive until the frame being built has been drawn.</remarks>
    /// <param name="entry">The font.</param>
    /// <returns>The ImGui font.</returns>
    internal unsafe ImFontPtr Use(Entry entry)
    {
        this.Touch(entry);
        return entry.Slot is { } slot ? slot.NativePointer : ImFontPtr.Null;
    }

    /// <summary>Gets the state of every font alive, for debugging.</summary>
    /// <returns>One entry per font.</returns>
    internal unsafe EntryInfo[] Snapshot()
    {
        var now = this.Frame;
        using (this.syncRoot.EnterScope())
        {
            return this.entries.Values
                       .Select(e => new EntryInfo(
                                   e.Spec,
                                   e.Slot is { } slot ? (nint)slot.NativePointer : 0,
                                   e.Load.Status switch
                                   {
                                       TaskStatus.RanToCompletion => "Loaded",
                                       TaskStatus.Faulted => $"Failed: {e.Exception?.Message}",
                                       _ => "Loading",
                                   },
                                   e.Ref.TryGetTarget(out _),
                                   now - e.LastUsedFrame,
                                   e.DependsOnDefault))
                       .ToArray();
        }
    }

    /// <summary>Counts a frame as drawn, and releases fonts that are neither held nor used lately.</summary>
    /// <remarks>Called after each frame has been drawn.</remarks>
    internal unsafe void OnFrameRendered()
    {
        var now = Interlocked.Increment(ref this.frame);
        if (now % SweepFrames != 0)
            return;

        using (this.syncRoot.EnterScope())
        {
            List<Entry>? released = null;
            foreach (var entry in this.entries.Values)
            {
                if (entry.Load.IsCompleted
                    && now - entry.LastUsedFrame > GraceFrames
                    && !entry.Ref.TryGetTarget(out _))
                {
                    (released ??= []).Add(entry);
                }
            }

            if (released is null)
                return;

            foreach (var entry in released)
            {
                this.entries.Remove(entry.Spec);
                if (entry.Slot is { } slot)
                    this.byPointer.Remove((nint)slot.NativePointer);
            }

            Log.Verbose("Released {Count} fonts, {Left} remain", released.Count, this.entries.Count);
        }
    }

    private static void WaitForLoad(Entry entry) =>
        ((Task)entry.Load).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();

    private Entry GetEntry(IFontSpec spec, bool retry)
    {
        Entry? entry;
        using (this.syncRoot.EnterScope())
        {
            if (!this.entries.TryGetValue(spec, out entry))
            {
                entry = new(spec, FollowsDefaultFont(spec));
                entry.Load = this.StartLoad(entry);
                this.entries.Add(spec, entry);
            }
            else if (retry && entry.Load.IsFaulted)
            {
                entry.FailureLogged = false;
                entry.Load = this.StartLoad(entry);
            }
        }

        this.Touch(entry);
        return entry;
    }

    private unsafe Task<CompositeFont> StartLoad(Entry entry) =>
        Task.Run(() =>
        {
            try
            {
                // Make it again if the default font changed while this one was made from it.
                CompositeFont font;
                int generation;
                do
                {
                    generation = Volatile.Read(ref this.defaultGeneration);
                    font = this.factory.CreateFont(entry.Spec);
                }
                while (entry.DependsOnDefault && generation != Volatile.Read(ref this.defaultGeneration));

                using (this.syncRoot.EnterScope())
                {
                    if (entry.Slot is { } existing)
                    {
                        existing.Target = font;
                    }
                    else
                    {
                        var slot = new FontSlot(entry.Spec.ToString(), font);
                        entry.Slot = slot;
                        this.byPointer[(nint)slot.NativePointer] = entry;
                    }
                }

                return font;
            }
            catch (Exception e)
            {
                throw new FontLoadException(entry.Spec, e);
            }
        });

    private ImFontRef RefOf(Entry entry)
    {
        this.Touch(entry);
        using (this.syncRoot.EnterScope())
        {
            if (entry.Ref.TryGetTarget(out var existing))
                return existing;
            var fontRef = new ImFontRef(this, entry);
            entry.Ref.SetTarget(fontRef);
            return fontRef;
        }
    }

    private void Touch(Entry entry) => entry.MarkUsed(Interlocked.Read(ref this.frame));

    private void OnDefaultChainChanged()
    {
        var generation = Interlocked.Increment(ref this.defaultGeneration);
        Entry[] dependents;
        using (this.syncRoot.EnterScope())
            dependents = this.entries.Values.Where(x => x.DependsOnDefault && x.Slot is not null).ToArray();

        // Make the new fonts in the background, and swap them in between frames.
        _ = Task.Run(() =>
        {
            var rebuilt = new List<(Entry Entry, CompositeFont Font)>(dependents.Length);
            foreach (var entry in dependents)
            {
                try
                {
                    rebuilt.Add((entry, this.factory.CreateFont(entry.Spec)));
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to make {Spec} again, keeping the old font", entry.Spec);
                }
            }

            return this.interfaceManager.RunBeforeImGuiRender(() =>
            {
                // A later change supersedes this one.
                if (generation != Volatile.Read(ref this.defaultGeneration))
                    return;
                foreach (var (entry, font) in rebuilt)
                    entry.Slot!.Target = font;
                this.DefaultFontChanged.InvokeSafely();
            });
        });
    }

    /// <summary>The state of a font, for debugging.</summary>
    /// <param name="Spec">The spec it was first made from.</param>
    /// <param name="Pointer">The ImGui font, or 0 if not loaded.</param>
    /// <param name="State">Whether it is loading, loaded, or failed (and why).</param>
    /// <param name="Held">Whether an <see cref="ImFontRef"/> holds it.</param>
    /// <param name="FramesSinceUse">Frames since it was last used.</param>
    /// <param name="FollowsDefault">Whether it is made again when the default font changes.</param>
    internal readonly record struct EntryInfo(
        IFontSpec Spec,
        nint Pointer,
        string State,
        bool Held,
        long FramesSinceUse,
        bool FollowsDefault);

    /// <summary>A font made from a spec.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="dependsOnDefault">Whether the font depends on the default font.</param>
    internal sealed class Entry(IFontSpec spec, bool dependsOnDefault)
    {
        private volatile FontSlot? slot;
        private long lastUsedFrame;

        /// <summary>Gets the spec.</summary>
        public IFontSpec Spec { get; } = spec;

        /// <summary>Gets the frame the font was last used in.</summary>
        /// <seealso cref="FontResolver.frame"/>
        public long LastUsedFrame => Interlocked.Read(ref this.lastUsedFrame);

        /// <summary>Gets a value indicating whether the font depends on the default font.</summary>
        public bool DependsOnDefault { get; } = dependsOnDefault;

        /// <summary>Gets or sets the load, which faults with <see cref="FontLoadException"/>.</summary>
        public Task<CompositeFont> Load { get; set; } = Task.FromResult<CompositeFont>(null!);

        /// <summary>Gets why the last load failed, or null if it has not failed (yet).</summary>
        public FontLoadException? Exception =>
            this.Load.IsFaulted ? (FontLoadException?)this.Load.Exception?.InnerException : null;

        /// <summary>Gets or sets the font ImGui knows, once loaded.</summary>
        public FontSlot? Slot
        {
            get => this.slot;
            set => this.slot = value;
        }

        /// <summary>Gets the <see cref="ImFontRef"/> of the font, while someone holds it.</summary>
        public WeakReference<ImFontRef> Ref { get; } = new(null!);

        /// <summary>Gets or sets a value indicating whether a failure to load has been logged.</summary>
        public bool FailureLogged { get; set; }

        /// <summary>Counts the font as used in a frame.</summary>
        /// <param name="frame">The frame.</param>
        public void MarkUsed(long frame) => Interlocked.Exchange(ref this.lastUsedFrame, frame);
    }
}
