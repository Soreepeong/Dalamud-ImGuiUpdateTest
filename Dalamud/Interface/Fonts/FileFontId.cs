using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>Represents a font from a file on the filesystem.</summary>
public sealed class FileFontId : IFontFamilyId, IFontId
{
    /// <summary>Initializes a new instance of the <see cref="FileFontId"/> class.</summary>
    /// <param name="path">Path of the font file.</param>
    [JsonConstructor]
    internal FileFontId(string path) => this.Path = path;

    /// <summary>Gets the path of the font file.</summary>
    [JsonProperty]
    public string Path { get; init; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string EnglishName => System.IO.Path.GetFileNameWithoutExtension(this.Path);

    /// <inheritdoc/>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? LocaleNames => null;

    /// <inheritdoc/>
    [JsonIgnore]
    public IReadOnlyList<IFontId> Fonts => new List<IFontId> { this }.AsReadOnly();

    /// <inheritdoc/>
    [JsonIgnore]
    public IFontFamilyId Family => this;

    /// <inheritdoc/>
    [JsonIgnore]
    public FontStyle Style => FontStyle.Default;

    public static bool operator ==(FileFontId? left, FileFontId? right) => Equals(left, right);

    public static bool operator !=(FileFontId? left, FileFontId? right) => !Equals(left, right);

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
            return true;

        try
        {
            return obj is FileFontId other &&
                   string.Equals(
                       System.IO.Path.GetFullPath(this.Path),
                       System.IO.Path.GetFullPath(other.Path),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(System.IO.Path.GetFullPath(this.Path));

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(FileFontId)}:{this.Path}";

    /// <inheritdoc/>
    public int FindBestMatch(FontStyle style) => 0;
}
