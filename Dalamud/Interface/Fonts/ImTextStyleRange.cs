using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts;

/// <summary>A range of text drawn in another style.</summary>
/// <param name="Offset">The start, in code units of the text. These are bytes of UTF-8, or chars of UTF-16.</param>
/// <param name="Length">The length, in code units of the text.</param>
/// <param name="Style">The style, used as is.</param>
/// <remarks>To change only some of the current style, use <c>ImGui.TextStyle with { Color = color }</c> and alike.
/// </remarks>
/// <seealso cref="ImGuiTextExtensions.extension(ImGui).TextStyledRanges(ReadOnlySpan{byte},ReadOnlySpan{ImTextStyleRange},bool)"/>
public readonly record struct ImTextStyleRange(int Offset, int Length, ImTextStyle Style);
