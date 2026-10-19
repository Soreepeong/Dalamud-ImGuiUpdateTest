using System.Reactive.Disposables;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Utility;

namespace Dalamud.Interface.ManagedFontAtlas.Internals;

/// <summary>
/// Standalone font atlas.
/// </summary>
internal sealed partial class FontAtlasFactory
{
    /// <summary>
    /// Fallback codepoints for ImFont.
    /// </summary>
    public const string FallbackCodepoints = "\u3013\uFFFD?-";

    /// <summary>
    /// Ellipsis codepoints for ImFont.
    /// </summary>
    public const string EllipsisCodepoints = "\u2026\u0085";

    private class DalamudFontAtlas : IFontAtlas, DisposeSafety.IDisposeCallback
    {
        private readonly DisposeSafety.ScopedFinalizer disposables = new();
        private readonly DelegateFontHandle.HandleManager delegateFontHandleManager;
        private readonly GamePrebakedFontHandle.HandleManager gameFontHandleManager;

        private bool disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DalamudFontAtlas"/> class.
        /// </summary>
        /// <param name="factory">The factory.</param>
        /// <param name="atlasName">Name of atlas, for debugging and logging purposes.</param>
        /// <param name="autoRebuildMode">Specify how to auto rebuild.</param>
        /// <param name="isGlobalScaled">Whether the fonts in the atlas are under the effect of global scale.</param>
        /// <param name="ownerPlugin">The owner plugin, if any.</param>
        public DalamudFontAtlas(
            FontAtlasFactory factory,
            string atlasName,
            FontAtlasAutoRebuildMode autoRebuildMode,
            bool isGlobalScaled,
            LocalPlugin? ownerPlugin)
        {
            this.OwnerPlugin = ownerPlugin;
            this.IsGlobalScaled = isGlobalScaled;
            try
            {
                this.AutoRebuildMode = autoRebuildMode;
                this.Name = atlasName;

                this.delegateFontHandleManager = this.disposables.Add(
                    new DelegateFontHandle.HandleManager(atlasName));
                this.gameFontHandleManager = this.disposables.Add(
                    new GamePrebakedFontHandle.HandleManager(atlasName, factory));
            }
            catch
            {
                this.disposables.Dispose();
                throw;
            }
        }

        /// <inheritdoc/>
        public event FontAtlasBuildStepDelegate? BuildStepChange;

        /// <inheritdoc/>
        public event Action? RebuildRecommend;

        /// <inheritdoc/>
        public event Action<DisposeSafety.IDisposeCallback>? BeforeDispose;

        /// <inheritdoc/>
        public event Action<DisposeSafety.IDisposeCallback, Exception?>? AfterDispose;

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public FontAtlasAutoRebuildMode AutoRebuildMode { get; }

        /// <inheritdoc/>
        public ImFontAtlasPtr ImAtlas => default;

        /// <inheritdoc/>
        public Task BuildTask => Task.CompletedTask;

        /// <inheritdoc/>
        public bool HasBuiltAtlas => false;

        /// <inheritdoc/>
        public bool IsGlobalScaled { get; }

        /// <summary>Gets the owner plugin, if any.</summary>
        public LocalPlugin? OwnerPlugin { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (this.disposed)
                return;

            this.BeforeDispose.InvokeSafely(this);

            try
            {
                this.disposed = true;
                this.disposables.Dispose();

                this.AfterDispose.InvokeSafely(this, null);
            }
            catch (Exception e)
            {
                this.AfterDispose.InvokeSafely(this, e);
            }
        }

        /// <inheritdoc/>
        public IDisposable SuppressAutoRebuild() => Disposable.Empty;

        /// <inheritdoc/>
        public IFontHandle NewGameFontHandle(GameFontStyle style) =>
            this.gameFontHandleManager.NewFontHandle(style);

        /// <inheritdoc/>
        public IFontHandle NewDelegateFontHandle(FontAtlasBuildStepDelegate buildStepDelegate) =>
            this.delegateFontHandleManager.NewFontHandle(buildStepDelegate);

        /// <inheritdoc/>
        public void BuildFontsOnNextFrame()
        {
        }

        /// <inheritdoc/>
        public void BuildFontsImmediately()
        {
        }

        /// <inheritdoc/>
        public Task BuildFontsAsync() => Task.CompletedTask;
    }
}
