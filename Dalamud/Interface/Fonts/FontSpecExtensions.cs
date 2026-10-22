using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Interface.Fonts.Internal;

namespace Dalamud.Interface.Fonts;

/// <summary>Uses font specs directly as fonts.</summary>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101", Justification = "Extension receivers are not members")]
public static class FontSpecExtensions
{
    /// <summary>Extensions for font specs.</summary>
    /// <param name="spec">The spec.</param>
    extension(IFontSpec spec)
    {
        /// <summary>Pushes the font of a spec into the ImGui font stack, loading it if needed.</summary>
        /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
        /// <inheritdoc cref="ImFontRef.Push" path="/returns|/exception"/>
        /// <remarks>Loading blocks. Use <see cref="GetFontAsync"/> to load ahead. If the font could not be loaded, the
        /// current font is used. <inheritdoc cref="ImFontRef.Push" path="/remarks/node()"/></remarks>
        /// <example><code>
        /// using (FontSpec.Mono.Push())
        ///     ImGui.Text("Monospaced"u8);
        /// </code></example>
        public ImPopFontDisposable Push(float? sizePx = null) =>
            Service<FontResolver>.Get().Push(spec, sizePx);

        /// <summary>Gets the font of the spec, loading it if needed.</summary>
        /// <returns>The font. Its <see cref="ImFontRef.Exception"/> is set if it could not be loaded, now or on an
        /// earlier attempt.</returns>
        /// <remarks>Only the first use of a spec that was not loaded ahead (see <see cref="GetFontAsync"/>) blocks,
        /// until the font is loaded.</remarks>
        public ImFontRef GetFont() => Service<FontResolver>.Get().Get(spec);

        /// <summary>Gets the font of a spec, loading it in the background if needed.</summary>
        /// <param name="retry">Whether to try loading again if an earlier attempt failed.</param>
        /// <param name="cancellationToken">Cancels the wait, not the load.</param>
        /// <returns>The font. Its <see cref="ImFontRef.Exception"/> is set if it could not be loaded. Faults only if
        /// cancelled.</returns>
        /// <seealso cref="ImFontRefExtensions.ThrowIfFailed"/>
        public Task<ImFontRef> GetFontAsync(bool retry = false, CancellationToken cancellationToken = default) =>
            Service<FontResolver>.Get().GetAsync(spec, retry, cancellationToken);
    }
}
