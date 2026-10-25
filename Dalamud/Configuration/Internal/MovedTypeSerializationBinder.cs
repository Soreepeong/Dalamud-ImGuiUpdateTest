using System.Text.RegularExpressions;

using Dalamud.Interface.Fonts;

using Newtonsoft.Json.Serialization;

namespace Dalamud.Configuration.Internal;

/// <summary>Reads type names of types that have moved since they were saved.</summary>
/// <remarks><ol>
/// <li>The font ids of <c>Dalamud.Interface.FontIdentifier</c> map to those in <see cref="Interface.Fonts"/>.</li>
/// <li>Its <c>IFontSpec</c> and <c>SingleFontSpec</c> are read as <see cref="FontSpec"/>.
/// <see cref="LegacyFontSpecContractResolver"/> reads their <c>GlyphRanges</c>.</li>
/// </ol></remarks>
internal sealed partial class MovedTypeSerializationBinder : DefaultSerializationBinder
{
    private const string OldFontNamespace = "Dalamud.Interface.FontIdentifier.";
    private const string NewFontNamespace = "Dalamud.Interface.Fonts.";
    private const string FontSpecName = "Dalamud.Interface.Fonts.FontSpec";

    /// <summary>Gets the instance.</summary>
    public static MovedTypeSerializationBinder Instance { get; } = new();

    /// <inheritdoc/>
    public override Type BindToType(string? assemblyName, string typeName) =>
        base.BindToType(
            assemblyName,
            OldFontSpecRegex()
                .Replace(typeName, FontSpecName)
                .Replace(OldFontNamespace, NewFontNamespace, StringComparison.Ordinal));

    // Also in generic arguments.
    [GeneratedRegex(@"\bDalamud\.Interface\.FontIdentifier\.(?:I|Single)FontSpec\b")]
    private static partial Regex OldFontSpecRegex();
}
