using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookHub.Core.Pdf;

namespace BookHub.Reader.Services;

/// <summary>
/// Bounded ring buffer of up to 12 rendered page surfaces for the active tab's viewport.
/// Creates frozen BitmapSource objects off-thread from ArrayPool buffers so UI thread never blocks
/// and WPF Image bindings always receive a distinct, immutable BitmapSource reference.
/// </summary>
public sealed class PdfSurfacePool
{
    public const int MaxSurfaces = 12;

    private sealed class SurfaceSlot
    {
        public int PageNumber;
        public bool IsHighRes;
        public int PixelWidth;
        public int PixelHeight;
        public BitmapSource? Bitmap;
        public Action? DetachCallback;
    }

    private readonly SurfaceSlot[] _slots = new SurfaceSlot[MaxSurfaces];
    private readonly object _sync = new();
    private int _ringCursor;

    public PdfSurfacePool()
    {
        for (int i = 0; i < MaxSurfaces; i++)
        {
            _slots[i] = new SurfaceSlot();
        }
    }

    public int ActiveSurfaceCount
    {
        get
        {
            lock (_sync)
            {
                return _slots.Count(s => s.PageNumber > 0 && s.Bitmap != null);
            }
        }
    }

    public string GetAssignedPagesSummary()
    {
        lock (_sync)
        {
            var pages = _slots
                .Where(s => s.PageNumber > 0 && s.Bitmap != null)
                .OrderBy(s => s.PageNumber)
                .Select(s => s.IsHighRes ? $"{s.PageNumber}" : $"{s.PageNumber}*")
                .ToList();

            return pages.Count > 0 ? string.Join(", ", pages) : "—";
        }
    }

    public bool HasSatisfiedSurface(int pageNumber, bool requireHighRes, int desiredWidthPx)
    {
        lock (_sync)
        {
            for (int i = 0; i < MaxSurfaces; i++)
            {
                var slot = _slots[i];
                if (slot.PageNumber == pageNumber && slot.Bitmap != null)
                {
                    if (!requireHighRes)
                        return true;

                    if (slot.IsHighRes && Math.Abs(slot.PixelWidth - desiredWidthPx) < 96)
                        return true;
                }
            }
            return false;
        }
    }

    public BitmapSource? TryGetExistingSurface(int pageNumber)
    {
        lock (_sync)
        {
            for (int i = 0; i < MaxSurfaces; i++)
            {
                if (_slots[i].PageNumber == pageNumber && _slots[i].Bitmap != null)
                    return _slots[i].Bitmap;
            }
            return null;
        }
    }

    /// <summary>
    /// Creates a thread-safe frozen BitmapSource on the background worker thread from the pooled BGRA buffer.
    /// </summary>
    public static BitmapSource CreateFrozenBitmapOffThread(PooledPageBuffer buffer)
    {
        var bmp = BitmapSource.Create(
            buffer.Width,
            buffer.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            buffer.Pixels,
            buffer.Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Registers a frozen BitmapSource into the bounded 12-slot ring buffer and evicts old non-protected pages.
    /// </summary>
    public BitmapSource RegisterSurfaceInRingBuffer(
        int pageNumber,
        int width,
        int height,
        bool isHighRes,
        BitmapSource frozenBitmap,
        Action? detachWhenEvicted,
        Func<int, bool>? isPageProtected = null)
    {
        Action? evictedCallback = null;

        lock (_sync)
        {
            SurfaceSlot? targetSlot = null;
            for (int i = 0; i < MaxSurfaces; i++)
            {
                if (_slots[i].PageNumber == pageNumber)
                {
                    targetSlot = _slots[i];
                    break;
                }
            }

            if (targetSlot == null)
            {
                for (int attempt = 0; attempt < MaxSurfaces; attempt++)
                {
                    int idx = (_ringCursor + attempt) % MaxSurfaces;
                    var candidate = _slots[idx];
                    if (candidate.PageNumber == 0 || isPageProtected == null || !isPageProtected(candidate.PageNumber))
                    {
                        targetSlot = candidate;
                        _ringCursor = (idx + 1) % MaxSurfaces;
                        break;
                    }
                }

                if (targetSlot == null)
                {
                    targetSlot = _slots[_ringCursor];
                    _ringCursor = (_ringCursor + 1) % MaxSurfaces;
                }

                evictedCallback = targetSlot.DetachCallback;
            }

            targetSlot.PageNumber = pageNumber;
            targetSlot.PixelWidth = width;
            targetSlot.PixelHeight = height;
            targetSlot.IsHighRes = isHighRes;
            targetSlot.Bitmap = frozenBitmap;
            targetSlot.DetachCallback = detachWhenEvicted;
        }

        evictedCallback?.Invoke();
        return frozenBitmap;
    }

    public void EvictPagesOutsideRange(int minPageInclusive, int maxPageInclusive)
    {
        var callbacks = new List<Action>();

        lock (_sync)
        {
            for (int i = 0; i < MaxSurfaces; i++)
            {
                var slot = _slots[i];
                if (slot.PageNumber > 0 && (slot.PageNumber < minPageInclusive || slot.PageNumber > maxPageInclusive))
                {
                    if (slot.DetachCallback != null)
                        callbacks.Add(slot.DetachCallback);

                    slot.DetachCallback = null;
                    slot.PageNumber = 0;
                    slot.IsHighRes = false;
                    slot.PixelWidth = 0;
                    slot.PixelHeight = 0;
                    slot.Bitmap = null;
                }
            }
        }

        foreach (var cb in callbacks)
        {
            cb.Invoke();
        }
    }

    public void ReleaseAllSurfaces()
    {
        var callbacks = new List<Action>();

        lock (_sync)
        {
            for (int i = 0; i < MaxSurfaces; i++)
            {
                var slot = _slots[i];
                if (slot.DetachCallback != null)
                    callbacks.Add(slot.DetachCallback);

                slot.DetachCallback = null;
                slot.PageNumber = 0;
                slot.IsHighRes = false;
                slot.PixelWidth = 0;
                slot.PixelHeight = 0;
                slot.Bitmap = null;
            }
            _ringCursor = 0;
        }

        foreach (var cb in callbacks)
        {
            cb.Invoke();
        }
    }
}
