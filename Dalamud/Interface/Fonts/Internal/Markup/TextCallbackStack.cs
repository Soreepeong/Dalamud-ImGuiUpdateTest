using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Markup;

/// <summary>The text markup callbacks pushed in each ImGui context.</summary>
/// <remarks>Main thread only, except <see cref="Invalidate"/>.</remarks>
internal static class TextCallbackStack
{
    private static readonly Dictionary<nint, List<ITextMarkupCallback>> Stacks = [];
    private static readonly ConditionalWeakTable<ITextMarkupCallback, Info> Infos = [];
    // The last snapshot of each depth. Weak, so that they do not keep the callbacks of unloaded plugins. Layouts made
    // with one keep it alive.
    private static readonly List<WeakReference<ITextMarkupCallback[]>> LastSnapshots = [];
    private static long nextId;

    /// <summary>Gets the number of callbacks pushed in the current context.</summary>
    public static int Count => Current(false)?.Count ?? 0;

    /// <summary>Pushes a callback.</summary>
    /// <param name="callback">The callback.</param>
    /// <seealso cref="Pop"/>
    public static void Push(ITextMarkupCallback callback) =>
        Current(true)!.Add(callback ?? throw new ArgumentNullException(nameof(callback)));

    /// <summary>Pops the last callback.</summary>
    public static void Pop()
    {
        if (Current(false) is not { Count: > 0 } stack)
            throw new InvalidOperationException("Popping a text callback too many times.");
        stack.RemoveAt(stack.Count - 1);
    }

    /// <summary>Pops callbacks until <paramref name="count"/> are left (error recovery).</summary>
    /// <param name="count">The number to keep.</param>
    public static void Shrink(int count)
    {
        if (Current(false) is { } stack && stack.Count > count)
            stack.RemoveRange(count, stack.Count - count);
    }

    /// <summary>Makes layouts made with a callback stale.</summary>
    /// <param name="callback">The callback.</param>
    /// <remarks>Any thread.</remarks>
    public static void Invalidate(ITextMarkupCallback callback) => Infos.GetValue(callback, NewInfo).Invalidate();

    /// <summary>Gets the callbacks to ask, in order.</summary>
    /// <returns>The callbacks. Do not modify.</returns>
    /// <remarks>The array of the last call with as many callbacks is given again while the same callbacks are pushed,
    /// as text is usually drawn with the same ones every frame.</remarks>
    public static ITextMarkupCallback[] Snapshot()
    {
        if (Current(false) is not { Count: > 0 } stack)
            return [];
        while (LastSnapshots.Count < stack.Count)
            LastSnapshots.Add(new([]));
        var kept = LastSnapshots[stack.Count - 1];
        if (kept.TryGetTarget(out var last) && last.Length == stack.Count)
        {
            var same = true;
            for (var i = 0; same && i < last.Length; i++)
                same = last[i] == stack[^(i + 1)];
            if (same)
                return last;
        }

        var chain = stack.ToArray();
        Array.Reverse(chain);
        kept.SetTarget(chain);
        return chain;
    }

    /// <summary>Gets values that identify the callbacks and their generations, for cache keys.</summary>
    /// <param name="chain">The callbacks.</param>
    /// <param name="keys">Receives two values per callback.</param>
    public static void GetKeys(ReadOnlySpan<ITextMarkupCallback> chain, Span<long> keys)
    {
        for (var i = 0; i < chain.Length; i++)
        {
            var info = Infos.GetValue(chain[i], NewInfo);
            keys[i * 2] = info.Id;
            keys[(i * 2) + 1] = info.Generation;
        }
    }

    private static Info NewInfo(ITextMarkupCallback callback) => new(Interlocked.Increment(ref nextId));

    private static unsafe List<ITextMarkupCallback>? Current(bool create)
    {
        var context = (nint)ImGui.GetCurrentContext().Handle;
        if (context == 0)
            return create ? throw new InvalidOperationException("No current ImGui context.") : null;
        if (!Stacks.TryGetValue(context, out var stack) && create)
            Stacks.Add(context, stack = []);
        return stack;
    }

    private sealed class Info(long id)
    {
        private long generation;

        public long Id { get; } = id;

        public long Generation => Volatile.Read(ref this.generation);

        public void Invalidate() => Interlocked.Increment(ref this.generation);
    }
}
