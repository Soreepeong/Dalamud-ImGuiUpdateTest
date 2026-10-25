using System.Collections.Immutable;
using System.Reflection;

using Dalamud.Interface.Fonts;

using Newtonsoft.Json.Serialization;

namespace Dalamud.Configuration.Internal;

/// <summary>Reads the <c>GlyphRanges</c> of the <c>SingleFontSpec</c>s that Dalamud saved before, into
/// <see cref="FontSpec.CodepointRanges"/>.</summary>
/// <remarks>Use it with <see cref="MovedTypeSerializationBinder"/>, which reads the old type names.</remarks>
internal sealed class LegacyFontSpecContractResolver : DefaultContractResolver
{
    private LegacyFontSpecContractResolver()
    {
    }

    /// <summary>Gets the instance.</summary>
    public static LegacyFontSpecContractResolver Instance { get; } = new();

    /// <inheritdoc/>
    protected override JsonObjectContract CreateObjectContract(Type objectType)
    {
        var contract = base.CreateObjectContract(objectType);
        if (objectType == typeof(FontSpec))
        {
            contract.Properties.AddProperty(
                new()
                {
                    PropertyName = "GlyphRanges",
                    UnderlyingName = "GlyphRanges",
                    PropertyType = typeof(ushort[]),
                    DeclaringType = typeof(FontSpec),
                    ValueProvider = GlyphRangesSetter.Instance,
                    Readable = false,
                    Writable = true,
                });
        }

        return contract;
    }

    // Sets the codepoint ranges of a spec without them. Saved as ushorts, with that array type in the JSON.
    private sealed class GlyphRangesSetter : IValueProvider
    {
        public static readonly GlyphRangesSetter Instance = new();

        private static readonly PropertyInfo CodepointRanges =
            typeof(FontSpec).GetProperty(nameof(FontSpec.CodepointRanges))!;

        public void SetValue(object target, object? value)
        {
            if (value is not ushort[] ranges || target is not FontSpec { CodepointRanges: null })
                return;
            ImmutableArray<uint> converted = [.. ranges];
            CodepointRanges.SetValue(target, converted);
        }

        public object? GetValue(object target) => null;
    }
}
