namespace Dalamud.Interface.Fonts;

/// <summary>Thrown when a font could not be loaded from a spec.</summary>
public sealed class FontLoadException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FontLoadException"/> class.</summary>
    /// <param name="spec">The font spec.</param>
    /// <param name="innerException">Inner exception.</param>
    public FontLoadException(IFontSpec spec, Exception innerException)
        : base($"Failed to load the font {spec}: {innerException.Message}", innerException) =>
        this.Spec = spec;

    /// <summary>Gets the font spec that could not be loaded.</summary>
    public IFontSpec Spec { get; }
}
