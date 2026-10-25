using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace Dalamud.Interface.Fonts;

/// <summary>Ready-made <see cref="ISeStringCallback"/>s.</summary>
public static class SeStringCallbacks
{
    /// <summary>Gets a callback that keeps the edge color of the font, ignoring <see cref="MacroCode.EdgeColor"/> and
    /// <see cref="MacroCode.EdgeColorType"/>.</summary>
    public static ISeStringCallback IgnoreEdgeColor { get; } = new IgnoreEdgeColorCallback();

    private sealed class IgnoreEdgeColorCallback : ISeStringCallback
    {
        public bool? ShouldHandle(ReadOnlySePayloadSpan payload) =>
            payload.MacroCode is MacroCode.EdgeColor or MacroCode.EdgeColorType ? false : null;
    }
}
