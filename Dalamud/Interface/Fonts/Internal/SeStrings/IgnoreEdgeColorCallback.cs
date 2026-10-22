using Dalamud.Interface.Fonts;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace Dalamud.Interface.Fonts.Internal.SeStrings;

/// <summary>Keeps the edge color of the font, ignoring <see cref="MacroCode.EdgeColor"/> and
/// <see cref="MacroCode.EdgeColorType"/>.</summary>
internal sealed class IgnoreEdgeColorCallback : ISeStringCallback
{
    /// <summary>Gets the instance.</summary>
    public static IgnoreEdgeColorCallback Instance { get; } = new();

    /// <inheritdoc/>
    public bool? ShouldHandle(ReadOnlySePayloadSpan payload) =>
        payload.MacroCode is MacroCode.EdgeColor or MacroCode.EdgeColorType ? false : null;
}
