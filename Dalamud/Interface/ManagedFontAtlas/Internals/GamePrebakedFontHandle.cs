using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Dalamud.Game.Text;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.Textures.TextureWraps;

using Lumina.Data.Files;

namespace Dalamud.Interface.ManagedFontAtlas.Internals;

/// <summary>
/// A font handle that uses the game's built-in fonts, optionally with some styling.
/// </summary>
internal class GamePrebakedFontHandle : FontHandle
{
    /// <summary>
    /// The smallest value of <see cref="SeIconChar"/>.
    /// </summary>
    public static readonly char SeIconCharMin = (char)Enum.GetValues<SeIconChar>().Min();

    /// <summary>
    /// The largest value of <see cref="SeIconChar"/>.
    /// </summary>
    public static readonly char SeIconCharMax = (char)Enum.GetValues<SeIconChar>().Max();

    /// <summary>
    /// Initializes a new instance of the <see cref="GamePrebakedFontHandle"/> class.
    /// </summary>
    /// <param name="manager">An instance of <see cref="IFontHandleManager"/>.</param>
    /// <param name="style">Font to use.</param>
    public GamePrebakedFontHandle(IFontHandleManager manager, GameFontStyle style)
        : base(manager)
    {
        if (!Enum.IsDefined(style.FamilyAndSize) || style.FamilyAndSize == GameFontFamilyAndSize.Undefined)
            throw new ArgumentOutOfRangeException(nameof(style), style, null);

        if (style.SizePt <= 0)
            throw new ArgumentException($"{nameof(style.SizePt)} must be a positive number.", nameof(style));

        this.FontStyle = style;
    }

    /// <summary>
    /// Provider for <see cref="IDalamudTextureWrap"/> for `common/font/fontNN.tex`.
    /// </summary>
    public interface IGameFontTextureProvider
    {
        /// <summary>
        /// Creates the <see cref="FdtFileView"/> for the <see cref="GameFontFamilyAndSize"/>.<br />
        /// <strong>Dispose after use.</strong>
        /// </summary>
        /// <param name="gffas">The font family and size.</param>
        /// <param name="fdtFileView">The view.</param>
        /// <returns>Dispose this after use..</returns>
        MemoryHandle CreateFdtFileView(GameFontFamilyAndSize gffas, out FdtFileView fdtFileView);

        /// <summary>
        /// Gets the number of font textures.
        /// </summary>
        /// <param name="texPathFormat">Format of .tex path.</param>
        /// <returns>The number of textures.</returns>
        int GetFontTextureCount(string texPathFormat);

        /// <summary>
        /// Gets the <see cref="TexFile"/> for the given index of a font.
        /// </summary>
        /// <param name="texPathFormat">Format of .tex path.</param>
        /// <param name="index">The index of .tex file.</param>
        /// <returns>The <see cref="TexFile"/>.</returns>
        TexFile GetTexFile(string texPathFormat, int index);

        /// <summary>
        /// Gets a new reference of the font texture.
        /// </summary>
        /// <param name="texPathFormat">Format of .tex path.</param>
        /// <param name="textureIndex">Texture index.</param>
        /// <returns>The texture.</returns>
        IDalamudTextureWrap NewFontTextureRef(string texPathFormat, int textureIndex);
    }

    /// <summary>
    /// Gets the font style.
    /// </summary>
    public GameFontStyle FontStyle { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(GamePrebakedFontHandle)}({this.FontStyle})";

    /// <summary>
    /// Manager for <see cref="GamePrebakedFontHandle"/>s.
    /// </summary>
    internal sealed class HandleManager : IFontHandleManager
    {
        private readonly Dictionary<GameFontStyle, int> gameFontsRc = [];
        private readonly HashSet<GamePrebakedFontHandle> handles = [];
        private readonly Lock syncRoot = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="HandleManager"/> class.
        /// </summary>
        /// <param name="atlasName">The name of the owner atlas.</param>
        /// <param name="gameFontTextureProvider">An instance of <see cref="IGameFontTextureProvider"/>.</param>
        public HandleManager(string atlasName, IGameFontTextureProvider gameFontTextureProvider)
        {
            this.GameFontTextureProvider = gameFontTextureProvider;
            this.Name = $"{atlasName}:{nameof(GamePrebakedFontHandle)}:Manager";
        }

        /// <inheritdoc/>
        public string Name { get; }

        /// <summary>
        /// Gets an instance of <see cref="IGameFontTextureProvider"/>.
        /// </summary>
        public IGameFontTextureProvider GameFontTextureProvider { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            // empty
        }

        /// <inheritdoc cref="IFontAtlas.NewGameFontHandle"/>
        public IFontHandle NewFontHandle(GameFontStyle style)
        {
            var handle = new GamePrebakedFontHandle(this, style);
            using (this.syncRoot.EnterScope())
            {
                this.handles.Add(handle);
                this.gameFontsRc[style] = this.gameFontsRc.GetValueOrDefault(style, 0) + 1;
            }

            return handle;
        }

        /// <inheritdoc/>
        public void FreeFontHandle(IFontHandle handle)
        {
            if (handle is not GamePrebakedFontHandle ggfh)
                return;

            using (this.syncRoot.EnterScope())
            {
                this.handles.Remove(ggfh);
                if (!this.gameFontsRc.ContainsKey(ggfh.FontStyle))
                    return;

                if ((this.gameFontsRc[ggfh.FontStyle] -= 1) == 0)
                    this.gameFontsRc.Remove(ggfh.FontStyle);
            }
        }
    }
}
