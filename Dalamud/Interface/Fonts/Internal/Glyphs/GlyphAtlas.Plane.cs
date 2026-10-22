using System.Collections.Generic;
using System.Diagnostics;

using Dalamud.Logging.Internal;
using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Fonts.Internal.Glyphs;

/// <summary>The planes.</summary>
internal sealed unsafe partial class GlyphAtlas
{
    /// <summary>What a plane holds.</summary>
    public enum PlaneKind
    {
        /// <summary>Coverage, one byte per texel.</summary>
        Coverage,

        /// <summary>Colors, RGBA, four bytes per texel.</summary>
        Color,
    }

    /// <summary>What a slot of a plane is doing.</summary>
    public enum SlotState
    {
        /// <summary>Not holding anything. Cleared when taken.</summary>
        Free,

        /// <summary>Holding glyphs in use.</summary>
        Current,

        /// <summary>Holding glyphs no longer valid, for a few frames, so that they can be copied back.</summary>
        Retired,
    }

    private enum OpKind
    {
        Clear,
        Upload,
        Copy,
    }

    // A change queued for the GPU. Uploaded texels are at DataOffset in the plane's data.
    private readonly record struct Op(
        OpKind Kind,
        int Slot,
        int X,
        int Y,
        int Width,
        int Height,
        int DataOffset = 0,
        int SourceSlot = 0,
        int SourceX = 0,
        int SourceY = 0);

    /// <summary>A texture array of glyphs of one format, and the slots in it.</summary>
    /// <remarks>Changes are queued, and made on the GPU in order.</remarks>
    /// <seealso cref="Flush"/>
    public sealed class Plane : IDisposable
    {
        // The frames a retired slot keeps its pixels for.
        private const int RetiredFrames = 4;

        // The most bytes of queued texels kept allocated after a flush.
        private const int MaxKeptData = 4 << 20;

        // HardMaxSlices as a multiple of MaxSlices.
        private const int HardLimitFactor = 8;

        private const int ClearRows = 256;

        private static readonly ModuleLog Log = ModuleLog.Create<GlyphAtlas>();

        // Zeros for clearing ClearRows rows of any plane.
        private static byte[]? zeros;

        private readonly List<SlotInfo> slots = [];

        // Glyphs copied back are the ones drawn often, so slots of their own stay in use, and slots of new glyphs, many
        // drawn once, fall out of use whole.
        private readonly List<Lane> newLanes = [];
        private readonly List<Lane> restoredLanes = [];
        private readonly List<Op> ops = [];
        private byte[] data = new byte[64 * 1024];
        private int dataLength;
        private int lastGeneration;

        private ComPtr<ID3D11Device> device;
        private ComPtr<ID3D11Texture2D> texture;
        private ComPtr<ID3D11ShaderResourceView> view;
        private int capacity;
        private long lastLimitWarning;

        /// <summary>Initializes a new instance of the <see cref="Plane"/> class.</summary>
        /// <param name="kind">What it holds.</param>
        /// <param name="maxSlices">The value of <see cref="MaxSlices"/>.</param>
        public Plane(PlaneKind kind, int maxSlices)
        {
            this.Kind = kind;
            this.MaxSlices = maxSlices;
        }

        /// <summary>Gets what it holds.</summary>
        public PlaneKind Kind { get; }

        /// <summary>Gets or sets the number of slices in use the plane keeps to, once the text shown needs no more.
        /// </summary>
        /// <remarks>Slices used in a frame are kept beyond it, up to <see cref="HardMaxSlices"/>.</remarks>
        public int MaxSlices { get; set; }

        /// <summary>Gets the most slices the plane has in use. Glyphs beyond are not drawn.</summary>
        public int HardMaxSlices => Math.Min(this.MaxSlices * HardLimitFactor, SliceLimit);

        /// <summary>Gets the number of slices in use.</summary>
        public int SliceCount => this.Count(SlotState.Current);

        /// <summary>Gets the number of retired slices, still holding their pixels.</summary>
        public int RetiredCount => this.Count(SlotState.Retired);

        /// <summary>Gets the number of slots in the texture array, in any state.</summary>
        public int SlotCount => this.slots.Count;

        /// <summary>Gets a value indicating whether <see cref="Flush"/> has changes to make on the same device.
        /// </summary>
        public bool HasChanges => this.ops.Count != 0 || this.capacity < this.slots.Count;

        /// <summary>Gets the number of glyphs in the slices in use.</summary>
        public int GlyphCount { get; private set; }

        /// <summary>Gets the number of times a slice was retired.</summary>
        public int EvictionCount { get; private set; }

        private int MaxRetired => Math.Max(2, this.MaxSlices / 4);

        private int BytesPerTexel => this.Kind == PlaneKind.Color ? 4 : 1;

        private DXGI_FORMAT Format =>
            this.Kind == PlaneKind.Color ? DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM : DXGI_FORMAT.DXGI_FORMAT_R8_UNORM;

        /// <summary>Gets what a slot is doing.</summary>
        /// <param name="slot">The slot.</param>
        /// <returns>The state.</returns>
        public SlotState StateOf(int slot) => this.slots[slot].State;

        /// <inheritdoc/>
        public void Dispose() => this.Release();

        /// <summary>Makes the queued changes, and gets the texture array for the renderers to bind.</summary>
        /// <param name="d3dDevice">The device.</param>
        /// <param name="context">The immediate context.</param>
        /// <param name="bytes">Receives the bytes uploaded, added.</param>
        /// <returns>The texture array, or null if there is nothing in it.</returns>
        /// <remarks><ol>
        /// <li>With another device than before, the glyphs are lost.</li>
        /// <li>The view is valid until the next call.</li>
        /// </ol></remarks>
        public ID3D11ShaderResourceView* Flush(ID3D11Device* d3dDevice, ID3D11DeviceContext* context, ref long bytes)
        {
            if (this.device.Get() != d3dDevice)
            {
                if (!this.device.IsEmpty())
                    this.Release();
                this.device = new(d3dDevice);
            }

            if (this.slots.Count == 0)
                return null;

            if (this.capacity < this.slots.Count)
                this.Grow(context, this.slots.Count);

            var bpp = this.BytesPerTexel;
            var resource = (ID3D11Resource*)this.texture.Get();
            foreach (var op in this.ops)
            {
                switch (op.Kind)
                {
                    case OpKind.Clear:
                        zeros ??= GC.AllocateArray<byte>(SliceSize * 4 * ClearRows, true);
                        fixed (byte* p = zeros)
                        {
                            for (var y = 0; y < SliceSize; y += ClearRows)
                            {
                                var box = new D3D11_BOX(0, y, 0, SliceSize, y + ClearRows, 1);
                                context->UpdateSubresource(resource, (uint)op.Slot, &box, p, (uint)(SliceSize * bpp), 0);
                            }
                        }

                        bytes += SliceSize * SliceSize * bpp;
                        break;

                    case OpKind.Upload:
                    {
                        var box = new D3D11_BOX(op.X, op.Y, 0, op.X + op.Width, op.Y + op.Height, 1);
                        fixed (byte* p = &this.data[op.DataOffset])
                            context->UpdateSubresource(resource, (uint)op.Slot, &box, p, (uint)(op.Width * bpp), 0);
                        bytes += op.Width * op.Height * bpp;
                        break;
                    }

                    case OpKind.Copy:
                    {
                        var box = new D3D11_BOX(
                            op.SourceX, op.SourceY, 0, op.SourceX + op.Width, op.SourceY + op.Height, 1);
                        context->CopySubresourceRegion(
                            resource, (uint)op.Slot, (uint)op.X, (uint)op.Y, 0, resource, (uint)op.SourceSlot, &box);
                        break;
                    }
                }
            }

            this.ops.Clear();
            this.dataLength = 0;

            // A burst of new glyphs may have grown it, which is not kept.
            if (this.data.Length > MaxKeptData)
                this.data = new byte[64 * 1024];
            return this.view.Get();
        }

        /// <summary>Tests whether a slot holds glyphs in use of a generation.</summary>
        /// <param name="slot">The slot.</param>
        /// <param name="generation">The generation.</param>
        /// <returns>Whether it does.</returns>
        internal bool IsCurrent(int slot, int generation) =>
            this.IsAvailable(slot, generation) && this.slots[slot].State == SlotState.Current;

        /// <summary>Tests whether a slot still has the pixels of a generation, in use or retired.</summary>
        /// <param name="slot">The slot.</param>
        /// <param name="generation">The generation.</param>
        /// <returns>Whether it does.</returns>
        internal bool IsAvailable(int slot, int generation) =>
            slot < this.slots.Count && this.slots[slot] is { State: not SlotState.Free } s &&
            s.Generation == generation;

        /// <summary>Gets the generation of a slot.</summary>
        /// <param name="slot">The slot.</param>
        /// <returns>The generation.</returns>
        internal int GenerationOf(int slot) => this.slots[slot].Generation;

        /// <summary>Marks a slot as used in a frame.</summary>
        /// <param name="slot">The slot.</param>
        /// <param name="frame">The frame.</param>
        internal void MarkUsed(int slot, long frame) => this.slots[slot].LastUsedFrame = frame;

        /// <summary>Counts a glyph added to a slot.</summary>
        /// <param name="slot">The slot.</param>
        internal void AddGlyph(int slot)
        {
            this.slots[slot].GlyphCount++;
            this.GlyphCount++;
        }

        /// <summary>Allocates a rectangle, and queues an upload of texels to it.</summary>
        /// <param name="width">The width in texels.</param>
        /// <param name="height">The height.</param>
        /// <param name="texels">The texels, row by row.</param>
        /// <param name="frame">The frame.</param>
        /// <param name="slot">Receives the slot.</param>
        /// <param name="x">Receives the left.</param>
        /// <param name="y">Receives the top.</param>
        /// <returns>Whether there was room.</returns>
        internal bool TryPut(
            int width, int height, ReadOnlySpan<byte> texels, long frame, out int slot, out int x, out int y)
        {
            if (!this.TryAllocate(this.newLanes, width, height, frame, out slot, out x, out y))
                return false;

            var length = width * height * this.BytesPerTexel;
            if (this.dataLength + length > this.data.Length)
                Array.Resize(ref this.data, Math.Max(this.dataLength + length, this.data.Length * 2));
            texels[..length].CopyTo(this.data.AsSpan(this.dataLength));
            this.ops.Add(new(OpKind.Upload, slot, x, y, width, height, DataOffset: this.dataLength));
            this.dataLength += length;
            return true;
        }

        /// <summary>Allocates a rectangle, and queues a copy of texels from another slot to it.</summary>
        /// <param name="sourceSlot">The slot to copy from.</param>
        /// <param name="sourceGeneration">The generation of the texels in it.</param>
        /// <param name="sourceX">The left of the texels.</param>
        /// <param name="sourceY">The top of the texels.</param>
        /// <param name="width">The width in texels.</param>
        /// <param name="height">The height.</param>
        /// <param name="frame">The frame.</param>
        /// <param name="slot">Receives the slot.</param>
        /// <param name="x">Receives the left.</param>
        /// <param name="y">Receives the top.</param>
        /// <returns>Whether there was room, and the texels are still there.</returns>
        internal bool TryCopy(
            int sourceSlot, int sourceGeneration, int sourceX, int sourceY, int width, int height, long frame,
            out int slot, out int x, out int y)
        {
            // Allocating may take the slot copied from, if it had been retired.
            if (!this.TryAllocate(this.restoredLanes, width, height, frame, out slot, out x, out y) ||
                !this.IsAvailable(sourceSlot, sourceGeneration))
            {
                return false;
            }

            this.ops.Add(
                new(OpKind.Copy, slot, x, y, width, height, SourceSlot: sourceSlot, SourceX: sourceX, SourceY: sourceY));
            return true;
        }

        /// <summary>Retires unused slices beyond <see cref="MaxSlices"/>, frees old retired ones, and makes the queued
        /// changes.</summary>
        /// <param name="frame">The frame that ends.</param>
        internal void EndFrame(long frame)
        {
            // Those still drawn from stay, as the text shown needs them.
            for (var current = this.SliceCount; current > this.MaxSlices; current--)
            {
                var unused = this.LeastRecentlyUsed(frame);
                if (unused < 0)
                    break;
                this.Retire(unused, frame);
            }

            foreach (var s in this.slots)
            {
                if (s.State == SlotState.Retired && frame - s.RetiredFrame >= RetiredFrames)
                    s.State = SlotState.Free;
            }

            for (var retired = this.RetiredCount; retired > this.MaxRetired; retired--)
                this.slots[this.Oldest(SlotState.Retired)].State = SlotState.Free;

            if (!this.device.IsEmpty() && this.ops.Count > 0)
            {
                using var context = this.FlushNow();
            }
        }

        // Shelf packing in the open lanes, taking a slot for a new lane if none has room.
        private bool TryAllocate(
            List<Lane> lanes, int width, int height, long frame, out int slot, out int x, out int y)
        {
            // With a gap of one texel, which taking a slot cleared, for bilinear sampling at the edges.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var lane in lanes)
                {
                    if (!lane.Full && lane.TryAllocate(width + 1, height + 1, out x, out y))
                    {
                        slot = lane.Slot;
                        return true;
                    }
                }

                var taken = this.Take(frame);
                if (taken < 0)
                    break;
                lanes.Add(new(taken));
            }

            slot = x = y = 0;
            return false;
        }

        // A cleared slot, or -1 if there can be no more. At MaxSlices, retires a slice not used in this frame first.
        private int Take(long frame)
        {
            if (this.SliceCount >= this.MaxSlices)
            {
                var lru = this.LeastRecentlyUsed(frame);
                if (lru >= 0)
                {
                    this.Retire(lru, frame);
                }
                else if (this.SliceCount >= this.HardMaxSlices)
                {
                    // Text is drawn beyond what any screen shows.
                    var now = Stopwatch.GetTimestamp();
                    if (now - this.lastLimitWarning > Stopwatch.Frequency * 10)
                    {
                        this.lastLimitWarning = now;
                        Log.Warning(
                            "{Count} slices of {Kind} glyphs are in use in a frame. Glyphs beyond are not drawn",
                            this.SliceCount,
                            this.Kind);
                    }

                    return -1;
                }
            }

            var take = this.slots.FindIndex(x => x.State == SlotState.Free);
            if (take < 0 && (this.RetiredCount > this.MaxRetired || this.slots.Count >= SliceLimit))
                take = this.Oldest(SlotState.Retired);
            if (take < 0)
            {
                if (this.slots.Count >= SliceLimit)
                    return -1;
                this.slots.Add(new());
                take = this.slots.Count - 1;
            }

            var slot = this.slots[take];
            slot.State = SlotState.Current;
            slot.Generation = ++this.lastGeneration;
            slot.LastUsedFrame = frame;
            slot.GlyphCount = 0;
            this.ops.Add(new(OpKind.Clear, take, 0, 0, SliceSize, SliceSize));
            return take;
        }

        // The slice in use that was used least recently, and of those the oldest, or -1 if all were used in this frame.
        private int LeastRecentlyUsed(long frame)
        {
            var lru = -1;
            for (var i = 0; i < this.slots.Count; i++)
            {
                var s = this.slots[i];
                if (s.State != SlotState.Current || s.LastUsedFrame >= frame)
                    continue;
                var l = lru < 0 ? null : this.slots[lru];
                if (l is null || s.LastUsedFrame < l.LastUsedFrame ||
                    (s.LastUsedFrame == l.LastUsedFrame && s.Generation < l.Generation))
                {
                    lru = i;
                }
            }

            return lru;
        }

        private void Retire(int index, long frame)
        {
            var slot = this.slots[index];
            slot.State = SlotState.Retired;
            slot.RetiredFrame = frame;
            this.GlyphCount -= slot.GlyphCount;
            slot.GlyphCount = 0;
            this.newLanes.RemoveAll(x => x.Slot == index);
            this.restoredLanes.RemoveAll(x => x.Slot == index);
            this.EvictionCount++;
        }

        // The slot of a state taken longest ago, or -1.
        private int Oldest(SlotState state)
        {
            var oldest = -1;
            for (var i = 0; i < this.slots.Count; i++)
            {
                if (this.slots[i].State == state &&
                    (oldest < 0 || this.slots[i].Generation < this.slots[oldest].Generation))
                {
                    oldest = i;
                }
            }

            return oldest;
        }

        private int Count(SlotState state)
        {
            var n = 0;
            foreach (var s in this.slots)
                n += s.State == state ? 1 : 0;
            return n;
        }

        // Forgets everything, and lets go of the texture and the device.
        private void Release()
        {
            this.slots.Clear();
            this.newLanes.Clear();
            this.restoredLanes.Clear();
            this.ops.Clear();
            this.dataLength = 0;
            this.GlyphCount = 0;
            this.ReleaseTexture();
            this.device.Reset();
        }

        // Makes the queued changes now, and gets the immediate context.
        private ComPtr<ID3D11DeviceContext> FlushNow()
        {
            var context = default(ComPtr<ID3D11DeviceContext>);
            this.device.Get()->GetImmediateContext(context.GetAddressOf());
            var bytes = 0L;
            _ = this.Flush(this.device, context, ref bytes);
            return context;
        }

        private void Grow(ID3D11DeviceContext* context, int newCapacity)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = SliceSize,
                Height = SliceSize,
                MipLevels = 1,
                ArraySize = (uint)newCapacity,
                Format = this.Format,
                SampleDesc = new(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            using var newTexture = default(ComPtr<ID3D11Texture2D>);
            this.device.Get()->CreateTexture2D(&desc, null, newTexture.GetAddressOf()).ThrowOnError();
            for (var i = 0; i < this.capacity; i++)
            {
                context->CopySubresourceRegion(
                    (ID3D11Resource*)newTexture.Get(), (uint)i, 0, 0, 0, (ID3D11Resource*)this.texture.Get(), (uint)i, null);
            }

            var viewDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = desc.Format,
                ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2DARRAY,
                Texture2DArray = new()
                    { MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = 0, ArraySize = desc.ArraySize },
            };
            using var newView = default(ComPtr<ID3D11ShaderResourceView>);
            this.device.Get()->CreateShaderResourceView((ID3D11Resource*)newTexture.Get(), &viewDesc, newView.GetAddressOf())
                .ThrowOnError();

            newTexture.Swap(ref this.texture);
            newView.Swap(ref this.view);
            this.capacity = newCapacity;
        }

        private void ReleaseTexture()
        {
            this.view.Reset();
            this.texture.Reset();
            this.capacity = 0;
        }
    }

    // What is known of a slot of a plane's texture array.
    private sealed class SlotInfo
    {
        public SlotState State { get; set; }

        // Unique to each time the slot was taken, and larger for later ones.
        public int Generation { get; set; }

        // The frame its glyphs were last drawn or added in.
        public long LastUsedFrame { get; set; }

        public long RetiredFrame { get; set; }

        // Glyphs whose image is here, not counting masks.
        public int GlyphCount { get; set; }
    }

    // The packing of a slot, in shelves.
    private sealed class Lane(int slot)
    {
        private int cursorX;
        private int cursorY;
        private int shelfHeight;

        public int Slot { get; } = slot;

        public bool Full { get; private set; }

        public bool TryAllocate(int w, int h, out int x, out int y)
        {
            if (this.cursorX + w > SliceSize)
            {
                this.cursorX = 0;
                this.cursorY += this.shelfHeight;
                this.shelfHeight = 0;
            }

            if (this.cursorY + h > SliceSize)
            {
                // Glyphs come in similar sizes, so a lane that cannot take one is as good as full.
                this.Full = true;
                x = y = 0;
                return false;
            }

            x = this.cursorX;
            y = this.cursorY;
            this.cursorX += w;
            this.shelfHeight = Math.Max(this.shelfHeight, h);
            return true;
        }
    }
}
