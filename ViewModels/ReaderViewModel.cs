using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookHub.Core.Data;
using BookHub.Core.Models;
using BookHub.Core.Parsers;
using BookHub.Core.Pdf;
using BookHub.Core.Services;
using BookHub.Reader.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BookHub.Reader.ViewModels;

public sealed record ZoomPresetOption(string Key, string Label);

public partial class ContinuousPageViewModel : ObservableObject
{
    public int PageNumber { get; init; }
    [ObservableProperty] private BitmapSource? _pageBitmap;
    [ObservableProperty] private bool _isLowResPreview;
    [ObservableProperty] private string _pageText = string.Empty;

    public void AssignRenderedBitmap(BitmapSource? bitmap, bool isLowRes)
    {
        PageBitmap = bitmap;
        IsLowResPreview = isLowRes;
        OnPropertyChanged(nameof(PageBitmap));
    }
}

public partial class DocumentTabViewModel : ObservableObject, IDisposable
{
    private readonly ReaderSettingsService _readerSettings;
    private readonly long? _catalogBookId;
    private readonly string? _catalogDbPath;
    private readonly PdfSurfacePool _surfacePool = new();

    private PdfDocumentSession? _pdfSession;
    private List<string> _textPages = [];
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _progressiveHighResCts;
    private CancellationTokenSource? _resizeDebounceCts;
    private CancellationTokenSource? _progressSaveDebounceCts;
    private int _lastRenderedTargetPixelWidth;
    private int _lastVisibleFirstPage = 1;
    private int _lastVisibleLastPage = 1;
    private bool _disposed;
    private bool _documentInitialized;
    private Task? _initializationTask;
    private Task? _resumeTask;
    private bool _isBackgroundUnloaded;
    private bool _isResumingFromBackground;
    private int _lifecycleVersion;
    private int _renderGeneration;

    public event Action<int, double>? PriorityScrollRequested;

    public BookRecord Book { get; }
    public long? CatalogBookId => _catalogBookId;
    public string? CatalogDbPath => _catalogDbPath;
    public bool IsCatalogLinked => _catalogBookId is > 0 && !string.IsNullOrWhiteSpace(_catalogDbPath);

    public string TabTitle => Book.Title.Length > 28 ? Book.Title[..25] + "..." : Book.Title;

    public const string MissingTextLayerToolTip = "У цьому документі відсутній текстовий шар (лише зображення). Посимвольне виділення недоступне";
    public const string DefaultTextSelectionToolTip = "Режим виділення тексту";

    [ObservableProperty] private bool _isPdfMode;
    [ObservableProperty] private bool _isTextMode;
    [ObservableProperty] private bool _hasTextLayer = true;
    [ObservableProperty] private bool _isLoading = true;

    public string TextSelectionToolTip => HasTextLayer
        ? ReaderLocalizationService.GetString("Loc_SelectionTooltipEnabled")
        : ReaderLocalizationService.GetString("Loc_SelectionTooltipDisabled");

    public void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(TextSelectionToolTip));
        OnPropertyChanged(nameof(ZoomPercentDisplay));
    }

    partial void OnHasTextLayerChanged(bool value)
    {
        OnPropertyChanged(nameof(TextSelectionToolTip));
    }
    [ObservableProperty] private double _loadingProgressPercent;
    [ObservableProperty] private double _loadingOverlayBarWidth = 640;
    [ObservableProperty] private double _loadingFilledBarWidth;
    [ObservableProperty] private string _loadTimeText = string.Empty;

    [ObservableProperty] private ReaderViewMode _viewMode = ReaderViewMode.ContinuousVertical;
    public bool IsSinglePageMode => ViewMode == ReaderViewMode.SinglePage;
    public bool IsTwoPageSpreadMode => ViewMode == ReaderViewMode.TwoPageSpread;
    public bool IsContinuousVerticalMode => ViewMode == ReaderViewMode.ContinuousVertical;

    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private string _pageJumpInput = "1";
    public double SavedScrollOffset { get; set; }
    public double SavedPageRelativeOffset { get; set; }

    [ObservableProperty] private string _zoomMode = "FitWidth";
    [ObservableProperty] private double _zoomLevel = 1.0;
    [ObservableProperty] private double _textFontSize = 16.0;

    [ObservableProperty] private double _viewportWidth = 960;
    [ObservableProperty] private double _viewportHeight = 760;
    [ObservableProperty] private double _computedPageDisplayWidth = 900;
    [ObservableProperty] private double _computedPageDisplayHeight = double.NaN;
    [ObservableProperty] private double _computedSpreadPageDisplayWidth = 440;
    [ObservableProperty] private double _continuousPageItemHeight = 1120;
    [ObservableProperty] private ScrollBarVisibility _horizontalScrollVisibility = ScrollBarVisibility.Disabled;

    [ObservableProperty] private BitmapSource? _currentPageImage;
    [ObservableProperty] private BitmapSource? _secondSpreadPageImage;
    [ObservableProperty] private string _currentPageText = string.Empty;
    [ObservableProperty] private string _secondSpreadPageText = string.Empty;

    [ObservableProperty] private double _readingProgressPercent;
    [ObservableProperty] private string _memoryStatusText = "Пул поверхонь: 0 / 6";

    public ObservableCollection<TocItem> TableOfContents { get; } = [];
    public ObservableCollection<AnnotationRecord> Annotations { get; } = [];
    public ObservableCollection<ContinuousPageViewModel> ContinuousWindowPages { get; } = [];

    public string LoadingStatusCaption
    {
        get
        {
            double sizeMb = Math.Max(0.05, Book.FileSize / (1024.0 * 1024.0));
            string sizeStr = sizeMb >= 0.1 ? $"{sizeMb:0.0} МБ" : $"{sizeMb:0.00} МБ";
            int pct = Math.Clamp((int)Math.Round(LoadingProgressPercent), 0, 100);
            bool hasKnownAuthor = !string.IsNullOrWhiteSpace(Book.Author) &&
                                  !Book.Author.Equals("Невідомий автор", StringComparison.OrdinalIgnoreCase);

            return hasKnownAuthor
                ? $"Завантажується {pct}%... {Book.Author} — {Book.Title}, {TotalPages} стор., {sizeStr}"
                : $"Завантажується {pct}%... {Book.Title}, {TotalPages} стор., {sizeStr}";
        }
    }

    public string ZoomPercentDisplay => ZoomMode switch
    {
        "FitWidth" => "↔ За шириною",
        "FitPage" => "📄 Ціла стор.",
        _ => $"{Math.Round(ZoomLevel * 100)}%"
    };

    public bool IsDocumentInitialized => _documentInitialized;
    public bool IsBackgroundUnloaded => _isBackgroundUnloaded;
    public bool IsResumingFromBackground => _isResumingFromBackground;

    public DocumentTabViewModel(
        BookRecord book,
        ReaderSettingsService readerSettings,
        string initialZoomMode = "FitWidth",
        ReaderViewMode initialViewMode = ReaderViewMode.ContinuousVertical,
        long? catalogBookId = null,
        string? catalogDbPath = null)
    {
        Book = book;
        _readerSettings = readerSettings;
        _catalogBookId = catalogBookId ?? (book.Id > 0 ? book.Id : null);
        _catalogDbPath = catalogDbPath;
        _viewMode = initialViewMode;
        bool isPdf = string.Equals(book.Extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        _isPdfMode = isPdf;
        _isTextMode = !isPdf;
        CurrentPage = Math.Max(1, book.CurrentPage);
        TotalPages = Math.Max(1, book.PageCount);
        _lastVisibleFirstPage = CurrentPage;
        _lastVisibleLastPage = CurrentPage + 1;
        PageJumpInput = CurrentPage.ToString();
        _zoomMode = string.IsNullOrWhiteSpace(initialZoomMode) ? "FitWidth" : initialZoomMode;
        RecalculatePageLayout();
        SavedScrollOffset = Math.Max(0, (CurrentPage - 1) * ContinuousPageItemHeight);
        SetLoadingProgress(5);
    }

    private void SetLoadingProgress(double percent)
    {
        LoadingProgressPercent = Math.Clamp(percent, 0, 100);
        LoadingFilledBarWidth = Math.Round(LoadingOverlayBarWidth * (LoadingProgressPercent / 100.0), 1);
        OnPropertyChanged(nameof(LoadingStatusCaption));
    }

    public void ApplyZoomMode(string modeKey)
    {
        if (string.IsNullOrWhiteSpace(modeKey))
            modeKey = "FitWidth";

        ZoomMode = modeKey;
        if (modeKey.EndsWith('%') && double.TryParse(modeKey.TrimEnd('%'), out double pct))
        {
            ZoomLevel = Math.Clamp(pct / 100.0, 0.5, 3.0);
        }

        RecalculatePageLayout();
        OnPropertyChanged(nameof(ZoomPercentDisplay));
        if (!_isBackgroundUnloaded && _documentInitialized)
        {
            _ = ClearCacheAndReloadThemeAsync();
        }
    }

    public void UpdateViewportSize(double width, double height)
    {
        if (width <= 80 || height <= 80) return;

        ViewportWidth = width;
        ViewportHeight = height;
        RecalculatePageLayout();
        OnPropertyChanged(nameof(ZoomPercentDisplay));

        if (!_isBackgroundUnloaded && _documentInitialized && !IsLoading && IsPdfMode && _pdfSession != null && (ZoomMode == "FitWidth" || ZoomMode == "FitPage"))
        {
            int desiredPx = CalculateTargetRenderPixelWidth();
            if (_lastRenderedTargetPixelWidth > 0 && Math.Abs(desiredPx - _lastRenderedTargetPixelWidth) >= 160)
            {
                _resizeDebounceCts?.Cancel();
                _resizeDebounceCts?.Dispose();
                _resizeDebounceCts = new CancellationTokenSource();
                var token = _resizeDebounceCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(180, token);
                        if (!token.IsCancellationRequested)
                        {
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                _ = RefreshActiveViewAsync();
                            });
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);
            }
        }
    }

    public void RecalculatePageLayout()
    {
        double availW = Math.Max(240, ViewportWidth - 48);
        double availH = Math.Max(240, ViewportHeight - 40);

        // Progress overlay width = 70% of current content viewport width
        LoadingOverlayBarWidth = Math.Max(240, Math.Round(ViewportWidth * 0.70));
        LoadingFilledBarWidth = Math.Round(LoadingOverlayBarWidth * (LoadingProgressPercent / 100.0), 1);

        if (ZoomMode.Equals("FitWidth", StringComparison.OrdinalIgnoreCase))
        {
            HorizontalScrollVisibility = ScrollBarVisibility.Disabled;
            ComputedPageDisplayWidth = availW;
            ComputedPageDisplayHeight = double.NaN;
            ComputedSpreadPageDisplayWidth = Math.Max(180, (availW - 16) / 2.0);
            ZoomLevel = Math.Clamp(availW / 820.0, 0.5, 2.5);
            TextFontSize = Math.Clamp(Math.Round(15.0 + (availW - 600) / 220.0, 1), 13.5, 19.5);
        }
        else if (ZoomMode.Equals("FitPage", StringComparison.OrdinalIgnoreCase))
        {
            HorizontalScrollVisibility = ScrollBarVisibility.Disabled;
            double pageAspect = 612.0 / 792.0;
            if (IsPdfMode && _pdfSession != null)
            {
                var (ptW, ptH) = _pdfSession.GetPageSizePoints(CurrentPage);
                if (ptW > 0 && ptH > 0) pageAspect = ptW / ptH;
            }

            double maxWByHeight = availH * pageAspect;
            ComputedPageDisplayWidth = Math.Min(availW, Math.Max(220, maxWByHeight));
            ComputedPageDisplayHeight = availH;
            ComputedSpreadPageDisplayWidth = Math.Min(Math.Max(180, (availW - 16) / 2.0), Math.Max(180, maxWByHeight));
            ZoomLevel = Math.Clamp(ComputedPageDisplayWidth / 820.0, 0.5, 2.5);
            TextFontSize = 15.0;
        }
        else
        {
            if (ZoomMode.EndsWith('%') && double.TryParse(ZoomMode.TrimEnd('%'), out double pct))
            {
                ZoomLevel = Math.Clamp(pct / 100.0, 0.5, 3.0);
            }

            double targetW = Math.Round(820.0 * ZoomLevel);
            ComputedPageDisplayWidth = targetW;
            ComputedPageDisplayHeight = double.NaN;
            ComputedSpreadPageDisplayWidth = Math.Round(520.0 * ZoomLevel);
            HorizontalScrollVisibility = targetW > availW ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            TextFontSize = Math.Clamp(Math.Round(16.0 * ZoomLevel, 1), 11.0, 32.0);
        }

        ContinuousPageItemHeight = Math.Max(360, ComputedPageDisplayWidth * (792.0 / 612.0) + 34);
    }

    private int CalculateTargetRenderPixelWidth() =>
        Math.Clamp((int)Math.Round(ComputedPageDisplayWidth * 1.30), 700, 2200);

    private int CalculateLowResPreviewPixelWidth() =>
        Math.Clamp((int)Math.Round(ComputedPageDisplayWidth * 0.42), 280, 460);

    public Task InitializeDocumentAsync()
    {
        if (_documentInitialized)
        {
            return _isBackgroundUnloaded ? ResumeFromBackgroundAsync() : Task.CompletedTask;
        }

        return _initializationTask ??= InitializeDocumentCoreAsync();
    }

    private async Task InitializeDocumentCoreAsync()
    {
        var sw = Stopwatch.StartNew();
        int lifecycleVersion = Volatile.Read(ref _lifecycleVersion);
        IsLoading = true;
        _isBackgroundUnloaded = false;
        SetLoadingProgress(12);

        using var progressTickerCts = new CancellationTokenSource();
        var tickerToken = progressTickerCts.Token;
        _ = Task.Run(async () =>
        {
            while (!tickerToken.IsCancellationRequested)
            {
                await Task.Delay(35, tickerToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (tickerToken.IsCancellationRequested) break;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (IsLoading && LoadingProgressPercent < 90)
                    {
                        SetLoadingProgress(LoadingProgressPercent + 2.5);
                    }
                });
            }
        }, tickerToken);

        try
        {
            if (!BookFormatParser.IsReaderExtensionSupported(Book.FilePath, out string rawExt))
            {
                throw new InvalidDataException(BookFormatParser.BuildUnsupportedFormatMessage(rawExt));
            }

            if (!BookFormatParser.ValidateReaderFileSignature(Book.FilePath, out string signatureError))
            {
                throw new InvalidDataException(signatureError);
            }

            await LoadAnnotationsAsync(lifecycleVersion);
            if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
                return;

            SetLoadingProgress(28);

            string ext = Book.Extension.ToLowerInvariant();
            if (ext == ".pdf")
            {
                IsPdfMode = true;
                IsTextMode = false;

                var session = await Task.Run(() => new PdfDocumentSession(Book.FilePath));
                if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
                {
                    session.Dispose();
                    return;
                }

                _pdfSession = session;
                _documentInitialized = true;
                TotalPages = Math.Max(1, session.PageCount);
                Book.PageCount = TotalPages;
                if (!string.IsNullOrWhiteSpace(session.Title) && session.Title.Length > 2)
                {
                    Book.Title = session.Title;
                    OnPropertyChanged(nameof(TabTitle));
                }
                if (!string.IsNullOrWhiteSpace(session.Author))
                {
                    Book.Author = session.Author;
                }

                SetLoadingProgress(55);

                int initialTargetPage = Math.Clamp(Book.CurrentPage, 1, TotalPages);
                var (toc, hasTextLayer) = await Task.Run(() =>
                {
                    var outline = session.GetTableOfContents();
                    bool hasText = session.HasRecognizedTextLayer(initialTargetPage);
                    return (outline, hasText);
                });
                if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
                    return;

                HasTextLayer = hasTextLayer;
                CurrentPage = initialTargetPage;
                _lastVisibleFirstPage = CurrentPage;
                _lastVisibleLastPage = Math.Min(TotalPages, CurrentPage + 1);
                PageJumpInput = CurrentPage.ToString();

                TableOfContents.Clear();
                foreach (var item in toc) TableOfContents.Add(item);

                RecalculatePageLayout();
                EnsureContinuousPageItems();
                SavedScrollOffset = Math.Max(0, (CurrentPage - 1) * ContinuousPageItemHeight);
                SetLoadingProgress(78);

                // Render current page and immediate neighbors before hiding loading overlay
                await RenderSinglePriorityPageAsync(CurrentPage);
                SetLoadingProgress(100);
                IsLoading = false;

                sw.Stop();
                LoadTimeText = $"Відкрито за {sw.Elapsed.TotalMilliseconds:0} мс • PDFium R2R + Пул {PdfSurfacePool.MaxSurfaces} поверхонь";

                // Deterministically position viewport on CurrentPage (e.g. saved page 68 of 624)
                PriorityScrollRequested?.Invoke(CurrentPage, -1.0);
            }
            else
            {
                IsPdfMode = false;
                IsTextMode = true;

                SetLoadingProgress(45);
                var (pages, toc) = await Task.Run(() => BookFormatParser.LoadDocumentPaginatedContent(Book.FilePath));
                if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
                    return;

                _textPages = pages;
                _documentInitialized = true;
                bool isRasterContainer = ext is ".cbz" or ".cbr" or ".djvu";
                HasTextLayer = !isRasterContainer &&
                               _textPages.Any(p => !string.IsNullOrWhiteSpace(p) &&
                                                   p.Any(c => !char.IsWhiteSpace(c) && !char.IsControl(c)));
                TotalPages = Math.Max(1, _textPages.Count);
                Book.PageCount = TotalPages;
                CurrentPage = Math.Clamp(Book.CurrentPage, 1, TotalPages);
                _lastVisibleFirstPage = CurrentPage;
                _lastVisibleLastPage = Math.Min(TotalPages, CurrentPage + 1);
                PageJumpInput = CurrentPage.ToString();

                TableOfContents.Clear();
                foreach (var item in toc) TableOfContents.Add(item);

                SetLoadingProgress(82);
                RecalculatePageLayout();
                EnsureContinuousPageItems();
                SavedScrollOffset = Math.Max(0, (CurrentPage - 1) * ContinuousPageItemHeight);

                await RefreshActiveViewAsync();
                SetLoadingProgress(100);
                IsLoading = false;

                sw.Stop();
                LoadTimeText = $"Відкрито за {sw.Elapsed.TotalMilliseconds:0} мс • Потоковий {Book.FormatBadge}";

                PriorityScrollRequested?.Invoke(CurrentPage, -1.0);
            }

            UpdateProgressMetrics(scheduleBackgroundSave: true);
        }
        catch (Exception ex)
        {
            _documentInitialized = false;
            IsPdfMode = false;
            IsTextMode = true;
            HasTextLayer = false;
            CurrentPageText = $"Помилка під час відкриття документа:\n{ex.Message}";
            LoadTimeText = "Помилка відкриття";
        }
        finally
        {
            _initializationTask = null;
            progressTickerCts.Cancel();
            IsLoading = false;
        }
    }

    private async Task LoadAnnotationsAsync(int lifecycleVersion)
    {
        Annotations.Clear();
        if (IsCatalogLinked && File.Exists(_catalogDbPath))
        {
            try
            {
                var db = new LibraryDatabase(_catalogDbPath);
                var dbAnns = await db.GetAnnotationsForBookAsync(_catalogBookId!.Value);
                if (lifecycleVersion != Volatile.Read(ref _lifecycleVersion))
                    return;
                foreach (var a in dbAnns) Annotations.Add(a);
                return;
            }
            catch
            {
                // Fallback to reader_settings.json
            }
        }

        var recent = _readerSettings.GetRecentEntry(Book.FilePath);
        if (recent?.LocalAnnotations != null)
        {
            foreach (var a in recent.LocalAnnotations)
                Annotations.Add(a);
        }
    }

    partial void OnViewModeChanged(ReaderViewMode value)
    {
        OnPropertyChanged(nameof(IsSinglePageMode));
        OnPropertyChanged(nameof(IsTwoPageSpreadMode));
        OnPropertyChanged(nameof(IsContinuousVerticalMode));
        RecalculatePageLayout();
        SavedPageRelativeOffset = 0;
        if (value == ReaderViewMode.ContinuousVertical)
        {
            EnsureContinuousPageItems();
            SavedScrollOffset = Math.Max(0, (CurrentPage - 1) * ContinuousPageItemHeight);
        }
        else
        {
            SavedScrollOffset = 0;
        }
        _ = RefreshActiveViewAsync();
        PriorityScrollRequested?.Invoke(CurrentPage, SavedScrollOffset);
    }

    public void EnsureContinuousPageItems()
    {
        if (TotalPages <= 0) return;
        if (ContinuousWindowPages.Count == TotalPages)
        {
            if (IsTextMode && _textPages.Count > 0)
            {
                for (int i = 0; i < ContinuousWindowPages.Count && i < _textPages.Count; i++)
                {
                    if (string.IsNullOrEmpty(ContinuousWindowPages[i].PageText) && !string.IsNullOrEmpty(_textPages[i]))
                    {
                        ContinuousWindowPages[i].PageText = _textPages[i];
                    }
                }
            }
            return;
        }

        ContinuousWindowPages.Clear();
        for (int page = 1; page <= TotalPages; page++)
        {
            ContinuousWindowPages.Add(new ContinuousPageViewModel
            {
                PageNumber = page,
                PageText = IsTextMode && page <= _textPages.Count ? _textPages[page - 1] : string.Empty
            });
        }
    }

    private void CancelPendingRenders()
    {
        Interlocked.Increment(ref _renderGeneration);
        _progressiveHighResCts?.Cancel();
        _progressiveHighResCts?.Dispose();
        _progressiveHighResCts = null;

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
    }

    /// <summary>
    /// Priority navigation for TOC, Bookmark, Slider, and PageJump clicks:
    /// 1) Immediately cancels all pending background page renders.
    /// 2) Instantly updates CurrentPage and triggers deterministic UI ScrollIntoView.
    /// 3) Renders the exact target page and its immediate neighbors at high priority.
    /// </summary>
    public async Task NavigateToPagePriorityAsync(int targetPage, double? customVerticalOffset = null)
    {
        int clamped = Math.Clamp(targetPage, 1, Math.Max(1, TotalPages));
        CancelPendingRenders();

        CurrentPage = clamped;
        _lastVisibleFirstPage = clamped;
        _lastVisibleLastPage = Math.Min(TotalPages, clamped + 1);
        UpdateProgressMetrics(scheduleBackgroundSave: true);

        if (ZoomMode == "FitPage")
        {
            RecalculatePageLayout();
        }

        if (IsContinuousVerticalMode)
        {
            EnsureContinuousPageItems();
            SavedPageRelativeOffset = 0;
            SavedScrollOffset = customVerticalOffset is > 0
                ? customVerticalOffset.Value
                : Math.Max(0, (clamped - 1) * ContinuousPageItemHeight);
        }
        else
        {
            SavedPageRelativeOffset = 0;
            SavedScrollOffset = customVerticalOffset ?? 0;
        }

        // Notify UI window immediately (pass -1 when no explicit bookmark offset is requested)
        PriorityScrollRequested?.Invoke(clamped, customVerticalOffset ?? -1.0);

        if (IsPdfMode && _pdfSession != null)
        {
            await RenderSinglePriorityPageAsync(clamped);
            _ = RenderContinuousOrPagedNeighborsAsync(clamped);
        }
        else
        {
            UpdateTextPagesForCurrentMode();
        }
    }

    private async Task RenderSinglePriorityPageAsync(int pageNumber)
    {
        if (!IsPdfMode || _pdfSession == null || _isBackgroundUnloaded)
            return;

        EnsureContinuousPageItems();

        int gen = Interlocked.Increment(ref _renderGeneration);
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        int highResWidth = CalculateTargetRenderPixelWidth();
        _lastRenderedTargetPixelWidth = highResWidth;
        uint bgArgb = ReaderThemeService.CurrentTheme.PdfCanvasArgb;

        int bufferFirst = Math.Max(1, pageNumber - 2);
        int bufferLast = Math.Min(TotalPages, pageNumber + 3);
        _surfacePool.EvictPagesOutsideRange(bufferFirst, bufferLast);

        // 1. Render exact target page first
        var surface = await RenderPageIntoPoolAsync(
            pageNumber,
            highResWidth,
            bgArgb,
            lowResPreview: false,
            bufferFirst,
            bufferLast,
            ct,
            expectedGeneration: gen);

        if (surface != null && !IsContinuousVerticalMode)
        {
            CurrentPageImage = surface;
        }

        // 2. Immediately render next page (and previous page) if in continuous or spread mode
        if (IsContinuousVerticalMode || IsTwoPageSpreadMode)
        {
            if (pageNumber + 1 <= TotalPages && !ct.IsCancellationRequested && gen == Volatile.Read(ref _renderGeneration))
            {
                var nextSurface = await RenderPageIntoPoolAsync(
                    pageNumber + 1,
                    highResWidth,
                    bgArgb,
                    lowResPreview: false,
                    bufferFirst,
                    bufferLast,
                    ct,
                    expectedGeneration: gen);

                if (IsTwoPageSpreadMode)
                {
                    SecondSpreadPageImage = nextSurface;
                }
            }

            if (IsContinuousVerticalMode && pageNumber - 1 >= 1 && !ct.IsCancellationRequested && gen == Volatile.Read(ref _renderGeneration))
            {
                await RenderPageIntoPoolAsync(
                    pageNumber - 1,
                    highResWidth,
                    bgArgb,
                    lowResPreview: false,
                    bufferFirst,
                    bufferLast,
                    ct,
                    expectedGeneration: gen);
            }
        }

        MemoryStatusText = $"🎯 Пріоритетна стор. {pageNumber} / {TotalPages} | Пул: {_surfacePool.ActiveSurfaceCount}/{PdfSurfacePool.MaxSurfaces}";
    }

    private async Task RenderContinuousOrPagedNeighborsAsync(int centerPage)
    {
        if (!IsPdfMode || _pdfSession == null || _isBackgroundUnloaded)
            return;

        if (IsContinuousVerticalMode)
        {
            int bufferFirst = Math.Max(1, centerPage - 2);
            int bufferLast = Math.Min(TotalPages, centerPage + 3);
            ScheduleHighResUpgradeAfterScrollIdle(bufferFirst, bufferLast);
        }
        else
        {
            await RenderPdfPagesForCurrentModeAsync();
        }
    }

    public async Task RenderContinuousPagesAsync(int firstVisiblePage, int lastVisiblePage, bool isActiveScrollEvent = false, int? dominantPage = null)
    {
        if (!IsContinuousVerticalMode || TotalPages <= 0 || _isBackgroundUnloaded)
            return;

        EnsureContinuousPageItems();

        int first = Math.Clamp(firstVisiblePage, 1, TotalPages);
        int last = Math.Clamp(lastVisiblePage, first, Math.Min(TotalPages, first + 4));
        _lastVisibleFirstPage = first;
        _lastVisibleLastPage = last;

        int current = dominantPage.HasValue
            ? Math.Clamp(dominantPage.Value, first, last)
            : first;

        if (!_isResumingFromBackground && !IsLoading && CurrentPage != current)
        {
            CurrentPage = current;
            UpdateProgressMetrics(scheduleBackgroundSave: true);
        }

        if (!IsPdfMode || _pdfSession == null)
        {
            if (current <= _textPages.Count)
                CurrentPageText = _textPages[current - 1];
            MemoryStatusText = $"Потокова сторінка {first}–{last} / {TotalPages}";
            return;
        }

        int bufferFirst = Math.Max(1, first - 2);
        int bufferLast = Math.Min(TotalPages, Math.Min(last + 3, bufferFirst + PdfSurfacePool.MaxSurfaces - 1));

        _surfacePool.EvictPagesOutsideRange(bufferFirst, bufferLast);

        if (isActiveScrollEvent)
        {
            int gen = Interlocked.Increment(ref _renderGeneration);
            _renderCts?.Cancel();
            _renderCts?.Dispose();
            _renderCts = new CancellationTokenSource();
            var lowResCt = _renderCts.Token;

            int lowResWidth = CalculateLowResPreviewPixelWidth();
            uint bgArgb = ReaderThemeService.CurrentTheme.PdfCanvasArgb;

            for (int page = first; page <= last; page++)
            {
                if (lowResCt.IsCancellationRequested || _isBackgroundUnloaded || gen != Volatile.Read(ref _renderGeneration))
                    return;

                if (_surfacePool.HasSatisfiedSurface(page, requireHighRes: false, lowResWidth))
                {
                    var existing = _surfacePool.TryGetExistingSurface(page);
                    if (existing != null && ContinuousWindowPages[page - 1].PageBitmap != existing)
                    {
                        ContinuousWindowPages[page - 1].AssignRenderedBitmap(existing, ContinuousWindowPages[page - 1].IsLowResPreview);
                    }
                    continue;
                }

                await RenderPageIntoPoolAsync(page, lowResWidth, bgArgb, lowResPreview: true, bufferFirst, bufferLast, lowResCt, gen);
            }

            MemoryStatusText = $"⚡ Low-Res скрол: стор. {first}–{last} / {TotalPages} | Пул: {_surfacePool.ActiveSurfaceCount}/{PdfSurfacePool.MaxSurfaces}";
            ScheduleHighResUpgradeAfterScrollIdle(bufferFirst, bufferLast);
        }
        else
        {
            await RenderHighResWindowAsync(bufferFirst, bufferLast);
        }
    }

    private void ScheduleHighResUpgradeAfterScrollIdle(int bufferFirst, int bufferLast)
    {
        _progressiveHighResCts?.Cancel();
        _progressiveHighResCts?.Dispose();
        _progressiveHighResCts = new CancellationTokenSource();
        var ct = _progressiveHighResCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140, ct);
                if (ct.IsCancellationRequested || _isBackgroundUnloaded)
                    return;

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    await dispatcher.InvokeAsync(async () =>
                    {
                        if (!ct.IsCancellationRequested && !_isBackgroundUnloaded)
                        {
                            await RenderHighResWindowAsync(bufferFirst, bufferLast);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);
    }

    private async Task RenderHighResWindowAsync(int bufferFirst, int bufferLast)
    {
        if (!IsPdfMode || _pdfSession == null || _isBackgroundUnloaded)
            return;

        EnsureContinuousPageItems();

        int gen = Interlocked.Increment(ref _renderGeneration);
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        int highResWidth = CalculateTargetRenderPixelWidth();
        _lastRenderedTargetPixelWidth = highResWidth;
        uint bgArgb = ReaderThemeService.CurrentTheme.PdfCanvasArgb;

        var orderedPages = new List<int>();
        for (int p = _lastVisibleFirstPage; p <= _lastVisibleLastPage; p++)
        {
            if (p >= 1 && p <= TotalPages)
                orderedPages.Add(p);
        }
        for (int p = bufferFirst; p <= bufferLast; p++)
        {
            if (p >= 1 && p <= TotalPages && !orderedPages.Contains(p))
                orderedPages.Add(p);
        }

        foreach (int page in orderedPages.Take(PdfSurfacePool.MaxSurfaces))
        {
            if (ct.IsCancellationRequested || _isBackgroundUnloaded || gen != Volatile.Read(ref _renderGeneration))
                return;

            if (_surfacePool.HasSatisfiedSurface(page, requireHighRes: true, highResWidth))
            {
                var existing = _surfacePool.TryGetExistingSurface(page);
                if (existing != null)
                {
                    var pageVm = ContinuousWindowPages[page - 1];
                    if (pageVm.PageBitmap != existing || pageVm.IsLowResPreview)
                    {
                        pageVm.AssignRenderedBitmap(existing, isLowRes: false);
                    }
                }
                continue;
            }

            await RenderPageIntoPoolAsync(page, highResWidth, bgArgb, lowResPreview: false, bufferFirst, bufferLast, ct, gen);
        }

        MemoryStatusText = $"🎯 High-Res: стор. {bufferFirst}–{bufferLast} / {TotalPages} | Пул: {_surfacePool.ActiveSurfaceCount}/{PdfSurfacePool.MaxSurfaces}";
    }

    private async Task<BitmapSource?> RenderPageIntoPoolAsync(
        int pageNumber,
        int targetWidthPx,
        uint bgArgb,
        bool lowResPreview,
        int protectedMinPage,
        int protectedMaxPage,
        CancellationToken ct,
        int expectedGeneration)
    {
        if (_pdfSession == null || pageNumber < 1 || pageNumber > TotalPages || _isBackgroundUnloaded)
            return null;

        try
        {
            var rendered = await Task.Run<(BitmapSource? Bitmap, int Width, int Height)>(() =>
            {
                if (ct.IsCancellationRequested || expectedGeneration != Volatile.Read(ref _renderGeneration))
                    return (null, 0, 0);

                using var pooledBuf = _pdfSession.RenderPagePooledBgra(pageNumber, ZoomLevel, targetWidthPx, bgArgb, lowResPreview);
                if (ct.IsCancellationRequested || expectedGeneration != Volatile.Read(ref _renderGeneration))
                    return (null, 0, 0);

                var frozen = PdfSurfacePool.CreateFrozenBitmapOffThread(pooledBuf);
                return (frozen, pooledBuf.Width, pooledBuf.Height);
            }, ct);

            if (rendered.Bitmap == null || ct.IsCancellationRequested || _isBackgroundUnloaded || expectedGeneration != Volatile.Read(ref _renderGeneration))
                return null;

            ContinuousPageViewModel? pageVm = pageNumber <= ContinuousWindowPages.Count
                ? ContinuousWindowPages[pageNumber - 1]
                : null;

            var surface = _surfacePool.RegisterSurfaceInRingBuffer(
                pageNumber,
                rendered.Width,
                rendered.Height,
                isHighRes: !lowResPreview,
                frozenBitmap: rendered.Bitmap,
                detachWhenEvicted: () =>
                {
                    if (pageVm != null)
                    {
                        pageVm.AssignRenderedBitmap(null, isLowRes: false);
                    }
                },
                isPageProtected: p => p >= protectedMinPage && p <= protectedMaxPage);

            if (pageVm != null)
            {
                pageVm.AssignRenderedBitmap(surface, lowResPreview);
            }

            return surface;
        }
        catch
        {
            return null;
        }
    }

    public void UnloadBackgroundTabBitmaps()
    {
        _isBackgroundUnloaded = true;
        CancelPendingRenders();

        _surfacePool.ReleaseAllSurfaces();
        CurrentPageImage = null;
        SecondSpreadPageImage = null;

        for (int i = 0; i < ContinuousWindowPages.Count; i++)
        {
            ContinuousWindowPages[i].PageBitmap = null;
        }

        MemoryStatusText = $"Фонова вкладка (стор. {CurrentPage}/{TotalPages}): растри вивантажено з RAM";
    }

    public void SyncToolbarAndSidebarState()
    {
        PageJumpInput = CurrentPage.ToString();
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageJumpInput));
        OnPropertyChanged(nameof(IsPdfMode));
        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(IsSinglePageMode));
        OnPropertyChanged(nameof(IsTwoPageSpreadMode));
        OnPropertyChanged(nameof(IsContinuousVerticalMode));
        OnPropertyChanged(nameof(TableOfContents));
        OnPropertyChanged(nameof(Annotations));
        OnPropertyChanged(nameof(ContinuousWindowPages));
    }

    public Task ResumeFromBackgroundAsync()
    {
        if (!_documentInitialized)
        {
            _isBackgroundUnloaded = false;
            return InitializeDocumentAsync();
        }

        return _resumeTask ??= ResumeFromBackgroundCoreAsync();
    }

    private async Task ResumeFromBackgroundCoreAsync()
    {
        int savedPage = Math.Clamp(CurrentPage, 1, Math.Max(1, TotalPages));
        double savedOffset = SavedScrollOffset;
        double savedRelativeOffset = SavedPageRelativeOffset;

        _isBackgroundUnloaded = false;
        _isResumingFromBackground = true;

        try
        {
            CurrentPage = savedPage;
            SavedScrollOffset = savedOffset;
            SavedPageRelativeOffset = savedRelativeOffset;
            _lastVisibleFirstPage = savedPage;
            _lastVisibleLastPage = Math.Min(TotalPages, savedPage + 1);

            RecalculatePageLayout();
            EnsureContinuousPageItems();
            SyncToolbarAndSidebarState();

            PriorityScrollRequested?.Invoke(savedPage, savedOffset);

            if (IsPdfMode && _pdfSession != null)
            {
                await RenderSinglePriorityPageAsync(savedPage);
                CurrentPage = savedPage;
                SavedScrollOffset = savedOffset;
                SavedPageRelativeOffset = savedRelativeOffset;
                await RefreshActiveViewAsync();
            }
            else
            {
                UpdateTextPagesForCurrentMode();
            }

            CurrentPage = savedPage;
            SavedScrollOffset = savedOffset;
            SavedPageRelativeOffset = savedRelativeOffset;
            PageJumpInput = savedPage.ToString();
            PriorityScrollRequested?.Invoke(savedPage, savedOffset);
        }
        finally
        {
            _isResumingFromBackground = false;
            _resumeTask = null;
        }
    }

    public async Task RefreshActiveViewAsync()
    {
        if (_isBackgroundUnloaded)
            return;

        if (IsPdfMode)
        {
            await RenderPdfPagesForCurrentModeAsync();
        }
        else
        {
            UpdateTextPagesForCurrentMode();
        }
    }

    private void UpdateTextPagesForCurrentMode()
    {
        if (_textPages.Count == 0)
        {
            CurrentPageText = "Документ порожній.";
            SecondSpreadPageText = string.Empty;
            return;
        }

        int idx1 = Math.Clamp(CurrentPage - 1, 0, _textPages.Count - 1);
        CurrentPageText = _textPages[idx1];

        int idx2 = idx1 + 1;
        SecondSpreadPageText = idx2 < _textPages.Count ? _textPages[idx2] : "— Кінець документа —";

        EnsureContinuousPageItems();
        MemoryStatusText = $"Потокова сторінка {CurrentPage} / {TotalPages}";
    }

    private async Task RenderPdfPagesForCurrentModeAsync()
    {
        if (_pdfSession == null || _isBackgroundUnloaded) return;

        if (IsContinuousVerticalMode)
        {
            EnsureContinuousPageItems();
            int first = Math.Clamp(CurrentPage, 1, TotalPages);
            int last = Math.Clamp(first + 1, first, TotalPages);
            await RenderContinuousPagesAsync(first, last, isActiveScrollEvent: false);
            return;
        }

        int gen = Interlocked.Increment(ref _renderGeneration);
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        int targetPage = CurrentPage;
        int targetWidthPx = CalculateTargetRenderPixelWidth();
        _lastRenderedTargetPixelWidth = targetWidthPx;
        uint bgArgb = ReaderThemeService.CurrentTheme.PdfCanvasArgb;

        CurrentPageImage = await RenderPageIntoPoolAsync(
            targetPage,
            targetWidthPx,
            bgArgb,
            lowResPreview: false,
            targetPage - 1,
            targetPage + 2,
            ct,
            gen);

        if (ct.IsCancellationRequested || gen != Volatile.Read(ref _renderGeneration))
            return;

        if (IsTwoPageSpreadMode)
        {
            if (targetPage + 1 <= TotalPages)
            {
                SecondSpreadPageImage = await RenderPageIntoPoolAsync(
                    targetPage + 1,
                    (int)(targetWidthPx * 0.75),
                    bgArgb,
                    lowResPreview: false,
                    targetPage,
                    targetPage + 2,
                    ct,
                    gen);
            }
            else
            {
                SecondSpreadPageImage = null;
            }
        }

        MemoryStatusText = $"Пул у RAM: [{_surfacePool.GetAssignedPagesSummary()}] ({_surfacePool.ActiveSurfaceCount}/{PdfSurfacePool.MaxSurfaces})";
    }

    public string GetCurrentPageExtractText(int? specificPage = null)
    {
        int p = specificPage ?? CurrentPage;
        if (IsPdfMode && _pdfSession != null)
        {
            return _pdfSession.ExtractPageText(p);
        }
        if (_textPages.Count > 0)
        {
            int idx = Math.Clamp(p - 1, 0, _textPages.Count - 1);
            return _textPages[idx];
        }
        return CurrentPageText;
    }

    public PdfTextSelectionResult PreviewPdfTextSelection(
        int pageNumber,
        double startNormX,
        double startNormY,
        double endNormX,
        double endNormY)
    {
        if (IsPdfMode && _pdfSession != null)
        {
            return _pdfSession.SelectTextAndHighlightRects(pageNumber, startNormX, startNormY, endNormX, endNormY);
        }
        return PdfTextSelectionResult.Empty;
    }

    public string ExtractPdfTextByNormalizedRect(int pageNumber, double normX1, double normY1, double normX2, double normY2)
    {
        if (IsPdfMode && _pdfSession != null)
        {
            var vectorResult = _pdfSession.SelectTextAndHighlightRects(pageNumber, normX1, normY1, normX2, normY2);
            if (!string.IsNullOrWhiteSpace(vectorResult.SelectedText))
                return vectorResult.SelectedText;

            return _pdfSession.ExtractTextInNormalizedRect(pageNumber, normX1, normY1, normX2, normY2);
        }
        return GetCurrentPageExtractText(pageNumber);
    }

    public async Task<List<DocumentSearchMatch>> SearchAllMatchesAsync(string query, CancellationToken ct)
    {
        if (!HasTextLayer || string.IsNullOrWhiteSpace(query))
            return [];

        string needle = query.Trim();
        if (needle.Length == 0)
            return [];

        if (IsPdfMode && _pdfSession != null)
        {
            var session = _pdfSession;
            int total = TotalPages;
            return await Task.Run(() =>
            {
                var matches = new List<DocumentSearchMatch>();
                for (int p = 1; p <= total; p++)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var pageMatches = session.SearchPageMatches(p, needle);
                    if (pageMatches.Count > 0)
                    {
                        matches.AddRange(pageMatches);
                    }
                }
                return matches;
            }, ct);
        }

        if (_textPages.Count > 0)
        {
            var pagesSnapshot = _textPages;
            return await Task.Run(() =>
            {
                var matches = new List<DocumentSearchMatch>();
                for (int p = 1; p <= pagesSnapshot.Count; p++)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    string pageText = pagesSnapshot[p - 1];
                    if (string.IsNullOrEmpty(pageText))
                        continue;

                    int pos = 0;
                    while (pos <= pageText.Length - needle.Length)
                    {
                        int idx = pageText.IndexOf(needle, pos, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0)
                            break;

                        double normY = Math.Clamp((double)idx / Math.Max(1, pageText.Length), 0.0, 1.0);
                        matches.Add(new DocumentSearchMatch(p, idx, needle.Length, [], normY));
                        pos = idx + Math.Max(1, needle.Length);
                    }
                }
                return matches;
            }, ct);
        }

        return [];
    }

    private void UpdateProgressMetrics(bool scheduleBackgroundSave)
    {
        PageJumpInput = CurrentPage.ToString();
        ReadingProgressPercent = TotalPages > 0
            ? Math.Clamp(Math.Round((double)CurrentPage / TotalPages * 100.0, 1), 0, 100)
            : 0;
        Book.CurrentPage = CurrentPage;
        Book.PageCount = TotalPages;
        Book.ReadingProgress = ReadingProgressPercent;

        if (scheduleBackgroundSave)
        {
            ScheduleDebouncedProgressSave();
        }
    }

    private void ScheduleDebouncedProgressSave()
    {
        _progressSaveDebounceCts?.Cancel();
        _progressSaveDebounceCts?.Dispose();
        _progressSaveDebounceCts = new CancellationTokenSource();
        var token = _progressSaveDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                if (!token.IsCancellationRequested)
                {
                    await SaveProgressAsync();
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public Task GoToPageAsync(int pageNumber) =>
        NavigateToPagePriorityAsync(pageNumber);

    public async Task ClearCacheAndReloadThemeAsync()
    {
        _surfacePool.ReleaseAllSurfaces();
        await RefreshActiveViewAsync();
    }

    public async Task SaveProgressAsync()
    {
        if (!BookFormatParser.IsReaderExtensionSupported(Book.FilePath, out _))
            return;

        UpdateProgressMetrics(scheduleBackgroundSave: false);

        _readerSettings.UpsertRecentFile(Book, Annotations);

        if (IsCatalogLinked && File.Exists(_catalogDbPath))
        {
            try
            {
                var db = new LibraryDatabase(_catalogDbPath);
                await db.UpdateReadingProgressAsync(_catalogBookId!.Value, CurrentPage, TotalPages);
            }
            catch
            {
                // Ignore transient DB lock
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _lifecycleVersion);
        _progressSaveDebounceCts?.Cancel();
        _progressSaveDebounceCts?.Dispose();
        CancelPendingRenders();
        _resizeDebounceCts?.Cancel();
        _resizeDebounceCts?.Dispose();
        _surfacePool.ReleaseAllSurfaces();
        _pdfSession?.Dispose();
        _pdfSession = null;
    }
}

public partial class ReaderViewModel : ObservableObject, IDisposable
{
    private const int MaximumOpenTabs = 20;
    private readonly ReaderSettingsService _settingsService;
    private double _lastKnownViewportWidth = 960;
    private double _lastKnownViewportHeight = 760;
    private string _lastSelectedTextForBookmark = string.Empty;
    private bool _wasTocVisibleBeforeFullScreen = true;
    private bool _wasNotesVisibleBeforeFullScreen;
    private CancellationTokenSource? _searchDebounceCts;
    private List<DocumentSearchMatch> _searchMatches = [];
    private string _lastExecutedSearchQuery = string.Empty;
    private DocumentTabViewModel? _lastSearchedTab;
    private bool _disposed;

    public event Action? FocusViewportRequested;
    public event Action? FocusSearchBoxRequested;
    public event Action<DocumentSearchMatch?>? SearchMatchChanged;
    public event Action<DocumentTabViewModel?, DocumentTabViewModel?>? ActiveTabSwitching;

    public ObservableCollection<DocumentTabViewModel> OpenTabs { get; } = [];
    public ObservableCollection<RecentReaderFileEntry> RecentFiles { get; } = [];

    public bool HasOpenTabs => OpenTabs.Count > 0;
    public bool IsStartScreenVisible => OpenTabs.Count == 0;
    public bool HasRecentFiles => RecentFiles.Count > 0;

    [ObservableProperty] private IReadOnlyList<ZoomPresetOption> _zoomPresets = BuildZoomPresets();

    private static IReadOnlyList<ZoomPresetOption> BuildZoomPresets() =>
    [
        new("FitWidth", ReaderLocalizationService.GetString("Loc_ZoomFitWidth")),
        new("FitPage", ReaderLocalizationService.GetString("Loc_ZoomFitPage")),
        new("50%", "50%"),
        new("75%", "75%"),
        new("100%", "100%"),
        new("125%", "125%"),
        new("150%", "150%"),
        new("200%", "200%")
    ];

    [ObservableProperty] private DocumentTabViewModel? _activeTab;
    [ObservableProperty] private DocumentTabViewModel? _secondarySplitTab;
    [ObservableProperty] private ZoomPresetOption _selectedZoomPreset;
    [ObservableProperty] private bool _isSplitViewEnabled;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _isTocVisible = true;
    [ObservableProperty] private bool _isAnnotationsVisible;
    [ObservableProperty] private bool _isTextSelectionMode;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private int _searchCurrentMatchIndex;
    [ObservableProperty] private int _searchTotalMatches;

    public bool HasSearchQuery => !string.IsNullOrEmpty(SearchQuery);
    public DocumentSearchMatch? ActiveSearchMatch { get; private set; }
    public IReadOnlyList<DocumentSearchMatch> AllSearchMatches => _searchMatches;

    public bool CanSelectText => ActiveTab is { HasTextLayer: true };
    public string TextSelectionToolTip => ActiveTab != null
        ? ActiveTab.TextSelectionToolTip
        : ReaderLocalizationService.GetString("Loc_SelectionTooltipEnabled");
    public string SearchBoxToolTip => CanSelectText
        ? ReaderLocalizationService.GetString("Loc_SearchBoxTooltipEnabled")
        : ReaderLocalizationService.GetString("Loc_SelectionTooltipDisabled");

    partial void OnIsTextSelectionModeChanged(bool value)
    {
        if (value && !CanSelectText)
        {
            _isTextSelectionMode = false;
            OnPropertyChanged(nameof(IsTextSelectionMode));
        }
    }

    [RelayCommand]
    public void ToggleTextSelectionMode()
    {
        if (!CanSelectText)
        {
            IsTextSelectionMode = false;
            return;
        }

        IsTextSelectionMode = !IsTextSelectionMode;
    }

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchQuery));
        ScheduleDebouncedSearch(value);
    }

    private void ScheduleDebouncedSearch(string rawQuery)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;

        string query = rawQuery.Trim();
        if (string.IsNullOrWhiteSpace(query) || !CanSelectText || ActiveTab == null)
        {
            _searchMatches = [];
            _lastExecutedSearchQuery = string.Empty;
            _lastSearchedTab = ActiveTab;
            ActiveSearchMatch = null;
            SearchCurrentMatchIndex = 0;
            SearchTotalMatches = 0;
            SearchMatchChanged?.Invoke(null);
            return;
        }

        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token);
                if (token.IsCancellationRequested)
                    return;

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    await dispatcher.InvokeAsync(async () =>
                    {
                        await ExecuteSearchImmediateAsync(query, token, jumpToNearestMatch: true);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task ExecuteSearchImmediateAsync(string query, CancellationToken ct, bool jumpToNearestMatch)
    {
        if (ActiveTab == null || !CanSelectText || string.IsNullOrWhiteSpace(query))
        {
            _searchMatches = [];
            _lastExecutedSearchQuery = string.Empty;
            _lastSearchedTab = ActiveTab;
            ActiveSearchMatch = null;
            SearchCurrentMatchIndex = 0;
            SearchTotalMatches = 0;
            SearchMatchChanged?.Invoke(null);
            return;
        }

        var tab = ActiveTab;
        var matches = await tab.SearchAllMatchesAsync(query, ct);
        if (ct.IsCancellationRequested || ActiveTab != tab)
            return;

        _searchMatches = matches;
        _lastExecutedSearchQuery = query;
        _lastSearchedTab = tab;
        SearchTotalMatches = _searchMatches.Count;

        if (_searchMatches.Count == 0)
        {
            ActiveSearchMatch = null;
            SearchCurrentMatchIndex = 0;
            SearchMatchChanged?.Invoke(null);
            return;
        }

        int chosenIdx = 0;
        if (jumpToNearestMatch)
        {
            int curPage = tab.CurrentPage;
            int foundIdx = _searchMatches.FindIndex(m => m.PageNumber >= curPage);
            if (foundIdx >= 0)
                chosenIdx = foundIdx;
        }

        SearchCurrentMatchIndex = chosenIdx + 1;
        ActiveSearchMatch = _searchMatches[chosenIdx];
        await NavigateToActiveSearchMatchAsync();
    }

    private async Task NavigateToActiveSearchMatchAsync()
    {
        if (ActiveTab == null || ActiveSearchMatch == null)
        {
            SearchMatchChanged?.Invoke(null);
            return;
        }

        var match = ActiveSearchMatch;
        if (ActiveTab.CurrentPage != match.PageNumber)
        {
            await ActiveTab.NavigateToPagePriorityAsync(match.PageNumber);
        }

        SearchMatchChanged?.Invoke(match);
    }

    [RelayCommand]
    public async Task NextSearchMatchAsync()
    {
        if (ActiveTab == null || !CanSelectText)
            return;

        string query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return;

        if (_lastSearchedTab != ActiveTab ||
            !string.Equals(_lastExecutedSearchQuery, query, StringComparison.OrdinalIgnoreCase))
        {
            _searchDebounceCts?.Cancel();
            using var cts = new CancellationTokenSource();
            await ExecuteSearchImmediateAsync(query, cts.Token, jumpToNearestMatch: true);
            return;
        }

        if (_searchMatches.Count == 0)
            return;

        int nextOneBased = SearchCurrentMatchIndex >= _searchMatches.Count
            ? 1
            : SearchCurrentMatchIndex + 1;

        SearchCurrentMatchIndex = nextOneBased;
        ActiveSearchMatch = _searchMatches[nextOneBased - 1];
        await NavigateToActiveSearchMatchAsync();
    }

    [RelayCommand]
    public async Task PreviousSearchMatchAsync()
    {
        if (ActiveTab == null || !CanSelectText)
            return;

        string query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return;

        if (_lastSearchedTab != ActiveTab ||
            !string.Equals(_lastExecutedSearchQuery, query, StringComparison.OrdinalIgnoreCase))
        {
            _searchDebounceCts?.Cancel();
            using var cts = new CancellationTokenSource();
            await ExecuteSearchImmediateAsync(query, cts.Token, jumpToNearestMatch: true);
            return;
        }

        if (_searchMatches.Count == 0)
            return;

        int prevOneBased = SearchCurrentMatchIndex <= 1
            ? _searchMatches.Count
            : SearchCurrentMatchIndex - 1;

        SearchCurrentMatchIndex = prevOneBased;
        ActiveSearchMatch = _searchMatches[prevOneBased - 1];
        await NavigateToActiveSearchMatchAsync();
    }

    [RelayCommand]
    public void ClearSearch()
    {
        _searchDebounceCts?.Cancel();
        SearchQuery = string.Empty;
        _searchMatches = [];
        _lastExecutedSearchQuery = string.Empty;
        ActiveSearchMatch = null;
        SearchCurrentMatchIndex = 0;
        SearchTotalMatches = 0;
        SearchMatchChanged?.Invoke(null);
        FocusViewportRequested?.Invoke();
    }

    [RelayCommand]
    public void FocusSearchBox()
    {
        if (!CanSelectText)
            return;

        FocusSearchBoxRequested?.Invoke();
    }

    [ObservableProperty] private string _newQuoteInput = string.Empty;
    [ObservableProperty] private string _newCommentInput = string.Empty;
    [ObservableProperty] private string _selectedHighlightColor = "#FACC15";
    [ObservableProperty] private string _selectedThemeName;
    [ObservableProperty] private ReaderLanguageOption _selectedLanguageOption;

    public IReadOnlyList<ReaderLanguageOption> SupportedLanguages { get; } = ReaderLocalizationService.SupportedLanguages;
    public IReadOnlyList<string> ThemeNames { get; } = ReaderThemeService.Presets.Select(p => p.Name).ToList();
    public IReadOnlyList<ThemePreset> LightThemes { get; } = ReaderThemeService.LightThemes;
    public IReadOnlyList<ThemePreset> DarkThemes { get; } = ReaderThemeService.DarkThemes;
    public string[] HighlightColors { get; } = ["#FACC15", "#34D399", "#38BDF8", "#F472B6", "#A78BFA"];

    public ReaderViewModel(ReaderSettingsService settingsService)
    {
        _settingsService = settingsService;
        _selectedThemeName = _settingsService.Current.ThemeName;
        string normalizedLang = ReaderLocalizationService.NormalizeLanguageCode(_settingsService.Current.Language);
        _selectedLanguageOption = SupportedLanguages.FirstOrDefault(l => l.Code.Equals(normalizedLang, StringComparison.OrdinalIgnoreCase))
                                  ?? SupportedLanguages[0];
        _isTocVisible = _settingsService.Current.IsTocVisible;
        _isAnnotationsVisible = _settingsService.Current.IsAnnotationsVisible;

        string savedZoomMode = string.IsNullOrWhiteSpace(_settingsService.Current.ZoomMode)
            ? "FitWidth"
            : _settingsService.Current.ZoomMode;

        _selectedZoomPreset = ZoomPresets.FirstOrDefault(z => z.Key.Equals(savedZoomMode, StringComparison.OrdinalIgnoreCase))
                              ?? ZoomPresets[0];

        ReaderThemeService.ThemeChanged += OnGlobalThemeChanged;
        ReaderLocalizationService.LanguageChanged += OnGlobalLanguageChanged;
        OpenTabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOpenTabs));
            OnPropertyChanged(nameof(IsStartScreenVisible));
        };

        ReloadRecentFilesList();
    }

    /// <summary>
    /// One-Shot Text Selection completion for PDF rectangle selection:
    /// Extracts bounded text via PDFium, copies to Clipboard, populates Quote field, and automatically turns off IsTextSelectionMode.
    /// </summary>
    public void CompletePdfRectSelectionOneShot(int pageNumber, double normX1, double normY1, double normX2, double normY2)
    {
        if (ActiveTab == null)
        {
            IsTextSelectionMode = false;
            return;
        }

        string extracted = ActiveTab.ExtractPdfTextByNormalizedRect(pageNumber, normX1, normY1, normX2, normY2);
        CommitOneShotSelectedText(extracted);
    }

    /// <summary>
    /// Copies selected text to Clipboard, populates the Notes Quote input, caches it for concise bookmarking,
    /// and automatically exits selection mode (One-Shot UX).
    /// </summary>
    public void CommitOneShotSelectedText(string? rawSelectedText)
    {
        string cleaned = Regex.Replace(rawSelectedText ?? string.Empty, @"\s+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            _lastSelectedTextForBookmark = cleaned;
            NewQuoteInput = cleaned;
            IsAnnotationsVisible = true;
            _settingsService.Current.IsAnnotationsVisible = true;
            _settingsService.Save();

            try
            {
                Clipboard.SetText(cleaned);
            }
            catch
            {
                // Ignore transient clipboard lock
            }
        }

        // Automatically exit selection mode after mouse release (One-Shot UX)
        IsTextSelectionMode = false;
    }

    public void ReloadRecentFilesList()
    {
        RecentFiles.Clear();
        foreach (var item in _settingsService.Current.RecentFiles
                     .Where(r => BookFormatParser.IsReaderExtensionSupported(r.FilePath, out _))
                     .Take(ReaderSettingsService.MaxRecentFiles))
        {
            RecentFiles.Add(item);
        }
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private void OnActiveTabInternalPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.HasTextLayer) or nameof(DocumentTabViewModel.TextSelectionToolTip))
        {
            OnPropertyChanged(nameof(CanSelectText));
            OnPropertyChanged(nameof(TextSelectionToolTip));
            OnPropertyChanged(nameof(SearchBoxToolTip));
            if (!CanSelectText)
            {
                if (IsTextSelectionMode)
                    IsTextSelectionMode = false;
                if (!string.IsNullOrEmpty(SearchQuery))
                    ClearSearch();
            }
        }
    }

    partial void OnActiveTabChanged(DocumentTabViewModel? value)
    {
        if (value == null && OpenTabs.Count > 0)
        {
            var fallback = OpenTabs[0];
            Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Send,
                new Action(() =>
                {
                    if (ActiveTab == null && OpenTabs.Count > 0)
                    {
                        ActiveTab = OpenTabs.Contains(fallback) ? fallback : OpenTabs[0];
                    }
                }));
            return;
        }

        OnPropertyChanged(nameof(CanSelectText));
        OnPropertyChanged(nameof(TextSelectionToolTip));
        OnPropertyChanged(nameof(SearchBoxToolTip));
        if (!CanSelectText && IsTextSelectionMode)
        {
            IsTextSelectionMode = false;
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            ScheduleDebouncedSearch(SearchQuery);
        }
        else
        {
            SearchMatchChanged?.Invoke(null);
        }

        if (value == null) return;
        value.PropertyChanged += OnActiveTabInternalPropertyChanged;
        value.UpdateViewportSize(_lastKnownViewportWidth, _lastKnownViewportHeight);
        value.SyncToolbarAndSidebarState();
        if (value.IsBackgroundUnloaded)
        {
            _ = value.ResumeFromBackgroundAsync();
        }
        else if (value.IsDocumentInitialized)
        {
            _ = value.RefreshActiveViewAsync();
        }
        else
        {
            _ = value.InitializeDocumentAsync();
        }

        var match = ZoomPresets.FirstOrDefault(z => z.Key.Equals(value.ZoomMode, StringComparison.OrdinalIgnoreCase));
        if (match != null && match != SelectedZoomPreset)
        {
            _selectedZoomPreset = match;
            OnPropertyChanged(nameof(SelectedZoomPreset));
        }
    }

    partial void OnActiveTabChanging(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue))
        {
            ActiveTabSwitching?.Invoke(oldValue, newValue);
        }

        if (oldValue != null)
        {
            oldValue.PropertyChanged -= OnActiveTabInternalPropertyChanged;
            if (oldValue != newValue)
            {
                oldValue.UnloadBackgroundTabBitmaps();
            }
        }
    }

    partial void OnSelectedZoomPresetChanged(ZoomPresetOption value)
    {
        if (value == null) return;
        _settingsService.Current.ZoomMode = value.Key;
        _settingsService.Save();

        foreach (var tab in OpenTabs)
        {
            tab.ApplyZoomMode(value.Key);
        }
    }

    public void UpdateViewportDimensions(double width, double height)
    {
        if (width <= 80 || height <= 80) return;
        _lastKnownViewportWidth = width;
        _lastKnownViewportHeight = height;

        foreach (var tab in OpenTabs)
        {
            tab.UpdateViewportSize(width, height);
        }
    }

    private void OnGlobalThemeChanged(ThemePreset preset)
    {
        ActiveTab?.ClearCacheAndReloadThemeAsync();
    }

    private void OnGlobalLanguageChanged(string langCode)
    {
        string currentZoomKey = SelectedZoomPreset?.Key ?? "FitWidth";
        ZoomPresets = BuildZoomPresets();
        SelectedZoomPreset = ZoomPresets.FirstOrDefault(z => z.Key.Equals(currentZoomKey, StringComparison.OrdinalIgnoreCase))
                             ?? ZoomPresets[0];
        OnPropertyChanged(nameof(TextSelectionToolTip));
        OnPropertyChanged(nameof(SearchBoxToolTip));

        foreach (var tab in OpenTabs)
        {
            tab.RefreshLocalizedProperties();
        }
    }

    partial void OnSelectedLanguageOptionChanged(ReaderLanguageOption value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.Code))
            return;

        ReaderLocalizationService.ApplyLanguage(value.Code);
        _settingsService.Current.Language = value.Code;
        _settingsService.Save();
    }

    partial void OnSelectedThemeNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ReaderThemeService.ApplyTheme(value);
            _settingsService.Current.ThemeName = value;
            _settingsService.Save();
        }
    }

    [RelayCommand]
    private void SelectTheme(string? themeName)
    {
        if (!string.IsNullOrWhiteSpace(themeName))
        {
            SelectedThemeName = themeName;
        }
    }

    public async Task OpenLaunchRequestAsync(ReaderLaunchRequest request)
    {
        if (!request.HasFile || string.IsNullOrWhiteSpace(request.FilePath))
            return;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(request.FilePath);
        }
        catch
        {
            fullPath = request.FilePath;
        }

        if (!BookFormatParser.IsReaderExtensionSupported(fullPath, out string ext))
        {
            MessageBox.Show(
                BookFormatParser.BuildUnsupportedFormatMessage(ext),
                "Формат не підтримується",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!File.Exists(fullPath))
        {
            MessageBox.Show(
                $"Файл не знайдено:\n{fullPath}",
                "Файл відсутній",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!BookFormatParser.ValidateReaderFileSignature(fullPath, out string signatureError))
        {
            MessageBox.Show(
                signatureError,
                "Пошкоджений або непідтримуваний файл",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var existingTab = OpenTabs.FirstOrDefault(t =>
            string.Equals(Path.GetFullPath(t.Book.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));

        if (existingTab != null)
        {
            ActiveTab = existingTab;
            if (request.InitialPage is > 0)
            {
                await existingTab.NavigateToPagePriorityAsync(request.InitialPage.Value);
            }
            return;
        }

        if (OpenTabs.Count >= MaximumOpenTabs)
        {
            MessageBox.Show($"Досягнуто ліміту: одночасно можна відкрити не більше {MaximumOpenTabs} вкладок.",
                "Ліміт вкладок", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BookRecord? book = null;
        string? dbPath = request.DbPath;
        long? bookId = request.BookId;

        if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
        {
            try
            {
                var db = new LibraryDatabase(dbPath);
                if (bookId is > 0)
                {
                    book = await db.GetBookByIdAsync(bookId.Value);
                }
                book ??= await db.GetBookByPathAsync(fullPath);
                if (book != null)
                {
                    bookId = book.Id;
                }
            }
            catch
            {
            }
        }

        if (book == null)
        {
            var recent = _settingsService.GetRecentEntry(fullPath);
            int startPage = request.InitialPage ?? recent?.CurrentPage ?? 1;
            book = BookFormatParser.CreateStandaloneBookRecord(fullPath, bookId ?? 0, startPage);
            if (recent != null)
            {
                book.Title = recent.Title;
                book.Author = recent.Author;
                book.PageCount = Math.Max(1, recent.TotalPages);
                book.ReadingProgress = recent.ReadingProgress;
            }
        }
        else if (request.InitialPage is > 0)
        {
            book.CurrentPage = request.InitialPage.Value;
        }

        var newTab = new DocumentTabViewModel(
            book,
            _settingsService,
            initialZoomMode: "FitWidth",
            initialViewMode: ReaderViewMode.ContinuousVertical,
            catalogBookId: bookId,
            catalogDbPath: dbPath);

        newTab.UpdateViewportSize(_lastKnownViewportWidth, _lastKnownViewportHeight);
        OpenTabs.Add(newTab);
        ActiveTab = newTab;
        if (SecondarySplitTab == null || SecondarySplitTab == OpenTabs[0])
            SecondarySplitTab = newTab;

        await newTab.InitializeDocumentAsync();
        ReloadRecentFilesList();
    }

    public async Task OpenFilesFromDropAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;

            await OpenLaunchRequestAsync(new ReaderLaunchRequest { FilePath = path });
        }
    }

    [RelayCommand]
    private async Task OpenAnotherFileInTabAsync()
    {
        if (OpenTabs.Count >= MaximumOpenTabs)
        {
            MessageBox.Show($"Досягнуто ліміту: одночасно можна відкрити не більше {MaximumOpenTabs} вкладок.",
                "Ліміт вкладок", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Відкрити книгу або документ",
            Multiselect = true,
            Filter = "Усі підтримувані книги|*.pdf;*.epub;*.fb2;*.mobi;*.azw;*.azw3;*.docx;*.txt;*.md;*.rtf;*.cbz;*.djvu|PDF Документи (*.pdf)|*.pdf|Електронні книги (*.epub;*.fb2;*.mobi;*.azw;*.azw3)|*.epub;*.fb2;*.mobi;*.azw;*.azw3|Документи (*.docx;*.txt;*.md;*.rtf)|*.docx;*.txt;*.md;*.rtf|Комікси та архіви (*.cbz;*.djvu)|*.cbz;*.djvu"
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                if (File.Exists(file))
                {
                    await OpenLaunchRequestAsync(new ReaderLaunchRequest { FilePath = file });
                }
            }
        }
    }

    [RelayCommand]
    private async Task OpenRecentFileAsync(RecentReaderFileEntry? entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
            return;

        if (!File.Exists(entry.FilePath))
        {
            var res = MessageBox.Show(
                $"Файл не знайдено за шляхом:\n{entry.FilePath}\n\nВидалити його зі списку останніх відкритих?",
                "Файл не знайдено",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                _settingsService.RemoveRecentFile(entry.FilePath);
                ReloadRecentFilesList();
            }
            return;
        }

        await OpenLaunchRequestAsync(new ReaderLaunchRequest
        {
            FilePath = entry.FilePath,
            InitialPage = entry.CurrentPage
        });
    }

    [RelayCommand]
    private void RemoveRecentFile(RecentReaderFileEntry? entry)
    {
        if (entry == null) return;
        _settingsService.RemoveRecentFile(entry.FilePath);
        ReloadRecentFilesList();
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _settingsService.ClearRecentFiles();
        ReloadRecentFilesList();
    }

    [RelayCommand]
    private async Task CloseTabAsync(DocumentTabViewModel? tab)
    {
        if (tab == null || !OpenTabs.Contains(tab))
            return;

        int index = OpenTabs.IndexOf(tab);
        bool wasActive = ReferenceEquals(ActiveTab, tab);
        bool wasSecondary = ReferenceEquals(SecondarySplitTab, tab);

        // Select neighbor tab BEFORE removing from OpenTabs so WPF ListBox.SelectedItem
        // never clears ActiveTab to null during collection removal.
        DocumentTabViewModel? nextActiveTab = null;
        if (wasActive && OpenTabs.Count > 1)
        {
            int nextIndex = index > 0 ? index - 1 : 1;
            nextActiveTab = OpenTabs[nextIndex];
        }

        await tab.SaveProgressAsync();

        if (wasActive && nextActiveTab != null)
        {
            ActiveTab = nextActiveTab;
        }

        OpenTabs.Remove(tab);
        tab.Dispose();

        if (OpenTabs.Count > 0)
        {
            var desiredTab = nextActiveTab ?? OpenTabs.LastOrDefault() ?? OpenTabs[0];
            if (!OpenTabs.Contains(desiredTab))
            {
                desiredTab = OpenTabs.LastOrDefault() ?? OpenTabs[0];
            }

            ActiveTab = desiredTab;
            OnPropertyChanged(nameof(ActiveTab));
            desiredTab.UpdateViewportSize(_lastKnownViewportWidth, _lastKnownViewportHeight);
            desiredTab.SyncToolbarAndSidebarState();
            await desiredTab.ResumeFromBackgroundAsync();
        }
        else
        {
            ActiveTab = null;
        }

        if (wasSecondary || SecondarySplitTab == null || !OpenTabs.Contains(SecondarySplitTab))
        {
            SecondarySplitTab = ActiveTab ?? OpenTabs.FirstOrDefault();
        }

        ReloadRecentFilesList();
        FocusViewportRequested?.Invoke();
    }

    [RelayCommand]
    private async Task CloseOtherTabsAsync(DocumentTabViewModel? keepTab)
    {
        if (keepTab == null || !OpenTabs.Contains(keepTab))
            return;

        ActiveTab = keepTab;
        SecondarySplitTab = keepTab;

        foreach (var tab in OpenTabs.Where(t => t != keepTab).ToList())
        {
            await tab.SaveProgressAsync();
            OpenTabs.Remove(tab);
            tab.Dispose();
        }

        keepTab.SyncToolbarAndSidebarState();
        await keepTab.ResumeFromBackgroundAsync();
        ReloadRecentFilesList();
        FocusViewportRequested?.Invoke();
    }

    [RelayCommand]
    private async Task CloseAllTabsAsync()
    {
        foreach (var tab in OpenTabs.ToList())
        {
            await tab.SaveProgressAsync();
            tab.Dispose();
        }

        OpenTabs.Clear();
        ActiveTab = null;
        SecondarySplitTab = null;
        ReloadRecentFilesList();
    }

    [RelayCommand]
    private void SetViewMode(string mode)
    {
        if (ActiveTab == null) return;
        ActiveTab.ViewMode = mode switch
        {
            "TwoPageSpread" => ReaderViewMode.TwoPageSpread,
            "ContinuousVertical" => ReaderViewMode.ContinuousVertical,
            _ => ReaderViewMode.SinglePage
        };
        _settingsService.Current.LastReaderViewMode = ActiveTab.ViewMode.ToString();
        _settingsService.Save();
    }

    [RelayCommand]
    private void SetZoomPreset(string presetKey)
    {
        var target = ZoomPresets.FirstOrDefault(z => z.Key.Equals(presetKey, StringComparison.OrdinalIgnoreCase));
        if (target != null)
        {
            SelectedZoomPreset = target;
        }
    }

    [RelayCommand]
    private void ToggleSplitView()
    {
        IsSplitViewEnabled = !IsSplitViewEnabled;
        if (IsSplitViewEnabled && SecondarySplitTab == null)
        {
            SecondarySplitTab = OpenTabs.LastOrDefault() ?? ActiveTab;
        }
    }

    [RelayCommand]
    private void ToggleFullScreen()
    {
        if (!IsFullScreen)
        {
            _wasTocVisibleBeforeFullScreen = IsTocVisible;
            _wasNotesVisibleBeforeFullScreen = IsAnnotationsVisible;

            IsFullScreen = true;
            IsTocVisible = false;
            IsAnnotationsVisible = false;
        }
        else
        {
            IsFullScreen = false;
            IsTocVisible = _wasTocVisibleBeforeFullScreen;
            IsAnnotationsVisible = _wasNotesVisibleBeforeFullScreen;
        }
    }

    [RelayCommand]
    private void ToggleToc()
    {
        IsTocVisible = !IsTocVisible;
        _settingsService.Current.IsTocVisible = IsTocVisible;
        _settingsService.Save();
    }

    [RelayCommand]
    private void ToggleAnnotations()
    {
        IsAnnotationsVisible = !IsAnnotationsVisible;
        _settingsService.Current.IsAnnotationsVisible = IsAnnotationsVisible;
        _settingsService.Save();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (ActiveTab == null) return;
        int step = ActiveTab.IsTwoPageSpreadMode ? 2 : 1;
        await ActiveTab.NavigateToPagePriorityAsync(ActiveTab.CurrentPage + step);
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (ActiveTab == null) return;
        int step = ActiveTab.IsTwoPageSpreadMode ? 2 : 1;
        await ActiveTab.NavigateToPagePriorityAsync(ActiveTab.CurrentPage - step);
    }

    [RelayCommand]
    private async Task FirstPageAsync()
    {
        if (ActiveTab != null) await ActiveTab.NavigateToPagePriorityAsync(1);
    }

    [RelayCommand]
    private async Task LastPageAsync()
    {
        if (ActiveTab != null) await ActiveTab.NavigateToPagePriorityAsync(ActiveTab.TotalPages);
    }

    [RelayCommand]
    private async Task JumpToInputPageAsync()
    {
        if (ActiveTab == null) return;
        if (int.TryParse(ActiveTab.PageJumpInput, out int target))
            await ActiveTab.NavigateToPagePriorityAsync(target);
        else
            ActiveTab.PageJumpInput = ActiveTab.CurrentPage.ToString();
    }

    [RelayCommand]
    private void ZoomIn()
    {
        string[] steps = ["50%", "75%", "100%", "125%", "150%", "200%"];
        int curIdx = Array.FindIndex(steps, s => s.Equals(SelectedZoomPreset?.Key, StringComparison.OrdinalIgnoreCase));
        if (curIdx < 0) curIdx = 2;
        int nextIdx = Math.Min(steps.Length - 1, curIdx + 1);
        SetZoomPreset(steps[nextIdx]);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        string[] steps = ["50%", "75%", "100%", "125%", "150%", "200%"];
        int curIdx = Array.FindIndex(steps, s => s.Equals(SelectedZoomPreset?.Key, StringComparison.OrdinalIgnoreCase));
        if (curIdx < 0) curIdx = 2;
        int prevIdx = Math.Max(0, curIdx - 1);
        SetZoomPreset(steps[prevIdx]);
    }

    [RelayCommand]
    private void SetHighlightColor(string colorHex)
    {
        if (!string.IsNullOrWhiteSpace(colorHex))
            SelectedHighlightColor = colorHex;
    }

    /// <summary>
    /// Builds a concise, human-readable bookmark caption:
    /// - If user selected text prior to clicking Bookmark: uses that selected snippet (max 45 chars + "...").
    /// - Otherwise: uses "Стор. {N} — {first 3–5 words of paragraph}..."
    /// </summary>
    public static string BuildConciseBookmarkText(int pageNumber, string? selectedSnippet, string? pageRawText)
    {
        string chosen = !string.IsNullOrWhiteSpace(selectedSnippet) ? selectedSnippet.Trim() : string.Empty;
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            chosen = Regex.Replace(chosen, @"\s+", " ").Trim();
            if (chosen.Length > 45)
                chosen = chosen[..45].TrimEnd() + "...";
            return $"Стор. {pageNumber} — «{chosen}»";
        }

        if (!string.IsNullOrWhiteSpace(pageRawText))
        {
            string[] lines = pageRawText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 2 && l.Any(char.IsLetter))
                .ToArray();

            string firstLine = lines.FirstOrDefault() ?? pageRawText;
            string[] words = Regex.Replace(firstLine, @"\s+", " ")
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length > 0)
            {
                string firstWords = string.Join(" ", words.Take(5));
                if (firstWords.Length > 42)
                    firstWords = firstWords[..42].TrimEnd();
                return $"Стор. {pageNumber} — {firstWords}...";
            }
        }

        return $"Стор. {pageNumber} — Швидка закладка";
    }

    [RelayCommand]
    private async Task AddQuickBookmarkAsync()
    {
        if (ActiveTab == null) return;

        string? selectedSnippet = !string.IsNullOrWhiteSpace(_lastSelectedTextForBookmark)
            ? _lastSelectedTextForBookmark
            : (!string.IsNullOrWhiteSpace(NewQuoteInput) ? NewQuoteInput : null);

        string pageSample = string.IsNullOrWhiteSpace(selectedSnippet)
            ? ActiveTab.GetCurrentPageExtractText()
            : string.Empty;

        string conciseQuote = BuildConciseBookmarkText(ActiveTab.CurrentPage, selectedSnippet, pageSample);
        _lastSelectedTextForBookmark = string.Empty;

        var ann = new AnnotationRecord
        {
            BookId = ActiveTab.CatalogBookId ?? 0,
            PageNumber = ActiveTab.CurrentPage,
            ScrollOffset = ActiveTab.SavedScrollOffset,
            QuoteText = conciseQuote,
            CommentText = "🔖 Закладка",
            HighlightColor = "#38BDF8",
            IsBookmark = true,
            CreatedAt = DateTime.Now
        };

        if (ActiveTab.IsCatalogLinked && File.Exists(ActiveTab.CatalogDbPath))
        {
            try
            {
                var db = new LibraryDatabase(ActiveTab.CatalogDbPath);
                ann.Id = await db.AddAnnotationAsync(ann);
            }
            catch { }
        }

        ActiveTab.Annotations.Add(ann);
        IsAnnotationsVisible = true;
        FocusViewportRequested?.Invoke();
        await ActiveTab.SaveProgressAsync();
    }

    [RelayCommand]
    private async Task AddHighlightOrNoteAsync()
    {
        if (ActiveTab == null) return;

        string quote = NewQuoteInput.Trim();
        if (string.IsNullOrWhiteSpace(quote))
        {
            quote = BuildConciseBookmarkText(ActiveTab.CurrentPage, _lastSelectedTextForBookmark, ActiveTab.GetCurrentPageExtractText());
        }
        else
        {
            quote = Regex.Replace(quote, @"\s+", " ").Trim();
            if (quote.Length > 90)
                quote = quote[..90].TrimEnd() + "...";
        }

        _lastSelectedTextForBookmark = string.Empty;

        var ann = new AnnotationRecord
        {
            BookId = ActiveTab.CatalogBookId ?? 0,
            PageNumber = ActiveTab.CurrentPage,
            ScrollOffset = ActiveTab.SavedScrollOffset,
            QuoteText = quote,
            CommentText = NewCommentInput.Trim(),
            HighlightColor = SelectedHighlightColor,
            IsBookmark = false,
            CreatedAt = DateTime.Now
        };

        if (ActiveTab.IsCatalogLinked && File.Exists(ActiveTab.CatalogDbPath))
        {
            try
            {
                var db = new LibraryDatabase(ActiveTab.CatalogDbPath);
                ann.Id = await db.AddAnnotationAsync(ann);
            }
            catch { }
        }

        ActiveTab.Annotations.Add(ann);
        NewQuoteInput = string.Empty;
        NewCommentInput = string.Empty;
        FocusViewportRequested?.Invoke();
        await ActiveTab.SaveProgressAsync();
    }

    [RelayCommand]
    private async Task DeleteAnnotationAsync(AnnotationRecord? ann)
    {
        if (ann == null || ActiveTab == null) return;
        if (ann.Id > 0 && ActiveTab.IsCatalogLinked && File.Exists(ActiveTab.CatalogDbPath))
        {
            try
            {
                var db = new LibraryDatabase(ActiveTab.CatalogDbPath);
                await db.DeleteAnnotationAsync(ann.Id);
            }
            catch { }
        }
        ActiveTab.Annotations.Remove(ann);
        await ActiveTab.SaveProgressAsync();
    }

    [RelayCommand]
    private void ExportAnnotationsToObsidianMd()
    {
        if (ActiveTab == null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Експорт нотаток та витягів у Markdown (Obsidian)",
            Filter = "Markdown файли (*.md)|*.md",
            FileName = $"{ActiveTab.Book.Title}_Notes.md"
        };

        if (dlg.ShowDialog() == true)
        {
            ExportAndBackupService.ExportAnnotationsToMarkdown(ActiveTab.Book, ActiveTab.Annotations, dlg.FileName);
            MessageBox.Show($"Конспект та анотації збережено у файл:\n{dlg.FileName}", "Експорт для Obsidian", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void PrintCurrentPage()
    {
        if (ActiveTab == null) return;
        try
        {
            var pd = new PrintDialog();
            if (pd.ShowDialog() == true)
            {
                if (ActiveTab.IsPdfMode && ActiveTab.CurrentPageImage != null)
                {
                    var img = new Image
                    {
                        Source = ActiveTab.CurrentPageImage,
                        Stretch = Stretch.Uniform
                    };
                    pd.PrintVisual(img, $"AllReaderBook Print — {ActiveTab.Book.Title} (Page {ActiveTab.CurrentPage})");
                }
                else
                {
                    var tb = new TextBlock
                    {
                        Text = ActiveTab.CurrentPageText,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(48),
                        FontSize = 12
                    };
                    pd.PrintVisual(tb, $"AllReaderBook Print — {ActiveTab.Book.Title}");
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Помилка друку: {ex.Message}", "Друк", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task SaveAllTabsProgressAsync()
    {
        foreach (var tab in OpenTabs)
        {
            await tab.SaveProgressAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReaderThemeService.ThemeChanged -= OnGlobalThemeChanged;
        foreach (var tab in OpenTabs)
        {
            tab.Dispose();
        }
    }
}
