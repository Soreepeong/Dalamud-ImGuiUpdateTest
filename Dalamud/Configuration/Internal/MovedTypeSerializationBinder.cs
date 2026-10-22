using Newtonsoft.Json.Serialization;

namespace Dalamud.Configuration.Internal;

/// <summary>Reads type names of types that have moved since they were saved. These are the font specs and font ids
/// of <c>Dalamud.Interface.FontIdentifier</c>, now in <see cref="Interface.Fonts"/>.</summary>
internal sealed class MovedTypeSerializationBinder : DefaultSerializationBinder
{
    private const string OldFontNamespace = "Dalamud.Interface.FontIdentifier.";
    private const string NewFontNamespace = "Dalamud.Interface.Fonts.";

    /// <summary>Gets the instance.</summary>
    public static MovedTypeSerializationBinder Instance { get; } = new();

    /// <inheritdoc/>
    public override Type BindToType(string? assemblyName, string typeName) =>
        base.BindToType(assemblyName, typeName.Replace(OldFontNamespace, NewFontNamespace, StringComparison.Ordinal));
}
