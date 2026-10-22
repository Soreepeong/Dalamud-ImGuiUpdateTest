using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Formats;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal.SeStrings;

/// <summary>Reads SeStrings.</summary>
/// <remarks>Layouts draw their icons and entities, and make their links react.</remarks>
internal class SeStringStyler : TextStyler
{
    private readonly GfdFileView gfd;
    private readonly uint[,] colorTypes;
    private readonly SeStringParser parser;

    /// <summary>Initializes a new instance of the <see cref="SeStringStyler"/> class.</summary>
    /// <param name="gfd">The bitmap font icon table.</param>
    /// <param name="colorTypes">Colors of each color type and theme.</param>
    public SeStringStyler(GfdFileView gfd, uint[,] colorTypes)
    {
        this.gfd = gfd;
        this.colorTypes = colorTypes;
        this.parser = new(gfd, colorTypes);
    }

    /// <inheritdoc/>
    public override ImTextMarkup Markup => ImTextMarkup.SeString;

    /// <inheritdoc/>
    protected override int ExtraKeyCount => 1;

    /// <inheritdoc/>
    protected override void Parse(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text,
        ITextMarkupCallback[] callbacks, bool nested, StyledText output) =>
        this.ParseSeString(font, desc, style, text.ToArray(), callbacks, nested, output);

    /// <inheritdoc/>
    /// <remarks>The theme the callbacks ask for.</remarks>
    protected override void GetExtraKeys(ReadOnlySpan<ITextMarkupCallback> callbacks, Span<long> keys) =>
        keys[0] = SeStringParser.ThemeIndexOf(callbacks);

    /// <summary>Reads a SeString.</summary>
    /// <param name="font">The font, resolved.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="style">The style the text starts with.</param>
    /// <param name="seString">The SeString, which becomes <see cref="StyledText.Source"/>.</param>
    /// <param name="callbacks">The callbacks, in order.</param>
    /// <param name="nested">Whether the parser of this styler is in use.</param>
    /// <param name="output">Receives the styled text, cleared.</param>
    protected unsafe void ParseSeString(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style, byte[] seString,
        ITextMarkupCallback[] callbacks, bool nested, StyledText output)
    {
        var reader = nested ? new SeStringParser(this.gfd, this.colorTypes) : this.parser;
        reader.Parse(seString, this.Markup, font.GetVariant(style), desc.Size, desc.Density, callbacks, output);
    }
}
