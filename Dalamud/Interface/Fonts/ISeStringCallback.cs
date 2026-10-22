using Dalamud.Bindings.ImGui;

using Lumina.Text.ReadOnly;

namespace Dalamud.Interface.Fonts;

/// <summary>Decides how SeStrings are drawn, for text in fonts with <see cref="ImTextMarkup.SeString"/> or
/// <see cref="ImTextMarkup.MacroString"/>.</summary>
/// <remarks>Asked as <see cref="ITextMarkupCallback"/> is.</remarks>
public interface ISeStringCallback : ITextMarkupCallback
{
    /// <summary>Gets the theme for <see cref="Lumina.Text.Payloads.MacroCode.ColorType"/> and
    /// <see cref="Lumina.Text.Payloads.MacroCode.EdgeColorType"/>.</summary>
    /// <value><c>0</c> to use colors for Dark theme, <c>1</c> to use colors for Light theme, <c>2</c> to use colors
    /// for Classic FF theme, <c>3</c> to use colors for Clear Blue theme, or <see langword="null"/> to pass.</value>
    int? ThemeIndex => null;

    /// <summary>Decides whether a macro payload applies.</summary>
    /// <param name="payload">The payload.</param>
    /// <returns><see langword="true"/> to apply it, <see langword="false"/> to ignore it, or <see langword="null"/> to
    /// pass. Payloads no callback decides on apply.</returns>
    bool? ShouldHandle(ReadOnlySePayloadSpan payload) => null;
}
