using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>The text markup callbacks pushed in each ImGui context, and what happened to the links drawn last.
/// </summary>
/// <remarks>Main thread only.</remarks>
internal static class TextCallbackStack
{
    private static readonly Dictionary<nint, List<ITextMarkupCallback>> Stacks = [];
    private static readonly ConditionalWeakTable<ITextMarkupCallback, Info> Infos = [];
    private static long nextId;
    private static (nint Context, int Frame, InteractionRecord Record) lastInteraction;

    /// <summary>Gets the number of callbacks pushed in the current context.</summary>
    public static int Count => Current(false)?.Count ?? 0;

    /// <summary>Records what happened to a link of styled text being drawn, replacing what was before.</summary>
    /// <param name="record">What happened, or <see langword="default"/> for nothing.</param>
    public static unsafe void SetInteraction(in InteractionRecord record) =>
        lastInteraction = ((nint)ImGui.GetCurrentContext().Handle, ImGui.GetFrameCount(), record);

    /// <summary>Gets what happened to a link of the styled text drawn last in this frame.</summary>
    /// <param name="interaction">What happened.</param>
    /// <returns>Whether anything did.</returns>
    public static unsafe bool TryGetInteraction(out TextInteraction interaction)
    {
        ref readonly var r = ref lastInteraction.Record;
        if (r.Flags == TextInteractionFlags.None ||
            lastInteraction.Context != (nint)ImGui.GetCurrentContext().Handle ||
            lastInteraction.Frame != ImGui.GetFrameCount())
        {
            interaction = default;
            return false;
        }

        var source = r.Source.AsSpan();
        interaction = new(
            r.Markup,
            r.Offset,
            r.Length,
            source,
            r.Link.Offset,
            r.Link.Length,
            source.Slice(r.Link.TargetOffset, r.Link.TargetLength),
            r.Flags,
            r.Held);
        return true;
    }

    /// <summary>Pushes a callback.</summary>
    /// <param name="callback">The callback.</param>
    /// <returns>Pops the callback on <see cref="IDisposable.Dispose"/>.</returns>
    public static ImPopFontDisposable Push(ITextMarkupCallback callback)
    {
        Current(true)!.Add(callback ?? throw new ArgumentNullException(nameof(callback)));
        return new(textCallback: true);
    }

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
    public static void Invalidate(ITextMarkupCallback callback) => Infos.GetValue(callback, NewInfo)!.Generation++;

    /// <summary>Gets the callbacks to ask, in order.</summary>
    /// <returns>The callbacks.</returns>
    public static ITextMarkupCallback[] Snapshot()
    {
        if (Current(false) is not { Count: > 0 } stack)
            return [];
        var chain = stack.ToArray();
        Array.Reverse(chain);
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

    private static Info NewInfo(ITextMarkupCallback callback) => new() { Id = ++nextId };

    private static unsafe List<ITextMarkupCallback>? Current(bool create)
    {
        var context = (nint)ImGui.GetCurrentContext().Handle;
        if (context == 0)
            return create ? throw new InvalidOperationException("No current ImGui context.") : null;
        if (!Stacks.TryGetValue(context, out var stack) && create)
            Stacks.Add(context, stack = []);
        return stack;
    }

    /// <summary>What happened to a link, kept until asked for.</summary>
    /// <param name="Markup">The markup of the text.</param>
    /// <param name="Offset">The byte offset of what defines the link, in the text as drawn.</param>
    /// <param name="Length">The length of what defines the link, in the text as drawn.</param>
    /// <param name="Source">The text as read. Layouts never write to it after it is made.</param>
    /// <param name="Link">The link, in offsets into <paramref name="Source"/>.</param>
    /// <param name="Flags">What happened.</param>
    /// <param name="Held">Whether the link is held down.</param>
    public readonly record struct InteractionRecord(
        ImTextMarkup Markup,
        int Offset,
        int Length,
        byte[] Source,
        TextLink Link,
        TextInteractionFlags Flags,
        bool Held);

    private sealed class Info
    {
        public long Id { get; init; }

        public long Generation { get; set; }
    }
}
