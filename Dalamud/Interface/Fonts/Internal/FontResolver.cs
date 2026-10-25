using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Interface.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Loads and keeps the fonts of font specs.</summary>
[ServiceManager.EarlyLoadedService]
internal sealed class FontResolver : IInternalDisposableService
{
    // Frames a font is kept after its last use, once no ImFontRef holds it. It covers the frame being drawn, and fonts
    // that are pushed every frame without being held.
    private const int GraceFrames = 120;

    // Frames between looks for fonts to release.
    private const int SweepFrames = 60;

    // Frames until a font that failed to load is tried again while it is used. Each failure doubles it, up to
    // 2^MaxRetryShift times.
    private const int RetryFrames = 600;
    private const int MaxRetryShift = 6;

    private static readonly ModuleLog Log = ModuleLog.Create<FontResolver>();

    private readonly FontFactory factory;
    private readonly Func<Action, Task> runBeforeRender;
    private readonly Lock syncRoot = new();
    private readonly Dictionary<FontSpec, Entry> entries = [];
    private readonly Dictionary<nint, Entry> byPointer = [];

    // Specs kept by their users, such as in a field, are found here without hashing them or taking the lock.
    private readonly ConditionalWeakTable<FontSpec, Entry> byInstance = new();
    private int defaultGeneration;

    /// <summary>Initializes a new instance of the <see cref="FontResolver"/> class.</summary>
    /// <param name="factory">Makes the fonts.</param>
    /// <param name="runBeforeRender">Runs an action on the main thread before the next frame is drawn.</param>
    internal FontResolver(FontFactory factory, Func<Action, Task> runBeforeRender)
    {
        this.factory = factory;
        this.runBeforeRender = runBeforeRender;
        factory.DefaultChainChanged += this.OnDefaultChainChanged;
    }

    [ServiceManager.ServiceConstructor]
    private FontResolver(FontFactory factory, InterfaceManager interfaceManager)
        : this(factory, interfaceManager.RunBeforeImGuiRender)
    {
    }

    /// <summary>Raised once the fonts that depend on the default font have been made again.</summary>
    /// <remarks>Raised on the main thread, before a frame. Pointers stay the same, but text sizes and metrics may
    /// change.</remarks>
    public event Action? DefaultFontChanged;

    /// <summary>Gets the font of a spec, loading it in the background if needed, without waiting.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="retry">Whether to try loading again if an earlier attempt failed.</param>
    /// <returns>The font. It pushes as the current font until loaded. <see cref="ImFontRef.LoadTask"/> says when it
    /// is loaded, or why it could not be.</returns>
    public ImFontRef Get(FontSpec spec, bool retry = false) => this.RefOf(this.GetEntry(spec, retry));

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

            // In the lock, so that a sweep does not release it before it is held.
            this.Touch(entry);
        }

        return this.RefOf(entry);
    }

    /// <summary>Pushes the font of a spec into the ImGui font stack, loading it in the background if needed.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
    /// <param name="style">The style to push the font with, or null for its own.</param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <remarks>Does not wait for the load. Until the font is loaded, and if it could not be loaded, pushes the current
    /// font at the size instead.</remarks>
    public ImPopFontDisposable Push(FontSpec spec, float? sizePx = null, ImTextStyle? style = null) =>
        this.Push(this.GetEntry(spec, false), sizePx, style);

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.factory.DefaultChainChanged -= this.OnDefaultChainChanged;
        using (this.syncRoot.EnterScope())
        {
            foreach (var entry in this.entries.Values)
                entry.Released = true;
            this.entries.Clear();
            this.byPointer.Clear();
            this.byInstance.Clear();
        }
    }

    /// <summary>Pushes a font into the ImGui font stack.</summary>
    /// <param name="entry">The font.</param>
    /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
    /// <param name="style">The style to push the font with, or null for its own.</param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <remarks>Pushes the current font if <paramref name="entry"/> has not loaded, at the size of its spec unless
    /// <paramref name="sizePx"/> is given.</remarks>
    internal unsafe ImPopFontDisposable Push(Entry entry, float? sizePx, ImTextStyle? style = null)
    {
        ThreadSafety.AssertMainThread();
        var target = entry.Slot.LoadedTarget;
        var size = target is null
                       ? FontSizes.ToPushSize(null, sizePx ?? entry.Spec.SizePx)
                       : FontSizes.ToPushSize(target, sizePx);
        this.Touch(entry);
        // A null font pushes the current one as it is, style and all.
        ImFontPtr font = target is null ? ImFontPtr.Null : entry.Slot.NativePointer;
        if (style is { } st)
            font = (font.IsNull ? ImGui.Font : font).WithStyleForFrame(st);
        ImGui.PushFont(font, size);
        return new(font: true);
    }

    /// <summary>Gets the ImGui font of a font, loaded or not.</summary>
    /// <remarks>Keeps it alive until the frame being built has been drawn, and for <see cref="GraceFrames"/> frames
    /// after.</remarks>
    /// <param name="entry">The font.</param>
    /// <returns>The ImGui font.</returns>
    internal unsafe ImFontPtr Use(Entry entry)
    {
        this.Touch(entry);
        return entry.Slot.NativePointer;
    }

    /// <summary>Gets the state of every font alive, for debugging.</summary>
    /// <returns>One entry per font.</returns>
    internal unsafe EntryInfo[] Snapshot()
    {
        var now = FontClock.Frame;
        using (this.syncRoot.EnterScope())
        {
            return
            [
                .. this.entries.Values
                       .Select(e => new EntryInfo(
                                   e.Spec,
                                   (nint)e.Slot.NativePointer,
                                   e.Load.Status switch
                                   {
                                       TaskStatus.RanToCompletion => "Loaded",
                                       TaskStatus.Faulted => $"Failed: {e.Exception?.Message}",
                                       _ => "Loading",
                                   },
                                   e.Ref.TryGetTarget(out _),
                                   now - e.LastUsedFrame,
                                   e.DependsOnDefault)),
            ];
        }
    }

    /// <summary>Releases fonts that are neither held nor used lately, and tries again to load fonts that failed and
    /// are still used.</summary>
    /// <remarks>Called by <see cref="Glyphs.GlyphAtlasTextResources.OnFrameRendered"/>, after the clock counted the
    /// frame.</remarks>
    internal unsafe void OnFrameRendered()
    {
        var now = FontClock.Frame;
        if (now % SweepFrames != 0)
            return;

        using (this.syncRoot.EnterScope())
        {
            List<Entry>? released = null;
            foreach (var entry in this.entries.Values)
            {
                if (!entry.Load.IsCompleted)
                    continue;

                var lastUsed = entry.LastUsedFrame;
                if (now - lastUsed > GraceFrames)
                {
                    if (!entry.Ref.TryGetTarget(out _) && entry.TryRelease(lastUsed))
                        (released ??= []).Add(entry);
                }
                else if (entry.Load.IsFaulted && now >= entry.RetryFrame)
                {
                    entry.Load = this.StartLoad(entry);
                }
            }

            if (released is null)
                return;

            foreach (var entry in released)
            {
                entry.Released = true;
                this.entries.Remove(entry.Spec);
                this.byPointer.Remove((nint)entry.Slot.NativePointer);
            }

            Log.Verbose("Released {Count} fonts, {Left} remain", released.Count, this.entries.Count);
        }
    }

    private Entry GetEntry(FontSpec spec, bool retry)
    {
        // A sweep that released the entry fails the mark, so that it is not used after its release.
        if (this.byInstance.TryGetValue(spec, out var entry)
            && !(retry && entry.Load.IsFaulted)
            && entry.TryMarkUsed(FontClock.Frame))
        {
            return entry;
        }

        return this.GetEntrySlow(spec, retry);
    }

    private unsafe Entry GetEntrySlow(FontSpec spec, bool retry)
    {
        using (this.syncRoot.EnterScope())
        {
            if (!this.entries.TryGetValue(spec, out var entry))
            {
                entry = new(spec, FontFactory.FollowsDefaultFont(spec));
                entry.Load = this.StartLoad(entry);
                this.entries.Add(spec, entry);
                this.byPointer.Add((nint)entry.Slot.NativePointer, entry);
            }
            else if (retry && entry.Load.IsFaulted)
            {
                entry.Load = this.StartLoad(entry);
            }

            // Touched before the lock is left, so that a sweep does not release a font that was just made.
            this.Touch(entry);
            this.byInstance.AddOrUpdate(spec, entry);
            return entry;
        }
    }

    // Called in the lock. The slot waits for the task it is given, so the task is made before it starts.
    private Task<CompositeFont> StartLoad(Entry entry)
    {
        var task = new Task<CompositeFont>(() => this.Load(entry));
        entry.Slot.Load = task;
        task.Start(TaskScheduler.Default);
        return task;
    }

    private CompositeFont Load(Entry entry)
    {
        try
        {
            while (true)
            {
                var generation = Volatile.Read(ref this.defaultGeneration);
                var font = this.factory.CreateFont(entry.Spec);
                using (this.syncRoot.EnterScope())
                {
                    // Made again if the default font changed while this one was made from it. A change after this
                    // check finds the font loaded, as OnDefaultChainChanged looks for loaded fonts in the lock.
                    if (entry.DependsOnDefault && generation != Volatile.Read(ref this.defaultGeneration))
                        continue;

                    entry.Slot.Target = font;
                    entry.CountSuccess();
                    return font;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load {Spec}. It is pushed as the current font", entry.Spec);
            entry.CountFailure(FontClock.Frame);

            // Made again, as the default font may have changed since the last attempt.
            entry.Slot.Fallback = this.CreateFallback(entry.Spec) ?? entry.Slot.Fallback;
            throw new FontLoadException(entry.Spec, e);
        }
    }

    // The default font at the size of a spec, for drawing with a font that could not be loaded. The built-in one if
    // the default font is the one that failed.
    private CompositeFont? CreateFallback(FontSpec spec)
    {
        try
        {
            return this.factory.CreateFont(FontSpec.Default with { SizePx = spec.SizePx });
        }
        catch (Exception)
        {
            try
            {
                return this.factory.CreateFont(FontFactory.BuiltInDefaultFontSpec with { SizePx = spec.SizePx });
            }
            catch (Exception e)
            {
                Log.Error(e, "No font could be made to stand for {Spec}", spec);
                return null;
            }
        }
    }

    // The entry was touched in this frame, so a sweep does not release it before it is held.
    private ImFontRef RefOf(Entry entry)
    {
        using (this.syncRoot.EnterScope())
        {
            if (entry.Ref.TryGetTarget(out var existing))
                return existing;
            var fontRef = new ImFontRef(this, entry);
            entry.Ref.SetTarget(fontRef);
            return fontRef;
        }
    }

    private void Touch(Entry entry) => entry.TryMarkUsed(FontClock.Frame);

    private void OnDefaultChainChanged()
    {
        var generation = Interlocked.Increment(ref this.defaultGeneration);
        Entry[] dependents;
        using (this.syncRoot.EnterScope())
        {
            dependents = [.. this.entries.Values.Where(x => x.DependsOnDefault && x.Slot.LoadedTarget is not null)];

            // A font that failed may load now, and its fallback is the default font.
            foreach (var entry in this.entries.Values)
            {
                if (entry.Load.IsFaulted)
                    entry.Load = this.StartLoad(entry);
            }
        }

        // Make the new fonts in the background, and swap them in between frames.
        _ = Task.Run(() =>
        {
            var rebuilt = new List<(Entry Entry, CompositeFont Font)>(dependents.Length);
            foreach (var entry in dependents)
            {
                // A later change makes them all again.
                if (generation != Volatile.Read(ref this.defaultGeneration))
                    return Task.CompletedTask;

                try
                {
                    rebuilt.Add((entry, this.factory.CreateFont(entry.Spec)));
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to make {Spec} again, keeping the old font", entry.Spec);
                }
            }

            return this.runBeforeRender(() =>
            {
                // A later change supersedes this one.
                if (generation != Volatile.Read(ref this.defaultGeneration))
                    return;
                foreach (var (entry, font) in rebuilt)
                    entry.Slot.Target = font;
                this.DefaultFontChanged.InvokeSafely();
            });
        });
    }

    /// <summary>The state of a font, for debugging.</summary>
    /// <param name="Spec">The spec it was first made from.</param>
    /// <param name="Pointer">The ImGui font.</param>
    /// <param name="State">Whether it is loading, loaded, or failed (and why).</param>
    /// <param name="Held">Whether an <see cref="ImFontRef"/> holds it.</param>
    /// <param name="FramesSinceUse">Frames since it was last used.</param>
    /// <param name="FollowsDefault">Whether it is made again when the default font changes.</param>
    internal readonly record struct EntryInfo(
        FontSpec Spec,
        nint Pointer,
        string State,
        bool Held,
        long FramesSinceUse,
        bool FollowsDefault);

    /// <summary>A font made from a spec.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="dependsOnDefault">Whether the font depends on the default font.</param>
    internal sealed class Entry(FontSpec spec, bool dependsOnDefault)
    {
        // The last used frame of a font a sweep has released.
        private const long ReleasedMark = long.MinValue;

        private volatile bool released;
        private Task<CompositeFont> load = Task.FromResult<CompositeFont>(null!);
        private FontLoadException? exception;
        private long lastUsedFrame;
        private long retryFrame;
        private int failures;

        /// <summary>Gets the spec.</summary>
        public FontSpec Spec { get; } = spec;

        /// <summary>Gets the frame the font was last used in.</summary>
        /// <seealso cref="FontClock.Frame"/>
        public long LastUsedFrame => Interlocked.Read(ref this.lastUsedFrame);

        /// <summary>Gets a value indicating whether the font depends on the default font.</summary>
        public bool DependsOnDefault { get; } = dependsOnDefault;

        /// <summary>Gets or sets the load, which faults with <see cref="FontLoadException"/>.</summary>
        public Task<CompositeFont> Load
        {
            get => this.load;
            set
            {
                this.exception = null;
                this.load = value;
            }
        }

        /// <summary>Gets why the last load failed, or null if it has not failed (yet).</summary>
        /// <remarks>Kept once read, as <see cref="Task.Exception"/> makes a new object on each read.</remarks>
        public FontLoadException? Exception =>
            this.load.IsFaulted
                ? this.exception ??= this.load.Exception?.InnerException as FontLoadException
                : null;

        /// <summary>Gets or sets a value indicating whether the resolver has let go of the font.</summary>
        /// <remarks>A spec that still maps to it then looks for a font again.</remarks>
        public bool Released
        {
            get => this.released;
            set => this.released = value;
        }

        /// <summary>Gets the font ImGui knows. It is filled when the load completes.</summary>
        public SlotFont Slot { get; } = new(NameOf(spec));

        /// <summary>Gets the <see cref="ImFontRef"/> of the font, while someone holds it.</summary>
        public WeakReference<ImFontRef> Ref { get; } = new(null!);

        /// <summary>Gets the frame from which a failed load is tried again, if the font is still used.</summary>
        public long RetryFrame => Interlocked.Read(ref this.retryFrame);

        /// <summary>Counts the font as used in a frame, unless the resolver has let go of it.</summary>
        /// <param name="frame">The frame.</param>
        /// <returns>Whether the resolver still has it.</returns>
        public bool TryMarkUsed(long frame)
        {
            var last = Interlocked.Read(ref this.lastUsedFrame);
            while (last != frame)
            {
                if (last == ReleasedMark)
                    return false;
                var seen = Interlocked.CompareExchange(ref this.lastUsedFrame, frame, last);
                if (seen == last)
                    break;
                last = seen;
            }

            return !this.released;
        }

        /// <summary>Marks the font for release, unless it was used after a frame.</summary>
        /// <param name="lastUsed">The last used frame the caller saw.</param>
        /// <returns>Whether it was marked. <see cref="TryMarkUsed"/> fails from then on.</returns>
        public bool TryRelease(long lastUsed) =>
            Interlocked.CompareExchange(ref this.lastUsedFrame, ReleasedMark, lastUsed) == lastUsed;

        /// <summary>Counts a failed load, and puts the next attempt off longer each time.</summary>
        /// <param name="frame">The frame it failed in.</param>
        public void CountFailure(long frame)
        {
            var shift = Math.Min(Interlocked.Increment(ref this.failures) - 1, MaxRetryShift);
            Interlocked.Exchange(ref this.retryFrame, frame + ((long)RetryFrames << shift));
        }

        /// <summary>Counts a load that succeeded.</summary>
        public void CountSuccess() => Volatile.Write(ref this.failures, 0);

        // The name of the slot. A spec that cannot be named fails its load, not the call that asked for it.
        private static string NameOf(FontSpec spec)
        {
            try
            {
                return spec.ToString();
            }
            catch (Exception)
            {
                return nameof(FontSpec);
            }
        }
    }
}
