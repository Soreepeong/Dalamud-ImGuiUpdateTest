using System.Collections.Generic;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Windows' system font fallback for a locale.</summary>
/// <remarks>Only <see cref="DefaultFontChain"/> asks for it. It cannot be saved.</remarks>
[JsonConverter(typeof(NotSerializableJsonConverter))]
internal sealed class SystemFallbackFontId : IFontId, IFontFamilyId
{
    /// <summary>Initializes a new instance of the <see cref="SystemFallbackFontId"/> class.</summary>
    /// <param name="locale">Locale name, such as "ja-JP".</param>
    public SystemFallbackFontId(string locale) => this.Locale = locale;

    /// <summary>Gets the locale name.</summary>
    /// <remarks>It decides between Han glyph variants.</remarks>
    public string Locale { get; }

    /// <inheritdoc cref="IFontFamilyId.Name"/>
    public LocalizedName Name => field ??= new($"System fallback ({this.Locale})");

    /// <inheritdoc/>
    public IFontFamilyId Family => this;

    /// <inheritdoc/>
    public FontStyle Style => FontStyle.Default;

    /// <inheritdoc/>
    public IReadOnlyList<IFontId> Fonts => field ??= [this];

    /// <inheritdoc/>
    public int FindBestMatch(FontStyle style) => 0;

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is SystemFallbackFontId other
        && string.Equals(this.Locale, other.Locale, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(this.Locale);

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(SystemFallbackFontId)}:{this.Locale}";
}
