using System.IO;
using System.Runtime.CompilerServices;

namespace Dalamud.Interface.Fonts;

public sealed partial record FontSpec
{
    /// <summary>A font for <see cref="From(ReadOnlySpan{FontSource}, float, float, bool)"/>.</summary>
    /// <remarks>Each conversion makes a spec without fallbacks. A <see cref="FontSpec"/> keeps its own. C# has no
    /// conversions from interfaces, so an <see cref="IFontId"/> or <see cref="IFontFamilyId"/> goes through
    /// <c>new(id)</c>.</remarks>
    /// <example><code>
    /// FontSpec.From([
    ///     "Comic Sans MS",
    ///     ("Times New Roman", FontStyle.Bold),
    ///     new FileInfo(path),
    ///     (bytes, 1),
    ///     new(fontId),
    /// ], sizePx: 20);
    /// </code></example>
    public readonly struct FontSource
    {
        /// <summary>Initializes a new instance of the <see cref="FontSource"/> struct.</summary>
        /// <param name="spec">The spec, with its own fallbacks.</param>
        public FontSource(FontSpec spec) => this.Spec = spec ?? throw new ArgumentNullException(nameof(spec));

        /// <summary>Initializes a new instance of the <see cref="FontSource"/> struct.</summary>
        /// <param name="font">The font.</param>
        [OverloadResolutionPriority(1)]
        public FontSource(IFontId font)
            : this(From(font, fallback: false))
        {
        }

        /// <summary>Initializes a new instance of the <see cref="FontSource"/> struct.</summary>
        /// <param name="family">The family.</param>
        /// <param name="style">The style.</param>
        public FontSource(IFontFamilyId family, FontStyle style = default)
            : this(From(family, style, fallback: false))
        {
        }

        /// <summary>Gets the spec, or <see langword="null"/> for the default value.</summary>
        internal FontSpec? Spec { get; }

        /// <summary>An installed font family by name, in its regular face.</summary>
        /// <param name="familyName">The family name, in any language the font has it in.</param>
        public static implicit operator FontSource(string familyName) => new(From(familyName, fallback: false));

        /// <summary>The face of an installed font family closest to a style.</summary>
        /// <param name="value">The family name and the style.</param>
        public static implicit operator FontSource((string FamilyName, FontStyle Style) value) =>
            new(From(value.FamilyName, value.Style, fallback: false));

        /// <summary>The face of a font family closest to a style.</summary>
        /// <param name="value">The family and the style.</param>
        public static implicit operator FontSource((IFontFamilyId Family, FontStyle Style) value) =>
            new(value.Family, value.Style);

        /// <summary>A game font.</summary>
        /// <param name="family">The family.</param>
        public static implicit operator FontSource(GameFontFamily family) => new(From(family, fallback: false));

        /// <summary>A game font, emboldened for weights above regular.</summary>
        /// <param name="value">The family and the style.</param>
        public static implicit operator FontSource((GameFontFamily Family, FontStyle Style) value) =>
            new(From(value.Family, value.Style, fallback: false));

        /// <summary>A game font at its own size.</summary>
        /// <param name="familyAndSize">The family and size.</param>
        public static implicit operator FontSource(GameFontFamilyAndSize familyAndSize) =>
            new(From(familyAndSize, fallback: false));

        /// <summary>A font file.</summary>
        /// <param name="file">The file.</param>
        public static implicit operator FontSource(FileInfo file) => new(From(file, fallback: false));

        /// <summary>A face of a font file.</summary>
        /// <param name="value">The file and the index of the face in the collection.</param>
        public static implicit operator FontSource((FileInfo File, int FontNo) value) =>
            new(From(value.File, fontNo: value.FontNo, fallback: false));

        /// <summary>Font data in a stream, read now.</summary>
        /// <param name="stream">The stream.</param>
        public static implicit operator FontSource(Stream stream) => new(From(stream, fallback: false));

        /// <summary>A face of font data in a stream, read now.</summary>
        /// <param name="value">The stream and the index of the face in the collection.</param>
        public static implicit operator FontSource((Stream Stream, int FontNo) value) =>
            new(From(value.Stream, fontNo: value.FontNo, fallback: false));

        /// <summary>Font data, copied now.</summary>
        /// <param name="data">The data.</param>
        public static implicit operator FontSource(byte[] data) => new(From(data, fallback: false));

        /// <summary>A face of font data, copied now.</summary>
        /// <param name="value">The data and the index of the face in the collection.</param>
        public static implicit operator FontSource((byte[] Data, int FontNo) value) =>
            new(From(value.Data, fontNo: value.FontNo, fallback: false));

        /// <inheritdoc cref="implicit operator FontSource(byte[])"/>
        public static implicit operator FontSource(ReadOnlyMemory<byte> data) =>
            new(From(data.Span, fallback: false));

        /// <inheritdoc cref="implicit operator FontSource(ValueTuple{byte[], int})"/>
        public static implicit operator FontSource((ReadOnlyMemory<byte> Data, int FontNo) value) =>
            new(From(value.Data.Span, fontNo: value.FontNo, fallback: false));

        /// <inheritdoc cref="implicit operator FontSource(byte[])"/>
        public static implicit operator FontSource(Memory<byte> data) => new(From(data.Span, fallback: false));

        /// <inheritdoc cref="implicit operator FontSource(ValueTuple{byte[], int})"/>
        public static implicit operator FontSource((Memory<byte> Data, int FontNo) value) =>
            new(From(value.Data.Span, fontNo: value.FontNo, fallback: false));

        /// <summary>A spec, with its own fallbacks.</summary>
        /// <param name="spec">The spec.</param>
        public static implicit operator FontSource(FontSpec spec) => new(spec);
    }
}
