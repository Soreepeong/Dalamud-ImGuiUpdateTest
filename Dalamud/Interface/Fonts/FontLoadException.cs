namespace Dalamud.Interface.Fonts;

/// <summary>Thrown when a font could not be loaded from a spec.</summary>
public sealed class FontLoadException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FontLoadException"/> class.</summary>
    /// <param name="spec">The font spec.</param>
    /// <param name="innerException">Inner exception.</param>
    public FontLoadException(FontSpec spec, Exception innerException)
        : base($"Failed to load the font {NameOf(spec)}: {innerException.Message}", innerException) =>
        this.Spec = spec;

    /// <summary>Gets the font spec that could not be loaded.</summary>
    public FontSpec Spec { get; }

    // A font id of a plugin may throw from ToString. The exception must still be made.
    private static string NameOf(FontSpec spec)
    {
        try
        {
            return spec.ToString();
        }
        catch (Exception)
        {
            return nameof(FontSpec);
        }
    }
}
