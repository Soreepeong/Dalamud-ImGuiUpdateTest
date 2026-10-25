using System.Collections.Generic;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>Represents a font from a file on the filesystem.</summary>
public sealed class FileFontId : IFontFamilyId, IFontId
{
    // The full path, compared without case. Made once, as equality and hashing run on every lookup of a spec.
    private readonly string fullPath;

    /// <summary>Initializes a new instance of the <see cref="FileFontId"/> class.</summary>
    /// <param name="path">Path of the font file.</param>
    [JsonConstructor]
    internal FileFontId(string path)
    {
        this.Path = path ?? string.Empty;
        try
        {
            this.fullPath = System.IO.Path.GetFullPath(this.Path);
        }
        catch
        {
            // Not a valid path. It is compared as it is, and fails to load.
            this.fullPath = this.Path;
        }
    }

    /// <summary>Gets the path of the font file.</summary>
    [JsonProperty]
    public string Path { get; }

    /// <inheritdoc cref="IFontFamilyId.Name"/>
    [JsonIgnore]
    [field: JsonIgnore]
    public LocalizedName Name => field ??= new(System.IO.Path.GetFileNameWithoutExtension(this.Path));

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
    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj)
        || (obj is FileFontId other
            && string.Equals(this.fullPath, other.fullPath, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(this.fullPath);

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(FileFontId)}:{this.Path}";

    /// <inheritdoc/>
    public int FindBestMatch(FontStyle style) => 0;
}
