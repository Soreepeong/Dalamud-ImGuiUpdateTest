using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Reads text of a markup into styled text, and lays it out.</summary>
/// <seealso cref="TextStylers"/>
internal abstract class TextStyler
{
    private readonly StyledText output = new();
    private int depth;

    /// <summary>Gets the markup it reads.</summary>
    public abstract ImTextMarkup Markup { get; }

    /// <summary>Gets the number of values <see cref="GetExtraKeys"/> writes.</summary>
    protected virtual int ExtraKeyCount => 0;

    /// <summary>Lays out text, with the callbacks pushed now. Equal arguments share a layout.</summary>
    /// <param name="font">The font.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="style">The style the text starts with.</param>
    /// <param name="text">The text as drawn.</param>
    /// <returns>The layout, whose offsets are into <paramref name="text"/>. Null outside the main thread.</returns>
    public TextLayout? CreateLayout(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text)
    {
        if (!ThreadSafety.IsMainThread)
            return null;

        font = font.Resolve();
        var chain = TextCallbackStack.Snapshot();
        var keyCount = (chain.Length * 2) + this.ExtraKeyCount;
        var keys = keyCount <= 32 ? stackalloc long[keyCount] : new long[keyCount];
        TextCallbackStack.GetKeys(chain, keys);
        this.GetExtraKeys(chain, keys[(chain.Length * 2)..]);
        return TextLayouter.TryGetCached(font, desc, style, text, keys, out var cached, out var key)
                   ? cached
                   : this.CreateUncachedLayout(font, desc, style, text, chain, key!);
    }

    /// <summary>Reads text.</summary>
    /// <param name="font">The font, resolved.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="style">The style the text starts with.</param>
    /// <param name="text">The text as drawn.</param>
    /// <param name="callbacks">The callbacks, in order.</param>
    /// <param name="nested">Whether this styler is already reading another text, from a callback. Buffers kept for
    /// reuse are in use then.</param>
    /// <param name="output">Receives the styled text, cleared.</param>
    protected abstract void Parse(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text,
        ITextMarkupCallback[] callbacks, bool nested, StyledText output);

    /// <summary>Gets what layouts depend on, besides the callbacks and the text.</summary>
    /// <param name="callbacks">The callbacks, in order.</param>
    /// <param name="keys">Receives <see cref="ExtraKeyCount"/> values.</param>
    protected virtual void GetExtraKeys(ReadOnlySpan<ITextMarkupCallback> callbacks, Span<long> keys)
    {
    }

    // CreateLayout of what is not cached. Apart, as its try block costs the cached path otherwise.
    private TextLayout CreateUncachedLayout(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text,
        ITextMarkupCallback[] chain, byte[] key)
    {
        // Callbacks may lay out text of this markup themselves.
        var nested = this.depth++ > 0;
        var start = FontProfiler.Begin(FontProfiler.Section.Layout);
        try
        {
            var styled = nested ? new() : this.output;
            styled.Clear();
            this.Parse(font, desc, style, text, chain, nested, styled);
            var layout = TextLayout.Rent(
                font,
                desc,
                styled.DefaultStyle,
                styled.Text,
                1,
                CollectionsMarshal.AsSpan(styled.Runs),
                CollectionsMarshal.AsSpan(styled.Objects),
                styled.GetDrawnOffsets());
            layout.RenderHook = new StyledTextHook(this.Markup, styled, chain, font, desc.Size);
            TextLayouter.AddCached(key, layout);
            return layout;
        }
        finally
        {
            FontProfiler.End(FontProfiler.Section.Layout, start, text.Length);
            this.depth--;
        }
    }
}
