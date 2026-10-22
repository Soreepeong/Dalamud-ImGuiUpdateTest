using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Dalamud.Interface.Fonts;

/// <summary>Extensions for <see cref="ImFontRef"/>.</summary>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101", Justification = "Extension receivers are not members")]
public static class ImFontRefExtensions
{
    /// <summary>Extensions for loads of fonts.</summary>
    /// <param name="task">The load, such as from <see cref="Plugin.Services.IFontService.GetFontAsync"/>.</param>
    extension(Task<ImFontRef> task)
    {
        /// <summary>Faults with the font's <see cref="ImFontRef.Exception"/> if it could not be loaded.</summary>
        /// <returns>The font, loaded.</returns>
        /// <exception cref="FontLoadException">If the font could not be loaded.</exception>
        /// <example><code>
        /// var title = await fontService.GetFontAsync(spec).ThrowIfFailed();
        /// </code></example>
        public async Task<ImFontRef> ThrowIfFailed()
        {
            var font = await task.ConfigureAwait(false);
            if (font.Exception is { } e)
                ExceptionDispatchInfo.Throw(e);
            return font;
        }
    }
}
