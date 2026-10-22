using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.SeStrings;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace Dalamud.Interface.Fonts;

/// <summary>Utility methods for <see cref="ImFontPtr"/>, and for pushing styles of the current font.</summary>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101", Justification = "Extension receivers are not members")]
public static unsafe class ImFontExtensions
{
    extension(ImGui)
    {
        /// <summary>Gets the current font.</summary>
        public static ImFontPtr Font => ImGui.GetFont();

        /// <summary>Gets the current font's <see cref="ImFont.Style"/>.</summary>
        public static ref readonly ImTextStyle TextStyle => ref ImGui.Font.Style;

        /// <summary>Draws text of a markup.</summary>
        /// <param name="text">The text.</param>
        /// <param name="markup">The markup.</param>
        /// <param name="callback">Callback.</param>
        /// <param name="wrap">Whether to wrap the text.</param>
        /// <remarks>If <c>##</c> shows up within the text, ImGui will interpret it as the label delimiter.</remarks>
        public static void TextMarkup(
            ImU8String text, ImTextMarkup markup, ITextMarkupCallback? callback = null, bool wrap = false)
        {
            using var pushMarkup = ImGui.PushTextMarkup(markup, callback);
            using var pushWrap = wrap ? ImRaii.TextWrapPos(0) : null;
            ImGui.Text(text);
        }

        /// <summary>Draw a SeString by compiling <paramref name="text"/>.</summary>
        /// <param name="text">Macro string to compile to a SeString.</param>
        /// <param name="callback">Callback.</param>
        /// <param name="wrap">Whether to wrap the text.</param>
        /// <remarks>If <c>##</c> shows up within a payload in the macro string, ImGui will interpret it as the label
        /// delimiter.</remarks>
        public static void MacroString(ImU8String text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.TextMarkup(text, ImTextMarkup.MacroString, callback, wrap);

        /// <summary>Draw a SeString.</summary>
        /// <param name="text">Compiled SeString.</param>
        /// <param name="callback">Callback.</param>
        /// <param name="wrap">Whether to wrap the text.</param>
        /// <remarks>If <c>##</c> shows up within a SeString payload, ImGui will interpret it as the label delimiter.
        /// </remarks>
        public static void SeString(ReadOnlySpan<byte> text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.TextMarkup(text, ImTextMarkup.SeString, callback, wrap);

        /// <inheritdoc cref="SeString(ReadOnlySpan{byte},ITextMarkupCallback?,bool)"/>
        public static void SeString(SeStringBuilder text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.SeString(text.ToReadOnlySeString().AsSpan(), callback, wrap);

        /// <inheritdoc cref="SeString(ReadOnlySpan{byte},ITextMarkupCallback?,bool)"/>
        public static void SeString(
            RentedSeStringBuilder text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.SeString(text.Builder, callback, wrap);

        /// <summary>Draws a styled text.</summary>
        /// <param name="text">Text to draw.</param>
        /// <param name="style">Style to apply.</param>
        /// <param name="wrap">Whether to wrap the text.</param>
        public static void TextStyled(ImU8String text, scoped in ImTextStyle style, bool wrap = false)
        {
            using var pushWrap = wrap ? ImRaii.TextWrapPos(0) : null;
            using var p = ImGui.PushTextStyle(style);
            ImGui.Text(text);
        }

        /// <summary>Draws a text with ranges in other styles, laid out as one text, as <see cref="ImGui.Text"/>
        /// does (wrapping at <see cref="ImGui.PushTextWrapPos(float)"/>).</summary>
        /// <param name="text">UTF-8 text to draw.</param>
        /// <param name="ranges">The ranges, in units of <paramref name="text"/>, in any order. For overlapping ranges,
        /// the later one wins. Text outside them is in the current style.</param>
        /// <param name="wrap">Whether to wrap the text at the end of the window.</param>
        /// <example><code>
        /// var wavy = new ImTextLineDecoration(ImTextLinePosition.Under, ImTextLineStyle.Wavy, 0xFF0000FF);
        /// ImGui.TextStyledRanges(
        ///     "Hello, world"u8,
        ///     [
        ///         new(0, 5, ImGui.TextStyle with { Lines = wavy }),
        ///         new(7, 5, ImGui.TextStyle with { Color = 0xFF40C040, Bold = true }),
        ///     ]);
        /// </code></example>
        public static void TextStyledRanges(
            ReadOnlySpan<byte> text, ReadOnlySpan<ImTextStyleRange> ranges, bool wrap = false) =>
            TextStyledRanges(text, 1, ranges, wrap);

        /// <inheritdoc cref="TextStyledRanges(ReadOnlySpan{byte},ReadOnlySpan{ImTextStyleRange},bool)"/>
        /// <param name="text">Text to draw.</param>
        /// <param name="ranges">The ranges, in units of <paramref name="text"/>, in any order. For overlapping ranges,
        /// the later one wins. Text outside them is in the current style.</param>
        /// <param name="wrap">Whether to wrap the text at the end of the window.</param>
        public static void TextStyledRanges(
            ReadOnlySpan<char> text, ReadOnlySpan<ImTextStyleRange> ranges, bool wrap = false) =>
            TextStyledRanges(MemoryMarshal.AsBytes(text), 2, ranges, wrap);

        /// <summary>Lays out a text in the current font and size, with ranges in other styles, to draw and to query.
        /// </summary>
        /// <param name="text">UTF-8 text.</param>
        /// <param name="ranges">The ranges, in units of <paramref name="text"/>, in any order. For overlapping ranges,
        /// the later one wins. Text outside them is in the current style.</param>
        /// <param name="wrapWidth">The width to wrap at, or 0 for no wrapping.</param>
        /// <param name="tabSize">Spaces between tab stops, counted from the start of each line, or 0 for 4.</param>
        /// <param name="flags">Layout flags.</param>
        /// <returns>The layout, to <see cref="ImTextLayoutPtr.Release"/> when done. Null if the current font is not
        /// Dalamud's.</returns>
        /// <example><code>
        /// var layout = ImGui.CreateTextLayout(line, highlights, tabSize: 4);
        /// layout.Render(ImGui.GetWindowDrawList(), pos, ImGui.GetColorU32(ImGuiCol.Text));
        /// var caret = layout.HitTest(ImGui.GetMousePos() - pos);
        /// layout.Release();
        /// </code></example>
        public static ImTextLayoutPtr CreateTextLayout(
            ReadOnlySpan<byte> text, ReadOnlySpan<ImTextStyleRange> ranges = default, float wrapWidth = 0,
            int tabSize = 0, ImTextLayoutFlags flags = ImTextLayoutFlags.None) =>
            CreateTextLayout(text, 1, ranges, wrapWidth, tabSize, flags);

        /// <inheritdoc cref="CreateTextLayout(ReadOnlySpan{byte},ReadOnlySpan{ImTextStyleRange},float,int,ImTextLayoutFlags)"/>
        /// <param name="text">Text to lay out.</param>
        /// <param name="ranges">The ranges, in chars of <paramref name="text"/>, as for
        /// <see cref="TextStyledRanges(ReadOnlySpan{char},ReadOnlySpan{ImTextStyleRange},bool)"/>.</param>
        /// <param name="wrapWidth">The width to wrap at, or 0 for no wrapping.</param>
        /// <param name="tabSize">Spaces between tab stops, counted from the start of each line, or 0 for 4.</param>
        /// <param name="flags">Layout flags.</param>
        /// <remarks>Equal arguments share a layout. Offsets of the layout are bytes of <paramref name="text"/>. An offset
        /// is twice the char index.</remarks>
        public static ImTextLayoutPtr CreateTextLayout(
            ReadOnlySpan<char> text, ReadOnlySpan<ImTextStyleRange> ranges = default, float wrapWidth = 0,
            int tabSize = 0, ImTextLayoutFlags flags = ImTextLayoutFlags.None) =>
            CreateTextLayout(MemoryMarshal.AsBytes(text), 2, ranges, wrapWidth, tabSize, flags);

        /// <summary>Lays out text of a markup in the current font and size as ImGui draws it, to draw and to query.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="markup">The markup.</param>
        /// <param name="wrapWidth">The width to wrap at, or 0 for no wrapping.</param>
        /// <param name="tabSize">Spaces between tab stops, counted from the start of each line, or 0 for 4.</param>
        /// <param name="flags">Layout flags.</param>
        /// <returns>The layout, to <see cref="ImTextLayoutPtr.Release"/> when done. Null if the current font is not
        /// Dalamud's.</returns>
        /// <remarks>The markup decides the styles, and the pushed text callbacks apply, as for text in a font with
        /// <paramref name="markup"/>. Equal arguments share a layout. Offsets of the layout are bytes of
        /// <paramref name="text"/>.</remarks>
        public static ImTextLayoutPtr CreateTextLayout(
            ReadOnlySpan<byte> text, ImTextMarkup markup, float wrapWidth = 0, int tabSize = 0,
            ImTextLayoutFlags flags = ImTextLayoutFlags.None)
        {
            if (ManagedFont.FromNative(ImGui.Font.Handle) is not { } font)
                return ImTextLayoutPtr.Null;
            var desc = DescOf(wrapWidth, tabSize, flags);
            var style = ImGui.TextStyle.WithMarkup(markup);
            var layout = TextStylers.Get(markup)?.CreateLayout(font, desc, style, text)
                         ?? TextLayouter.Create(font, desc, style, text);
            return TextLayouter.ToNative(layout);
        }

        /// <summary>Lays out a SeString in the current font and size as ImGui draws one, to draw and to query.
        /// </summary>
        /// <param name="text">The SeString.</param>
        /// <param name="wrapWidth">The width to wrap at, or 0 for no wrapping.</param>
        /// <param name="tabSize">Spaces between tab stops, counted from the start of each line, or 0 for 4.</param>
        /// <param name="flags">Layout flags.</param>
        /// <returns>The layout, to <see cref="ImTextLayoutPtr.Release"/> when done. Null if the current font is not
        /// Dalamud's.</returns>
        /// <remarks>As <see cref="ImTextMarkup.SeString"/> text. Equal arguments share a layout. Offsets of the layout
        /// are bytes of the SeString.</remarks>
        public static ImTextLayoutPtr CreateTextLayout(
            ReadOnlySeStringSpan text, float wrapWidth = 0, int tabSize = 0,
            ImTextLayoutFlags flags = ImTextLayoutFlags.None) =>
            ImGui.CreateTextLayout(text.Data, ImTextMarkup.SeString, wrapWidth, tabSize, flags);

        /// <summary>Enters a scope where all text is of a markup.</summary>
        /// <param name="markup">The markup.</param>
        /// <param name="callback">Optional callback.</param>
        /// <returns>A disposable object that exits the scope.</returns>
        public static ImPopFontDisposable PushTextMarkup(ImTextMarkup markup, ITextMarkupCallback? callback = null)
        {
            var f = ImGui.Font;
            var pushFont = f.Style.Markup != markup;
            if (pushFont)
                _ = f.WithStyleForFrame(f.Style.WithMarkup(markup)).Push();
            if (callback is not null)
                _ = TextCallbackStack.Push(callback);
            return new(font: pushFont, textCallback: callback is not null);
        }

        /// <summary>Enters a scope where all text is macro string.</summary>
        /// <param name="callback">Optional callback.</param>
        /// <returns>A disposable object that exits the scope.</returns>
        public static ImPopFontDisposable PushMacroString(ITextMarkupCallback? callback = null) =>
            ImGui.PushTextMarkup(ImTextMarkup.MacroString, callback);

        /// <summary>Enters a scope where all text is SeString.</summary>
        /// <param name="callback">Optional callback.</param>
        /// <returns>A disposable object that exits the scope.</returns>
        public static ImPopFontDisposable PushSeString(ITextMarkupCallback? callback = null) =>
            ImGui.PushTextMarkup(ImTextMarkup.SeString, callback);

        /// <summary>Pushes the current font with another style, keeping the size.</summary>
        /// <param name="style">The style.</param>
        /// <returns>A disposable object that pops the font (<see cref="ImGui.PopFont"/>).</returns>
        public static ImPopFontDisposable PushTextStyle(scoped in ImTextStyle style) =>
            ImGui.Font.WithStyleForFrame(style).Push();

        /// <summary>Pushes the current font with a text color.</summary>
        /// <param name="color">The color, or 0 for the color text is drawn with.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushTextColor(ImTextColor color) =>
            ImGui.PushTextStyle(ImGui.TextStyle.WithColor(color));

        /// <summary>Pushes the current font with an edge around the text.</summary>
        /// <param name="color">The color, or alpha 0 for none.</param>
        /// <param name="width">The width in ems, or 0 for none.</param>
        /// <param name="softness">The fraction of the width the edge fades over, or 0 for a hard edge.</param>
        /// <param name="minWidth">The least width in pixels, or 0 for no limit.</param>
        /// <param name="maxWidth">The most width in pixels, or 0 for no limit.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        /// <remarks>Values not given are those of the current edge, or of a black default one if there is none.
        /// </remarks>
        public static ImPopFontDisposable PushTextEdge(
            ImTextColor? color = null, float? width = null, float? softness = null, float? minWidth = null,
            float? maxWidth = null)
        {
            var current = ImGui.TextStyle.Edge;
            var on = current.IsOn;
            return ImGui.PushTextStyle(
                ImGui.TextStyle.WithEdge(
                    color ?? (on ? current.Color : 0xFF000000),
                    width ?? (on ? current.Width : ImTextEdge.DefaultWidth),
                    softness ?? current.Softness,
                    minWidth ?? current.MinWidth,
                    maxWidth ?? current.MaxWidth));
        }

        /// <summary>Pushes the current font with a shadow under the text.</summary>
        /// <param name="color">The color, or alpha 0 for none.</param>
        /// <param name="offset">The offset in ems, or 0 for none.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushShadow(ImTextColor? color = null, Vector2? offset = null)
        {
            var current = ImGui.TextStyle.Shadow;
            var on = current.IsOn;
            return ImGui.PushTextStyle(
                ImGui.TextStyle.WithShadow(
                    color ?? (on ? current.Color : 0xFF000000),
                    offset ?? (on ? current.Offset : ImTextShadow.DefaultOffset)));
        }

        /// <summary>Pushes the current font with <see cref="ImTextStyle.ExtraDecoration"/> set.</summary>
        /// <param name="id">The kind of decoration, built in or from <see cref="IFontService.RegisterTextDecoration"/>.
        /// </param>
        /// <param name="context">The context, or null to keep that of the current one if it is of the same kind.</param>
        /// <param name="data">The data, or null to keep that of the current one if it is of the same kind.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushDecoration(
            ImTextDecorationId id, ImTextColor? context = null, ulong? data = null)
        {
            var current = ImGui.TextStyle.ExtraDecoration;
            if (current.Id != id)
                current = default;
            return ImGui.PushTextStyle(
                ImGui.TextStyle.WithDecoration(new(id, context ?? current.Context, data ?? current.Data)));
        }

        /// <summary>Pushes the current font without the decorations of a kind.</summary>
        /// <param name="id">The kind of decoration. <see cref="ImTextDecorationId.Background"/> and
        /// <see cref="ImTextDecorationId.Line"/> also clear <see cref="ImTextStyle.Background"/> and
        /// <see cref="ImTextStyle.Lines"/>.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushWithoutDecoration(ImTextDecorationId id) =>
            ImGui.PushTextStyle(ImGui.TextStyle.WithoutDecoration(id));

        /// <summary>Pushes the current font with lines under, over, or through the text.</summary>
        /// <param name="positions">Where the lines are, or <see cref="ImTextLinePosition.None"/> for none.</param>
        /// <param name="style">How the lines are drawn, or <see cref="ImTextLineStyle.None"/> for none.</param>
        /// <param name="color">The color, or 0 for the color of the text.</param>
        /// <param name="thickness">The thickness in pixels, or 0 for what the font says.</param>
        /// <param name="count">The number of lines at each position.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        /// <seealso cref="ImTextStyle.Lines"/>
        public static ImPopFontDisposable PushLines(
            ImTextLinePosition? positions = null, ImTextLineStyle? style = null, ImTextColor? color = null,
            float? thickness = null, byte? count = null) => ImGui.PushTextStyle(
            ImGui.TextStyle.WithLines(Merge(ImGui.TextStyle.Lines, positions, style, color, thickness, count)));

        /// <summary>Pushes the current font with underlined text.</summary>
        /// <param name="color">The color of all the lines, 0 for the color of the text, or null to keep that of the
        /// current lines.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushUnderline(ImTextColor? color = null) =>
            PushLinesAlso(ImTextLinePosition.Under, color);

        /// <summary>Pushes the current font with overlined text.</summary>
        /// <inheritdoc cref="PushUnderline"/>
        public static ImPopFontDisposable PushOverline(ImTextColor? color = null) =>
            PushLinesAlso(ImTextLinePosition.Over, color);

        /// <summary>Pushes the current font with struck-through text.</summary>
        /// <inheritdoc cref="PushUnderline"/>
        public static ImPopFontDisposable PushStrikethrough(ImTextColor? color = null) =>
            PushLinesAlso(ImTextLinePosition.Through, color);

        /// <summary>Pushes the current font with <see cref="ImTextStyle.Background"/> set.</summary>
        /// <param name="color">The color, or alpha 0 for none.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushBackground(ImTextColor color) =>
            ImGui.PushTextStyle(ImGui.TextStyle.WithBackground(color));

        /// <summary>Pushes the font of a spec, keeping the style of the current font.</summary>
        /// <param name="spec">The spec.</param>
        /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
        /// <returns>A disposable object that pops the font.</returns>
        public static ImPopFontDisposable PushFontFamily(IFontSpec spec, float? sizePx = null) =>
            Service<FontResolver>.Get().Push(spec, sizePx, ImGui.TextStyle);

        /// <summary>Pushes a font, keeping the style of the current font.</summary>
        /// <param name="font">The font.</param>
        /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
        /// <returns>A disposable object that pops the font.</returns>
        public static ImPopFontDisposable PushFontFamily(ImFontRef font, float? sizePx = null) =>
            Service<FontResolver>.Get().Push(font.Entry, sizePx, ImGui.TextStyle);

        /// <summary>Pushes a font, keeping the style of the current font.</summary>
        /// <param name="font">The font to take the family from, or null for the current font.</param>
        /// <param name="fontSizeBaseUnscaled">The size, as for <see cref="ImGui.PushFont(ImFontPtr, float)"/>, or 0 to
        /// keep the current size.</param>
        /// <returns>A disposable object that pops the font.</returns>
        public static ImPopFontDisposable PushFontFamily(ImFontPtr font, float fontSizeBaseUnscaled = 0) =>
            (font.IsNull ? ImGui.Font : font.WithStyleForFrame(ImGui.TextStyle)).Push(fontSizeBaseUnscaled);

        /// <summary>Makes text drawn until popped have all its glyphs, however long rasterizing them takes.</summary>
        /// <returns>A disposable object that pops it.</returns>
        /// <remarks>
        /// <para>Glyphs not drawn before are otherwise rasterized in the background once rasterizing has taken its
        /// share of the frame, and drawn as nothing until they are ready, a frame or a few later. Text drawn every frame
        /// does not need this. Text drawn once, such as into a texture or a draw list kept for later, does.</para>
        /// <para>It applies to text as it is drawn, not as it is measured. Pops left at the end of the frame are done
        /// then, with a warning.</para>
        /// </remarks>
        /// <seealso cref="ImTextRenderFlags.CompleteGlyphs"/>
        public static ImPopFontDisposable PushCompleteGlyphs() => GlyphRasterQueue.Synchronous();

        /// <summary>Pushes the bold form of the current font.</summary>
        /// <param name="bold">Whether bold.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        /// <remarks>The font's bold faces where it has them, emboldened where not.</remarks>
        public static ImPopFontDisposable PushBold(bool bold = true) =>
            ImGui.PushTextStyle(ImGui.TextStyle.WithBold(bold));

        /// <summary>Gets what happened to a link of the styled text drawn last in this frame, as with
        /// <see cref="ImGui.IsItemClicked()"/> after an item.</summary>
        /// <param name="interaction">What happened.</param>
        /// <param name="flags">What to look for.</param>
        /// <returns>Whether any of <paramref name="flags"/> happened.</returns>
        /// <remarks>Links are of text in fonts with <see cref="ImTextStyle.Markup"/>, drawn into the current window.
        /// Text drawn later, of any markup, replaces what is reported.</remarks>
        public static bool TryGetTextInteraction(
            out TextInteraction interaction, TextInteractionFlags flags = TextInteractionFlags.LeftClick) =>
            TextCallbackStack.TryGetInteraction(out interaction) && (interaction.Flags & flags) != 0;

        /// <summary>Pushes the italic form of the current font.</summary>
        /// <param name="italic">Whether italic.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        /// <remarks>The font's italic faces where it has them, slanted where not.</remarks>
        public static ImPopFontDisposable PushItalic(bool italic = true) =>
            ImGui.PushTextStyle(ImGui.TextStyle.WithItalic(italic));
    }

    /// <param name="font">The font.</param>
    extension(ImFontPtr font)
    {
        /// <summary>Pushes this font.</summary>
        /// <param name="fontSizeBaseUnscaled">Optional scale.</param>
        /// <returns>Calls <see cref="ImGui.PopFont"/> on <see cref="IDisposable.Dispose"/>.</returns>
        public ImPopFontDisposable Push(float fontSizeBaseUnscaled = 0)
        {
            ImGui.PushFont(font, fontSizeBaseUnscaled);
            return new(font: true);
        }

        /// <summary>Gets the font with another style, for use in this frame.</summary>
        /// <param name="style">The style.</param>
        /// <returns>The font in <paramref name="style"/>, the same for the same style within a frame.
        /// <paramref name="font"/> itself if it is not a Dalamud font.</returns>
        /// <remarks>The font is valid until the frame being built has been drawn. Kept longer, it may show another
        /// style. To use a styled font over frames, keep the <see cref="ImFontRef"/> and the style, and call this in
        /// each frame.</remarks>
        public ImFontPtr WithStyleForFrame(in ImTextStyle style) =>
            font.IsNull || ManagedFont.FromNative(font.Handle) is not { } f ? font : f.GetVariant(style);

        /// <summary>Tests whether a font has a glyph for a codepoint, from its own glyphs or its fallbacks.</summary>
        /// <param name="codepoint">The codepoint.</param>
        /// <returns>Whether the glyph exists.</returns>
        public bool IsGlyphInFont(uint codepoint) => font.GetGlyphIndex(codepoint) != 0;
    }

    private static void TextStyledRanges(
        ReadOnlySpan<byte> text, int codeUnitSize, ReadOnlySpan<ImTextStyleRange> ranges, bool wrap)
    {
        using var pushWrap = wrap ? ImRaii.TextWrapPos(0) : null;
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems)
            return;

        if (ManagedFont.FromNative(ImGui.Font.Handle) is not { } font)
        {
            if (codeUnitSize == 1)
                ImGui.Text(text);
            else
                ImGui.Text(MemoryMarshal.Cast<byte, char>(text));
            return;
        }

        ref var dc = ref window.DC;
        var wrapWidth = dc.TextWrapPos >= 0 ? ImGuiP.CalcWrapWidthForPos(dc.CursorPos, dc.TextWrapPos) : 0f;
        var layout = CreateLayout(font, text, codeUnitSize, ranges, wrapWidth, 0, ImTextLayoutFlags.None);
        var pos = dc.CursorPos + new Vector2(0, dc.CurrLineTextBaseOffset);
        var bb = new ImRect(pos, pos + layout.Size);
        ImGuiP.ItemSize(layout.Size, 0);
        if (ImGuiP.ItemAdd(bb, 0))
            layout.Render(ImGui.GetWindowDrawList(), pos, ImGui.GetColorU32(ImGuiCol.Text));
    }

    // Layout parameters in the current font size, as ImGui would lay out text.
    private static TextLayoutDesc DescOf(float wrapWidth, int tabSize, ImTextLayoutFlags flags)
    {
        ImGuiP.GetTextDesc(out var desc, wrapWidth, flags);
        return TextLayoutDesc.FromNative(desc) with { TabSize = tabSize };
    }

    // Lays out text in the current font and size, with the ranges as its runs.
    private static TextLayout CreateLayout(
        ManagedFont font, ReadOnlySpan<byte> text, int codeUnitSize, ReadOnlySpan<ImTextStyleRange> ranges,
        float wrapWidth, int tabSize, ImTextLayoutFlags flags)
    {
        var runs = ArrayPool<TextRun>.Shared.Rent(ranges.Length);
        for (var i = 0; i < ranges.Length; i++)
        {
            var r = ranges[i];
            runs[i] = new(r.Offset * codeUnitSize, (r.Offset + r.Length) * codeUnitSize, Style: r.Style);
        }

        var res = TextLayouter.Create(
            font,
            DescOf(wrapWidth, tabSize, flags),
            ImGui.TextStyle,
            text,
            codeUnitSize,
            runs.AsSpan(0, ranges.Length));
        ArrayPool<TextRun>.Shared.Return(runs); // let GC free it on failure
        return res;
    }

    private static ImTextLayoutPtr CreateTextLayout(
        ReadOnlySpan<byte> text, int codeUnitSize, ReadOnlySpan<ImTextStyleRange> ranges, float wrapWidth,
        int tabSize, ImTextLayoutFlags flags) =>
        ManagedFont.FromNative(ImGui.Font.Handle) is { } font
            ? TextLayouter.ToNative(CreateLayout(font, text, codeUnitSize, ranges, wrapWidth, tabSize, flags))
            : ImTextLayoutPtr.Null;

    private static ImTextLineDecoration Merge(
        in ImTextLineDecoration current, ImTextLinePosition? positions, ImTextLineStyle? style, ImTextColor? color,
        float? thickness, byte? count)
    {
        var on = current.IsOn;
        return new(
            positions ?? (on ? current.Positions : ImTextLinePosition.None),
            style ?? (on ? current.Style : ImTextLineStyle.Solid),
            color ?? (on ? current.Color : default),
            thickness ?? (on ? current.Thickness : 0),
            count ?? (on ? current.Count : (byte)1));
    }

    private static ImPopFontDisposable PushLinesAlso(ImTextLinePosition position, ImTextColor? color)
    {
        var current = ImGui.TextStyle.Lines;
        return ImGui.PushLines((current.IsOn ? current.Positions : ImTextLinePosition.None) | position, color: color);
    }
}
