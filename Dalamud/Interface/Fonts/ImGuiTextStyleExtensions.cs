using System.Diagnostics.CodeAnalysis;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Markup;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts;

/// <summary>Pushes the current font in another style.</summary>
/// <remarks>Main thread only, as ImGui is. Off the main thread, the pushes throw
/// <see cref="InvalidOperationException"/>.</remarks>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101", Justification = "Extension receivers are not members")]
public static class ImGuiTextStyleExtensions
{
    extension(ImGui)
    {
        /// <summary>Gets the current font.</summary>
        public static ImFontPtr Font => ImGui.GetFont();

        /// <summary>Gets the current font's <see cref="ImFont.Style"/>.</summary>
        public static ref readonly ImTextStyle TextStyle => ref ImGui.Font.Style;

        /// <summary>Pushes the current font with another style, keeping the size.</summary>
        /// <param name="style">The style.</param>
        /// <returns>A disposable object that pops the font.</returns>
        /// <seealso cref="ImGui.PopFont"/>
        public static ImPopFontDisposable PushTextStyle(scoped in ImTextStyle style)
        {
            // 0 keeps the size as it is. Push would apply the font's line height again.
            ImGui.PushFont(ImGui.Font.WithStyleForFrame(style), 0);
            return new(font: true);
        }

        /// <summary>Pushes the current font with a text color.</summary>
        /// <param name="color">The color, or 0 for the color text is drawn with.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushTextColor(ImTextColor color) =>
            ImGui.PushTextStyle(ImGui.TextStyle with { Color = color });

        /// <summary>Pushes the current font with an edge around the text.</summary>
        /// <param name="color">The color, or alpha 0 for none.</param>
        /// <param name="width">The width in ems, or 0 for none.</param>
        /// <param name="softness">A ratio of <paramref name="width"/>, or 0 for a hard edge.</param>
        /// <param name="minWidth">The minimum width in pixels, or 0 for no limit.</param>
        /// <param name="maxWidth">The maximum width in pixels, or 0 for no limit.</param>
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
                ImGui.TextStyle with
                {
                    Edge = new(
                        color ?? (on ? current.Color : 0xFF000000),
                        width ?? (on ? current.Width : ImTextEdge.DefaultWidth),
                        softness ?? current.Softness,
                        minWidth ?? current.MinWidth,
                        maxWidth ?? current.MaxWidth),
                });
        }

        /// <summary>Pushes the current font with a shadow under the text.</summary>
        /// <param name="color">The color, or alpha 0 for none.</param>
        /// <param name="offset">The offset in ems, or 0 for none.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushTextShadow(ImTextColor? color = null, Vector2? offset = null)
        {
            var current = ImGui.TextStyle.Shadow;
            var on = current.IsOn;
            return ImGui.PushTextStyle(
                ImGui.TextStyle with
                {
                    Shadow = new(
                        color ?? (on ? current.Color : 0xFF000000),
                        offset ?? (on ? current.Offset : ImTextShadow.DefaultOffset)),
                });
        }

        /// <summary>Pushes the current font with <see cref="ImTextStyle.Background"/> set.</summary>
        /// <param name="color">The color, or alpha 0 for none.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushTextBackground(ImTextColor color) =>
            ImGui.PushTextStyle(ImGui.TextStyle with { Background = color });

        /// <summary>Pushes the current font with lines under, over, or through the text.</summary>
        /// <param name="positions">Where the lines are, or <see cref="ImTextLinePosition.None"/> for none.</param>
        /// <param name="style">How the lines are drawn, or <see cref="ImTextLineStyle.None"/> for none.</param>
        /// <param name="color">The color, or 0 for the color of the text.</param>
        /// <param name="thickness">The thickness in pixels, or 0 for what the font says.</param>
        /// <param name="count">The number of lines at each position.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        /// <remarks>Null arguments keep the current lines' values. A style or a color shows nothing while there are no
        /// lines.</remarks>
        /// <seealso cref="ImTextStyle.Lines"/>
        public static ImPopFontDisposable PushTextLines(
            ImTextLinePosition? positions = null, ImTextLineStyle? style = null, ImTextColor? color = null,
            float? thickness = null, byte? count = null) => ImGui.PushTextStyle(
            ImGui.TextStyle with { Lines = Merge(ImGui.TextStyle.Lines, positions, style, color, thickness, count) });

        /// <summary>Pushes the current font with underlined text, or without the underline.</summary>
        /// <param name="color">The color of all the lines, 0 for the color of the text, or null to keep that of the
        /// current lines. The lines of a style share one color.</param>
        /// <param name="on">Whether to add the line, or to remove it.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        public static ImPopFontDisposable PushTextUnderline(ImTextColor? color = null, bool on = true) =>
            PushLinesAlso(ImTextLinePosition.Under, color, on);

        /// <summary>Pushes the current font with overlined text, or without the overline.</summary>
        /// <inheritdoc cref="PushTextUnderline"/>
        public static ImPopFontDisposable PushTextOverline(ImTextColor? color = null, bool on = true) =>
            PushLinesAlso(ImTextLinePosition.Over, color, on);

        /// <summary>Pushes the current font with struck-through text, or without the line through it.</summary>
        /// <inheritdoc cref="PushTextUnderline"/>
        public static ImPopFontDisposable PushTextStrikethrough(ImTextColor? color = null, bool on = true) =>
            PushLinesAlso(ImTextLinePosition.Through, color, on);

        /// <summary>Pushes the current font with a kind of decoration as <see cref="ImTextStyle.ExtraDecoration"/>,
        /// or without the decorations of that kind.</summary>
        /// <param name="id">The kind of decoration, built in or from <see cref="IFontService.RegisterTextDecoration"/>.
        /// </param>
        /// <param name="context">The context, such as a color, or null to keep that of the current one if it is of the
        /// same kind.</param>
        /// <param name="data">The data, or null to keep that of the current one if it is of the same kind.</param>
        /// <param name="on">Whether to add the decoration, or to remove it.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        /// <remarks>Removing <see cref="ImTextDecorationId.Background"/> or <see cref="ImTextDecorationId.Line"/> also
        /// clears <see cref="ImTextStyle.Background"/> or <see cref="ImTextStyle.Lines"/>.</remarks>
        public static ImPopFontDisposable PushTextDecoration(
            ImTextDecorationId id, uint? context = null, ulong? data = null, bool on = true)
        {
            if (!on)
                return ImGui.PushTextStyle(ImGui.TextStyle.WithoutDecoration(id));
            var current = ImGui.TextStyle.ExtraDecoration;
            if (current.Id != id)
                current = default;
            return ImGui.PushTextStyle(
                ImGui.TextStyle with
                {
                    ExtraDecoration = new(id, context ?? current.Context, data ?? current.Data),
                });
        }

        /// <summary>Pushes the bold form of the current font.</summary>
        /// <param name="bold">Whether bold.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        /// <remarks>The font's bold faces where it has them, emboldened where not.</remarks>
        public static ImPopFontDisposable PushTextBold(bool bold = true) =>
            ImGui.PushTextStyle(ImGui.TextStyle with { Bold = bold });

        /// <summary>Pushes the italic form of the current font.</summary>
        /// <param name="italic">Whether italic.</param>
        /// <inheritdoc cref="PushTextStyle(in ImTextStyle)" path="/returns"/>
        /// <remarks>The font's italic faces where it has them, slanted where not.</remarks>
        public static ImPopFontDisposable PushTextItalic(bool italic = true) =>
            ImGui.PushTextStyle(ImGui.TextStyle with { Italic = italic });

        /// <summary>Enters a scope where all text is of a markup.</summary>
        /// <param name="markup">The markup.</param>
        /// <param name="callback">Optional callback.</param>
        /// <returns>A disposable object that exits the scope.</returns>
        public static ImPopFontDisposable PushTextMarkup(ImTextMarkup markup, ITextMarkupCallback? callback = null)
        {
            ThreadSafety.AssertMainThread();
            var f = ImGui.Font;
            var pushFont = f.Style.Markup != markup;
            if (pushFont)
                ImGui.PushFont(f.WithStyleForFrame(f.Style with { Markup = markup }), 0);
            if (callback is not null)
                TextCallbackStack.Push(callback);
            return new(font: pushFont, textCallback: callback is not null);
        }

        /// <summary>Enters a scope where all text is SeString.</summary>
        /// <param name="callback">Optional callback.</param>
        /// <returns>A disposable object that exits the scope.</returns>
        public static ImPopFontDisposable PushTextSeString(ITextMarkupCallback? callback = null) =>
            ImGui.PushTextMarkup(ImTextMarkup.SeString, callback);

        /// <summary>Enters a scope where all text is macro string.</summary>
        /// <param name="callback">Optional callback.</param>
        /// <returns>A disposable object that exits the scope.</returns>
        public static ImPopFontDisposable PushTextMacroString(ITextMarkupCallback? callback = null) =>
            ImGui.PushTextMarkup(ImTextMarkup.MacroString, callback);
    }

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

    private static ImPopFontDisposable PushLinesAlso(ImTextLinePosition position, ImTextColor? color, bool on)
    {
        var current = ImGui.TextStyle.Lines;
        var positions = current.IsOn ? current.Positions : ImTextLinePosition.None;
        return ImGui.PushTextLines(on ? positions | position : positions & ~position, color: color);
    }
}
