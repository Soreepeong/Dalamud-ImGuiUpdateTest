using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Markup;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility;

using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace Dalamud.Interface.Fonts;

/// <summary>Draws and lays out text in the current font.</summary>
/// <remarks>Main thread only, as ImGui is. Off the main thread, the methods throw
/// <see cref="InvalidOperationException"/>.</remarks>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101", Justification = "Extension receivers are not members")]
public static unsafe class ImGuiTextExtensions
{
    extension(ImGui)
    {
        /// <summary>Draws text of a markup, through <see cref="ImGui.Text(ImU8String)"/>.</summary>
        /// <param name="text">The text.</param>
        /// <param name="markup">The markup.</param>
        /// <param name="callback">The callback to push for this text only. Keep it between frames.</param>
        /// <param name="wrap">Whether to wrap the text at the end of the window.</param>
        /// <remarks>The text is laid out as one, however long. <c>##</c> in it is drawn.</remarks>
        public static void TextMarkup(
            ImU8String text, ImTextMarkup markup, ITextMarkupCallback? callback = null, bool wrap = false)
        {
            using var pop = ImGui.PushTextMarkup(markup, callback);
            PlainText(text.Span, wrap);
            text.Recycle();
        }

        /// <summary>Draws a SeString compiled from <paramref name="text"/>.</summary>
        /// <param name="text">Macro string to compile to a SeString.</param>
        /// <param name="callback">The callback to push for this text only. Keep it between frames.</param>
        /// <param name="wrap">Whether to wrap the text.</param>
        public static void TextMacroString(
            ImU8String text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.TextMarkup(text, ImTextMarkup.MacroString, callback, wrap);

        /// <summary>Draws a SeString.</summary>
        /// <param name="text">Compiled SeString.</param>
        /// <param name="callback">The callback to push for this text only. Keep it between frames.</param>
        /// <param name="wrap">Whether to wrap the text.</param>
        public static void TextSeString(
            ReadOnlySpan<byte> text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.TextMarkup(text, ImTextMarkup.SeString, callback, wrap);

        /// <inheritdoc cref="TextSeString(ReadOnlySpan{byte},ITextMarkupCallback?,bool)"/>
        public static void TextSeString(
            SeStringBuilder text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.TextSeString(text.GetViewAsSpan(), callback, wrap);

        /// <inheritdoc cref="TextSeString(ReadOnlySpan{byte},ITextMarkupCallback?,bool)"/>
        public static void TextSeString(
            RentedSeStringBuilder text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.TextSeString(text.Builder, callback, wrap);

        /// <inheritdoc cref="TextSeString(ReadOnlySpan{byte},ITextMarkupCallback?,bool)"/>
        public static void Text(
            ReadOnlySeString text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.TextSeString(text, callback, wrap);

        /// <inheritdoc cref="TextSeString(ReadOnlySpan{byte},ITextMarkupCallback?,bool)"/>
        public static void Text(
            ReadOnlySeStringSpan text, ITextMarkupCallback? callback = null, bool wrap = false) =>
            ImGui.TextSeString(text, callback, wrap);

        /// <summary>Draws a styled text.</summary>
        /// <param name="text">Text to draw.</param>
        /// <param name="style">Style to apply.</param>
        /// <param name="wrap">Whether to wrap the text.</param>
        public static void TextStyled(ImU8String text, scoped in ImTextStyle style, bool wrap = false)
        {
            using var pop = ImGui.PushTextStyle(style);
            PlainText(text, wrap);
        }

        /// <summary>Draws a text with ranges in other styles, laid out as one text, as <see cref="ImGui.Text"/>
        /// does.</summary>
        /// <seealso cref="ImGui.PushTextWrapPos(float)"/>
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
        /// <returns>The layout.</returns>
        /// <example><code>
        /// using var layout = ImGui.CreateTextLayout(line, highlights, tabSize: 4);
        /// layout.Ptr.Render(ImGui.GetWindowDrawList(), pos, ImGui.GetColorU32(ImGuiCol.Text));
        /// var caret = layout.Ptr.HitTest(ImGui.GetMousePos() - pos);
        /// </code></example>
        public static ImTextLayoutRef CreateTextLayout(
            ReadOnlySpan<byte> text, ReadOnlySpan<ImTextStyleRange> ranges = default, float wrapWidth = 0,
            int tabSize = 0, ImTextLayoutFlags flags = ImTextLayoutFlags.None) =>
            CreateTextLayout(text, 1, ranges, wrapWidth, tabSize, flags);

        /// <inheritdoc cref="CreateTextLayout(ReadOnlySpan{byte},ReadOnlySpan{ImTextStyleRange},float,int,ImTextLayoutFlags)" path="/summary"/>
        /// <param name="text">Text to lay out.</param>
        /// <param name="ranges">The ranges, in chars of <paramref name="text"/>, as for
        /// <see cref="TextStyledRanges(ReadOnlySpan{char},ReadOnlySpan{ImTextStyleRange},bool)"/>.</param>
        /// <param name="wrapWidth">The width to wrap at, or 0 for no wrapping.</param>
        /// <param name="tabSize">Spaces between tab stops, counted from the start of each line, or 0 for 4.</param>
        /// <param name="flags">Layout flags.</param>
        /// <returns>The layout.</returns>
        public static ImTextLayoutRef CreateTextLayout(
            ReadOnlySpan<char> text, ReadOnlySpan<ImTextStyleRange> ranges = default, float wrapWidth = 0,
            int tabSize = 0, ImTextLayoutFlags flags = ImTextLayoutFlags.None) =>
            CreateTextLayout(MemoryMarshal.AsBytes(text), 2, ranges, wrapWidth, tabSize, flags);

        /// <inheritdoc cref="CreateTextLayout(ReadOnlySpan{byte},ReadOnlySpan{ImTextStyleRange},float,int,ImTextLayoutFlags)" path="/summary"/>
        /// <param name="text">The text.</param>
        /// <param name="markup">The markup.</param>
        /// <param name="wrapWidth">The width to wrap at, or 0 for no wrapping.</param>
        /// <param name="tabSize">Spaces between tab stops, counted from the start of each line, or 0 for 4.</param>
        /// <param name="flags">Layout flags.</param>
        /// <returns>The layout.</returns>
        public static ImTextLayoutRef CreateTextLayout(
            ReadOnlySpan<byte> text, ImTextMarkup markup, float wrapWidth = 0, int tabSize = 0,
            ImTextLayoutFlags flags = ImTextLayoutFlags.None)
        {
            ThreadSafety.AssertMainThread();
            if (Font.FromNative(ImGui.Font.Handle) is not { } font)
                return default;
            var desc = DescOf(wrapWidth, tabSize, flags);
            var style = ImGui.TextStyle with { Markup = markup };
            return new(NativeTextLayouts.ToNative(TextLayouter.CreateStyled(font, desc, style, text)));
        }

        /// <summary>Lays out a SeString in the current font and size as ImGui draws one, to draw and to query.
        /// </summary>
        /// <param name="text">The SeString.</param>
        /// <param name="wrapWidth">The width to wrap at, or 0 for no wrapping.</param>
        /// <param name="tabSize">Spaces between tab stops, counted from the start of each line, or 0 for 4.</param>
        /// <param name="flags">Layout flags.</param>
        /// <returns>The layout.</returns>
        public static ImTextLayoutRef CreateTextLayout(
            ReadOnlySeStringSpan text, float wrapWidth = 0, int tabSize = 0,
            ImTextLayoutFlags flags = ImTextLayoutFlags.None) =>
            ImGui.CreateTextLayout(text.Data, ImTextMarkup.SeString, wrapWidth, tabSize, flags);

        /// <summary>Gets what happened to a link of the styled text drawn last in this frame, like
        /// <see cref="ImGui.IsItemClicked()"/> after an item.</summary>
        /// <param name="interaction">What happened.</param>
        /// <param name="flags">What to look for.</param>
        /// <returns>Whether any of <paramref name="flags"/> happened.</returns>
        /// <remarks>Links are of text in fonts with <see cref="ImTextStyle.Markup"/>, drawn into the current window.
        /// Markup text drawn later replaces what is reported, even without links. Plain text does not.</remarks>
        public static bool TryGetTextInteraction(
            out TextInteraction interaction, TextInteractionFlags flags = TextInteractionFlags.LeftClick)
        {
            ThreadSafety.AssertMainThread();
            if (TextInteractionState.TryGet(out interaction) && (interaction.Flags & flags) != 0)
                return true;

            interaction = default;
            return false;
        }

        /// <summary>Marks all glyphs used from now on to be rasterized before this frame gets presented.</summary>
        /// <returns>A disposable object that stops the effect.</returns>
        /// <seealso cref="ImTextRenderFlags.CompleteGlyphs"/>
        public static ImPopFontDisposable PushCompleteGlyphs() => GlyphRasterBudget.BeginSynchronous();
    }

    private static void TextStyledRanges(
        ReadOnlySpan<byte> text, int codeUnitSize, ReadOnlySpan<ImTextStyleRange> ranges, bool wrap)
    {
        ThreadSafety.AssertMainThread();
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems)
            return;

        if (Font.FromNative(ImGui.Font.Handle) is not { } font)
        {
            if (codeUnitSize == 1)
                PlainText(text, wrap);
            else
                PlainText(MemoryMarshal.Cast<byte, char>(text), wrap);
            return;
        }

        ref var dc = ref window.DC;
        var wrapWidth = WrapWidthOf(window, wrap);
        var layout = CreateLayout(font, text, codeUnitSize, ranges, wrapWidth, 0, ImTextLayoutFlags.None);
        var pos = dc.CursorPos + new Vector2(0, dc.CurrLineTextBaseOffset);
        var bb = new ImRect(pos, pos + layout.Size);
        ImGuiP.ItemSize(layout.Size, 0);
        if (ImGuiP.ItemAdd(bb, 0))
            layout.Render(ImGui.GetWindowDrawList(), pos, ImGui.GetColorU32(ImGuiCol.Text));
    }

    /// <summary>Draws text with ImGui, wrapping at the end of the window if asked.</summary>
    /// <param name="text">The text.</param>
    /// <param name="wrap">Whether to wrap.</param>
    /// <remarks><see cref="ImGui.Begin(ImU8String, ImGuiWindowFlags)"/> resets a wrap position that an exception
    /// leaves.</remarks>
    private static void PlainText(ImU8String text, bool wrap)
    {
        if (wrap)
            ImGui.PushTextWrapPos(0);
        ImGui.Text(text);
        if (wrap)
            ImGui.PopTextWrapPos();
    }

    /// <summary>Gets the width <see cref="ImGui.Text"/> wraps text at.</summary>
    /// <param name="window">The window.</param>
    /// <param name="wrap">Whether to wrap at the end of the window.</param>
    /// <returns>The width, or 0 for none.</returns>
    private static float WrapWidthOf(ImGuiWindowPtr window, bool wrap)
    {
        ref var dc = ref window.DC;
        var wrapPos = wrap ? 0f : dc.TextWrapPos;
        return wrapPos >= 0 ? ImGuiP.CalcWrapWidthForPos(dc.CursorPos, wrapPos) : 0f;
    }

    /// <summary>Gets the layout parameters in the current font size, as ImGui would lay out text.</summary>
    /// <param name="wrapWidth">The wrap width.</param>
    /// <param name="tabSize">The tab size.</param>
    /// <param name="flags">The flags.</param>
    /// <returns>The parameters.</returns>
    private static TextLayoutDesc DescOf(float wrapWidth, int tabSize, ImTextLayoutFlags flags)
    {
        ImGuiP.GetTextDesc(out var desc, wrapWidth, flags);
        return TextLayoutDesc.FromNative(desc) with { TabSize = tabSize };
    }

    /// <summary>Lays out text in the current font and size, with the ranges as its runs.</summary>
    /// <param name="font">The font.</param>
    /// <param name="text">The text.</param>
    /// <param name="codeUnitSize">The size of a unit of <paramref name="text"/> in bytes.</param>
    /// <param name="ranges">The ranges.</param>
    /// <param name="wrapWidth">The wrap width.</param>
    /// <param name="tabSize">The tab size.</param>
    /// <param name="flags">The flags.</param>
    /// <returns>The layout.</returns>
    private static TextLayout CreateLayout(
        Font font, ReadOnlySpan<byte> text, int codeUnitSize, ReadOnlySpan<ImTextStyleRange> ranges,
        float wrapWidth, int tabSize, ImTextLayoutFlags flags)
    {
        var pooledRuns = PooledSpan<TextRun>.Rent(ranges.Length);
        var runs = pooledRuns.Span;
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
            runs);
        pooledRuns.Recycle();
        return res;
    }

    private static ImTextLayoutRef CreateTextLayout(
        ReadOnlySpan<byte> text, int codeUnitSize, ReadOnlySpan<ImTextStyleRange> ranges, float wrapWidth,
        int tabSize, ImTextLayoutFlags flags)
    {
        ThreadSafety.AssertMainThread();
        if (Font.FromNative(ImGui.Font.Handle) is not { } font)
            return default;
        var layout = CreateLayout(font, text, codeUnitSize, ranges, wrapWidth, tabSize, flags);
        return new(NativeTextLayouts.ToNative(layout));
    }
}
