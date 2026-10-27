using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BookHub.Core.Models;
using BookHub.Core.Pdf;
using BookHub.Core.Services;
using BookHub.Reader.Services;
using BookHub.Reader.ViewModels;

namespace BookHub.Reader.Views;

public partial class ReaderMainWindow : Window
{
    private readonly ReaderViewModel _viewModel;
    private readonly ReaderSettingsService _settingsService;
    private readonly ReaderLaunchRequest _initialRequest;
    private readonly DispatcherTimer _scrollDebounceTimer;
    private DocumentTabViewModel? _observedTab;
    private bool _isProgrammaticScroll;
    private long _suppressScrollChangedUntilTick;
    private int _lastTocNavigatedPage;
    private long _lastTocNavigationTick;

    // State for debounced 60 FPS continuous scroll
    private ListBox? _pendingScrollListBox;
    private DocumentTabViewModel? _pendingScrollTab;
    private double _pendingVerticalChange;

    // State for One-Shot Character/Paragraph PDF Text Selection
    private bool _isPdfTextSelecting;
    private Point _pdfSelectStartNorm;
    private Canvas? _activeSelectionCanvas;
    private string _currentDraggedPdfText = string.Empty;

    // Loaded search highlight overlay canvases across single/split/continuous views
    private readonly HashSet<Canvas> _loadedSearchHighlightCanvases = [];

    // Snapshot before entering Fullscreen (F11)
    private WindowState _previousWindowState = WindowState.Normal;
    private WindowStyle _previousWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _previousResizeMode = ResizeMode.CanResize;
    private Rect _previousNormalBounds = new(120, 80, 1360, 880);

    public ReaderMainWindow(
        ReaderViewModel viewModel,
        ReaderSettingsService settingsService,
        ReaderLaunchRequest initialRequest)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        _initialRequest = initialRequest;

        // 18 ms frame-synced debounce timer (~60 FPS) for smooth continuous scrolling
        _scrollDebounceTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(18)
        };
        _scrollDebounceTimer.Tick += OnScrollDebounceTimerTick;

        DataContext = _viewModel;
        _viewModel.OpenTabs.CollectionChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                EnsureActiveTabVisibleInStrip();
                UpdateTabOverflowIndicators();
            }));
        };
        ObserveActiveTab(_viewModel.ActiveTab);

        RestoreReaderWindowGeometry();
        _viewModel.ActiveTabSwitching += OnActiveTabSwitching;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.FocusViewportRequested += FocusReadingViewport;
        _viewModel.FocusSearchBoxRequested += FocusSearchBox;
        _viewModel.SearchMatchChanged += OnSearchMatchChanged;

        Loaded += async (_, _) =>
        {
            if (PrimaryViewportHost.ActualWidth > 80 && PrimaryViewportHost.ActualHeight > 80)
            {
                _viewModel.UpdateViewportDimensions(PrimaryViewportHost.ActualWidth, PrimaryViewportHost.ActualHeight);
            }

            if (_initialRequest.HasFile)
            {
                await _viewModel.OpenLaunchRequestAsync(_initialRequest);
                EnsureActiveTabVisibleInStrip();
                UpdateTabOverflowIndicators();
                RestoreActiveTabScrollPosition();
            }
        };

        Closing += OnWindowClosing;
    }

    public void BringToForeground()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _previousWindowState == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void OnExternalDocumentOpened()
    {
        if (TabListBox != null && _viewModel.ActiveTab != null)
        {
            TabListBox.SelectedItem = _viewModel.ActiveTab;
        }

        EnsureActiveTabVisibleInStrip();
        UpdateTabOverflowIndicators();
        RestoreActiveTabScrollPosition();
        FocusReadingViewport();
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await _viewModel.OpenFilesFromDropAsync(files);
            EnsureActiveTabVisibleInStrip();
            UpdateTabOverflowIndicators();
            RestoreActiveTabScrollPosition();
        }
    }

    private void OnActiveTabSwitching(DocumentTabViewModel? oldTab, DocumentTabViewModel? newTab)
    {
        _scrollDebounceTimer.Stop();
        _pendingScrollListBox = null;
        _pendingScrollTab = null;

        if (oldTab != null)
        {
            CaptureTabScrollStateFromVisualTree(oldTab);
        }

        // Suppress spurious ScrollChanged events triggered when DataContext / ItemsSource switches to newTab
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 900;
    }

    private void CaptureTabScrollStateFromVisualTree(DocumentTabViewModel tab)
    {
        if (tab.IsLoading || tab.IsBackgroundUnloaded || tab.IsResumingFromBackground)
            return;

        if (tab.IsContinuousVerticalMode)
        {
            ListBox listBox = tab.IsPdfMode ? PdfContinuousListBox : TextContinuousListBox;
            if (!ReferenceEquals(listBox.DataContext, tab))
                return;

            if (FindVisualChild<ScrollViewer>(listBox) is { } sv)
            {
                tab.SavedScrollOffset = sv.VerticalOffset;
            }

            var (_, _, dominantPage) = GetActuallyVisiblePageRange(listBox, tab, fallbackPage: tab.CurrentPage);
            if (dominantPage >= 1 && dominantPage <= tab.TotalPages)
            {
                tab.CurrentPage = dominantPage;
                tab.PageJumpInput = dominantPage.ToString();

                if (dominantPage <= tab.ContinuousWindowPages.Count &&
                    FindRealizedContainerForItem(listBox, tab.ContinuousWindowPages[dominantPage - 1]) is { } container)
                {
                    double itemTop = container.TransformToAncestor(listBox).Transform(new Point(0, 0)).Y;
                    tab.SavedPageRelativeOffset = Math.Max(0, -itemTop);
                }
            }
        }
        else
        {
            ScrollViewer sv = tab.IsPdfMode ? PdfScrollViewer : TextScrollViewer;
            if (ReferenceEquals(sv.DataContext, tab))
            {
                tab.SavedScrollOffset = sv.VerticalOffset;
                tab.SavedPageRelativeOffset = 0;
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.IsTocVisible))
        {
            ApplyTocColumnCollapseState(_viewModel.IsTocVisible);
        }
        else if (e.PropertyName == nameof(ReaderViewModel.IsAnnotationsVisible))
        {
            ApplyNotesColumnCollapseState(_viewModel.IsAnnotationsVisible);
        }
        else if (e.PropertyName == nameof(ReaderViewModel.IsFullScreen))
        {
            ApplyFullScreenWindowState(_viewModel.IsFullScreen);
        }
        else if (e.PropertyName == nameof(ReaderViewModel.ActiveTab))
        {
            _scrollDebounceTimer.Stop();
            _pendingScrollListBox = null;
            _pendingScrollTab = null;
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 900;

            if (TabListBox != null && _viewModel.ActiveTab != null && !ReferenceEquals(TabListBox.SelectedItem, _viewModel.ActiveTab))
            {
                TabListBox.SelectedItem = _viewModel.ActiveTab;
            }

            ObserveActiveTab(_viewModel.ActiveTab);
            EnsureActiveTabVisibleInStrip();
            UpdateTabOverflowIndicators();

            if (_viewModel.ActiveTab is { } immediateTab && immediateTab.IsDocumentInitialized)
            {
                RestoreActiveTabScrollPosition();
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
            {
                if (_viewModel.ActiveTab is { } activeTab)
                {
                    if (TabListBox != null && !ReferenceEquals(TabListBox.SelectedItem, activeTab))
                    {
                        TabListBox.SelectedItem = activeTab;
                    }

                    EnsureActiveTabVisibleInStrip();
                    UpdateTabOverflowIndicators();

                    activeTab.SyncToolbarAndSidebarState();
                    if (activeTab.IsBackgroundUnloaded)
                    {
                        await activeTab.ResumeFromBackgroundAsync();
                    }
                    RestoreActiveTabScrollPosition();
                }
            }));
        }
    }

    private async void TabItemBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualAncestor<Button>(source) != null)
            return;

        if (sender is FrameworkElement { DataContext: DocumentTabViewModel clickedTab })
        {
            e.Handled = true;
            if (!ReferenceEquals(_viewModel.ActiveTab, clickedTab))
            {
                _viewModel.ActiveTab = clickedTab;
            }
            else
            {
                clickedTab.SyncToolbarAndSidebarState();
                await clickedTab.ResumeFromBackgroundAsync();
                RestoreActiveTabScrollPosition();
            }

            FocusReadingViewport();
        }
    }

    private void ObserveActiveTab(DocumentTabViewModel? tab)
    {
        if (_observedTab != null)
        {
            _observedTab.PropertyChanged -= OnActiveTabPropertyChanged;
            _observedTab.PriorityScrollRequested -= OnActiveTabPriorityScrollRequested;
        }

        _observedTab = tab;
        if (_observedTab != null)
        {
            _observedTab.PropertyChanged += OnActiveTabPropertyChanged;
            _observedTab.PriorityScrollRequested += OnActiveTabPriorityScrollRequested;
        }
    }

    private void OnActiveTabPriorityScrollRequested(int targetPage, double customVerticalOffset)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnActiveTabPriorityScrollRequested(targetPage, customVerticalOffset));
            return;
        }

        if (_viewModel.ActiveTab is not { } tab)
            return;

        ScrollToPageDeterministic(tab, targetPage, customVerticalOffset, restoreSubPageOffset: customVerticalOffset >= 0);
    }

    private void ScrollToPageDeterministic(
        DocumentTabViewModel tab,
        int targetPage,
        double customVerticalOffset = -1.0,
        bool restoreSubPageOffset = false)
    {
        _scrollDebounceTimer.Stop();
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 450;

        if (tab.IsContinuousVerticalMode)
        {
            ListBox listBox = tab.IsPdfMode ? PdfContinuousListBox : TextContinuousListBox;
            tab.EnsureContinuousPageItems();

            if (targetPage >= 1 && targetPage <= tab.ContinuousWindowPages.Count)
            {
                var targetItem = tab.ContinuousWindowPages[targetPage - 1];
                double subOffset = restoreSubPageOffset ? Math.Max(0, tab.SavedPageRelativeOffset) : 0.0;

                _isProgrammaticScroll = true;
                try
                {
                    listBox.UpdateLayout();
                    listBox.ScrollIntoView(targetItem);
                    listBox.UpdateLayout();

                    if (FindVisualChild<ScrollViewer>(listBox) is { } immSv &&
                        FindRealizedContainerForItem(listBox, targetItem) is { } immContainer)
                    {
                        double immTop = immContainer.TransformToAncestor(listBox).Transform(new Point(0, 0)).Y;
                        double immDelta = immTop + subOffset;
                        if (Math.Abs(immDelta) > 1.5)
                        {
                            immSv.ScrollToVerticalOffset(Math.Max(0, immSv.VerticalOffset + immDelta));
                            listBox.UpdateLayout();
                        }
                        tab.SavedScrollOffset = immSv.VerticalOffset;
                    }
                }
                finally
                {
                    _isProgrammaticScroll = false;
                }

                // Second pass at Loaded priority ensures VirtualizingStackPanel completes multi-page jumps
                // and restores the exact page and sub-page scroll position.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
                {
                    if (_viewModel.ActiveTab != tab || !tab.IsContinuousVerticalMode)
                        return;

                    _suppressScrollChangedUntilTick = Environment.TickCount64 + 320;
                    _isProgrammaticScroll = true;
                    try
                    {
                        listBox.ScrollIntoView(targetItem);
                        listBox.UpdateLayout();

                        if (FindVisualChild<ScrollViewer>(listBox) is { } sv)
                        {
                            if (FindRealizedContainerForItem(listBox, targetItem) is null)
                            {
                                double estimatedOffset = customVerticalOffset > 0
                                    ? customVerticalOffset
                                    : Math.Max(0, (targetPage - 1) * tab.ContinuousPageItemHeight + subOffset);
                                sv.ScrollToVerticalOffset(estimatedOffset);
                                listBox.UpdateLayout();
                                listBox.ScrollIntoView(targetItem);
                                listBox.UpdateLayout();
                            }

                            if (FindRealizedContainerForItem(listBox, targetItem) is { } container)
                            {
                                double itemTop = container.TransformToAncestor(listBox).Transform(new Point(0, 0)).Y;
                                double delta = itemTop + subOffset;
                                if (Math.Abs(delta) > 1.5)
                                {
                                    sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + delta));
                                    listBox.UpdateLayout();
                                }
                            }
                            else if (customVerticalOffset > 0 && sv.ScrollableHeight >= customVerticalOffset)
                            {
                                sv.ScrollToVerticalOffset(customVerticalOffset);
                                listBox.UpdateLayout();
                            }

                            tab.SavedScrollOffset = sv.VerticalOffset;
                        }
                    }
                    finally
                    {
                        _isProgrammaticScroll = false;
                    }

                    int renderFirst = Math.Max(1, targetPage - 1);
                    int renderLast = Math.Min(tab.TotalPages, targetPage + 2);
                    await tab.RenderContinuousPagesAsync(renderFirst, renderLast, isActiveScrollEvent: false, dominantPage: targetPage);
                }));
            }
        }
        else
        {
            ScrollViewer targetSv = tab.IsPdfMode ? PdfScrollViewer : TextScrollViewer;
            double targetOffset = customVerticalOffset >= 0
                ? customVerticalOffset
                : (restoreSubPageOffset ? Math.Max(0, tab.SavedScrollOffset) : 0.0);

            _isProgrammaticScroll = true;
            try
            {
                if (targetOffset > 0)
                    targetSv.ScrollToVerticalOffset(targetOffset);
                else
                    targetSv.ScrollToTop();
            }
            finally
            {
                _isProgrammaticScroll = false;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_viewModel.ActiveTab != tab || tab.IsContinuousVerticalMode)
                    return;

                _suppressScrollChangedUntilTick = Environment.TickCount64 + 220;
                _isProgrammaticScroll = true;
                try
                {
                    targetSv.UpdateLayout();
                    if (targetOffset > 0)
                        targetSv.ScrollToVerticalOffset(targetOffset);
                    else
                        targetSv.ScrollToTop();
                    tab.SavedScrollOffset = targetSv.VerticalOffset;
                }
                finally
                {
                    _isProgrammaticScroll = false;
                }
            }));
        }
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentTabViewModel.ViewMode) ||
            (e.PropertyName == nameof(DocumentTabViewModel.IsLoading) && sender is DocumentTabViewModel { IsLoading: false }))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RestoreActiveTabScrollPosition));
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.CurrentPage)
                                or nameof(DocumentTabViewModel.CurrentPageImage)
                                or nameof(DocumentTabViewModel.SecondSpreadPageImage))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RefreshAllSearchHighlightCanvases));
        }
    }

    private void ApplyTocColumnCollapseState(bool isTocVisible)
    {
        if (isTocVisible)
        {
            double restoredWidth = Math.Clamp(_settingsService.Current.TocWidth, 180, 500);
            TocSidebarColumn.MinWidth = 180;
            TocSidebarColumn.MaxWidth = 500;
            TocSidebarColumn.Width = new GridLength(restoredWidth);
            TocSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (TocSidebarColumn.ActualWidth >= 180)
            {
                _settingsService.Current.TocWidth = Math.Clamp(TocSidebarColumn.ActualWidth, 180, 500);
            }
            TocSidebarColumn.MinWidth = 0;
            TocSidebarColumn.MaxWidth = 0;
            TocSidebarColumn.Width = new GridLength(0);
            TocSplitterColumn.Width = new GridLength(0);
        }
    }

    private void ApplyNotesColumnCollapseState(bool isNotesVisible)
    {
        if (isNotesVisible)
        {
            double restoredWidth = Math.Clamp(_settingsService.Current.NotesWidth, 200, 600);
            NotesSidebarColumn.MinWidth = 200;
            NotesSidebarColumn.MaxWidth = 600;
            NotesSidebarColumn.Width = new GridLength(restoredWidth);
            NotesSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (NotesSidebarColumn.ActualWidth >= 200)
            {
                _settingsService.Current.NotesWidth = Math.Clamp(NotesSidebarColumn.ActualWidth, 200, 600);
            }
            NotesSidebarColumn.MinWidth = 0;
            NotesSidebarColumn.MaxWidth = 0;
            NotesSidebarColumn.Width = new GridLength(0);
            NotesSplitterColumn.Width = new GridLength(0);
        }
    }

    private void PrimaryViewportHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 80 && e.NewSize.Height > 80)
        {
            _viewModel.UpdateViewportDimensions(e.NewSize.Width, e.NewSize.Height);
        }

        if (_viewModel.IsTocVisible && TocSidebarColumn.ActualWidth >= 180)
        {
            _settingsService.Current.TocWidth = Math.Clamp(TocSidebarColumn.ActualWidth, 180, 500);
        }
    }

    private void RestoreReaderWindowGeometry()
    {
        var workArea = SystemParameters.WorkArea;
        MaxHeight = workArea.Height + 14;
        MaxWidth = workArea.Width + 14;

        var s = _settingsService.Current;

        double tocWidth = Math.Clamp(s.TocWidth, 180, 500);
        TocSidebarColumn.MinWidth = 180;
        TocSidebarColumn.MaxWidth = 500;
        TocSidebarColumn.Width = new GridLength(tocWidth);
        ApplyTocColumnCollapseState(_viewModel.IsTocVisible);

        double notesWidth = Math.Clamp(s.NotesWidth, 200, 600);
        NotesSidebarColumn.MinWidth = 200;
        NotesSidebarColumn.MaxWidth = 600;
        NotesSidebarColumn.Width = new GridLength(notesWidth);
        ApplyNotesColumnCollapseState(_viewModel.IsAnnotationsVisible);

        if (s.WindowWidth >= 700 && s.WindowHeight >= 500)
        {
            Width = Math.Min(s.WindowWidth, workArea.Width);
            Height = Math.Min(s.WindowHeight, workArea.Height);
        }

        bool hasValidPosition = !double.IsNaN(s.WindowLeft) &&
                                !double.IsNaN(s.WindowTop) &&
                                s.WindowLeft >= SystemParameters.VirtualScreenLeft - 50 &&
                                s.WindowTop >= SystemParameters.VirtualScreenTop - 50 &&
                                s.WindowLeft + 200 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                                s.WindowTop + 200 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (hasValidPosition)
        {
            Left = Math.Clamp(s.WindowLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(s.WindowTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        }
        else
        {
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }

        _previousNormalBounds = new Rect(Left, Top, Width, Height);
        WindowState = s.WindowState == "Maximized" ? WindowState.Maximized : WindowState.Normal;
        _previousWindowState = WindowState;
    }

    private void ApplyFullScreenWindowState(bool isFullScreen)
    {
        if (isFullScreen)
        {
            _previousWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _previousWindowStyle = WindowStyle;
            _previousResizeMode = ResizeMode;
            if (WindowState == WindowState.Normal)
            {
                _previousNormalBounds = new Rect(Left, Top, Width, Height);
            }

            MaxHeight = double.PositiveInfinity;
            MaxWidth = double.PositiveInfinity;

            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            MaxHeight = workArea.Height + 14;
            MaxWidth = workArea.Width + 14;

            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;

            if (_previousWindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
                if (_previousNormalBounds.Width >= 700 && _previousNormalBounds.Height >= 500)
                {
                    Left = _previousNormalBounds.Left;
                    Top = _previousNormalBounds.Top;
                    Width = _previousNormalBounds.Width;
                    Height = _previousNormalBounds.Height;
                }
            }
        }

        FocusReadingViewport();
    }

    private void SaveReaderWindowGeometry()
    {
        if (_viewModel.IsFullScreen)
        {
            _viewModel.ToggleFullScreenCommand.Execute(null);
        }

        var s = _settingsService.Current;
        s.WindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowLeft = Left;
            s.WindowTop = Top;
        }

        if (_viewModel.IsTocVisible && TocSidebarColumn.ActualWidth >= 180)
        {
            s.TocWidth = Math.Clamp(TocSidebarColumn.ActualWidth, 180, 500);
        }

        if (_viewModel.IsAnnotationsVisible && NotesSidebarColumn.ActualWidth >= 200)
        {
            s.NotesWidth = Math.Clamp(NotesSidebarColumn.ActualWidth, 200, 600);
        }

        _settingsService.Save();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            _scrollDebounceTimer.Stop();
            _viewModel.ActiveTabSwitching -= OnActiveTabSwitching;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.FocusViewportRequested -= FocusReadingViewport;
            _viewModel.FocusSearchBoxRequested -= FocusSearchBox;
            _viewModel.SearchMatchChanged -= OnSearchMatchChanged;
            SaveReaderWindowGeometry();
            await _viewModel.SaveAllTabsProgressAsync();
            _viewModel.Dispose();
        }
        catch
        {
            // Ignore shutdown errors
        }
    }

    // 2. Миттєва навігація за змістом (TOC Click Fix)
    private async void TocItemRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TocItem tocItem } &&
            tocItem.PageNumber > 0 &&
            _viewModel.ActiveTab != null)
        {
            e.Handled = true;
            _lastTocNavigatedPage = tocItem.PageNumber;
            _lastTocNavigationTick = Environment.TickCount64;
            await _viewModel.ActiveTab.NavigateToPagePriorityAsync(tocItem.PageNumber);
            FocusReadingViewport();
        }
    }

    private async void TocTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TocItem tocItem && tocItem.PageNumber > 0 && _viewModel.ActiveTab != null)
        {
            // Prevent duplicate invocation if TocItemRow_MouseLeftButtonUp just handled this click
            if (_lastTocNavigatedPage == tocItem.PageNumber &&
                Environment.TickCount64 - _lastTocNavigationTick < 250)
            {
                return;
            }

            _lastTocNavigatedPage = tocItem.PageNumber;
            _lastTocNavigationTick = Environment.TickCount64;
            await _viewModel.ActiveTab.NavigateToPagePriorityAsync(tocItem.PageNumber);
            FocusReadingViewport();
        }
    }

    // Закладки: навігація за сторінкою
    private async void AnnotationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is AnnotationRecord ann && _viewModel.ActiveTab != null)
        {
            await _viewModel.ActiveTab.NavigateToPagePriorityAsync(ann.PageNumber);
            FocusReadingViewport();
        }
    }

    private async void AnnotationsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            if (FindVisualAncestor<Button>(source) != null)
                return;

            if (FindVisualAncestor<ListBoxItem>(source) is { DataContext: AnnotationRecord ann } &&
                _viewModel.ActiveTab != null)
            {
                await _viewModel.ActiveTab.NavigateToPagePriorityAsync(ann.PageNumber);
                FocusReadingViewport();
            }
        }
    }

    // 3. Посимвольне та посабзацне виділення тексту за вектором PDFium (One-Shot Text Selection)
    private void PdfSelectionOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsTextSelectionMode || !_viewModel.CanSelectText || sender is not Canvas canvas)
            return;

        _isPdfTextSelecting = true;
        _activeSelectionCanvas = canvas;
        _currentDraggedPdfText = string.Empty;
        _pdfSelectStartNorm = GetNormalizedPointOnPdfImage(canvas, e.GetPosition(canvas));
        canvas.CaptureMouse();
        ClearSelectionHighlightRects(canvas);

        e.Handled = true;
    }

    private void PdfSelectionOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPdfTextSelecting || _activeSelectionCanvas == null || sender != _activeSelectionCanvas)
            return;

        if (_viewModel.ActiveTab is not { } tab)
            return;

        Point currentNorm = GetNormalizedPointOnPdfImage(_activeSelectionCanvas, e.GetPosition(_activeSelectionCanvas));
        int pageNumber = _activeSelectionCanvas.Tag is int p && p > 0 ? p : tab.CurrentPage;

        PdfTextSelectionResult selection = tab.PreviewPdfTextSelection(
            pageNumber,
            _pdfSelectStartNorm.X,
            _pdfSelectStartNorm.Y,
            currentNorm.X,
            currentNorm.Y);

        _currentDraggedPdfText = selection.SelectedText;
        RenderSelectionHighlightRects(_activeSelectionCanvas, selection.HighlightRects);
    }

    private void PdfSelectionOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPdfTextSelecting || _activeSelectionCanvas == null)
            return;

        Canvas canvas = _activeSelectionCanvas;
        Point endNorm = GetNormalizedPointOnPdfImage(canvas, e.GetPosition(canvas));

        _isPdfTextSelecting = false;
        _activeSelectionCanvas = null;
        canvas.ReleaseMouseCapture();
        ClearSelectionHighlightRects(canvas);

        int pageNumber = canvas.Tag is int p && p > 0
            ? p
            : (_viewModel.ActiveTab?.CurrentPage ?? 1);

        if (!string.IsNullOrWhiteSpace(_currentDraggedPdfText))
        {
            string selected = _currentDraggedPdfText;
            _currentDraggedPdfText = string.Empty;
            _viewModel.CommitOneShotSelectedText(selected);
        }
        else
        {
            _viewModel.CompletePdfRectSelectionOneShot(
                pageNumber,
                _pdfSelectStartNorm.X,
                _pdfSelectStartNorm.Y,
                endNorm.X,
                endNorm.Y);
        }

        e.Handled = true;
    }

    private static Rect GetRenderedImageRectInCanvas(Canvas canvas)
    {
        double canvasW = Math.Max(1.0, canvas.ActualWidth);
        double canvasH = Math.Max(1.0, canvas.ActualHeight);

        if (canvas.Parent is Panel parentPanel)
        {
            foreach (UIElement child in parentPanel.Children)
            {
                if (child is Image { Source: { } bmp } img && bmp.Width > 0 && bmp.Height > 0 && img.ActualWidth > 0 && img.ActualHeight > 0)
                {
                    Point offset = img.TransformToVisual(canvas).Transform(new Point(0, 0));
                    return new Rect(offset.X, offset.Y, img.ActualWidth, img.ActualHeight);
                }
            }
        }

        return new Rect(0, 0, canvasW, canvasH);
    }

    private static Point GetNormalizedPointOnPdfImage(Canvas canvas, Point canvasPoint)
    {
        Rect imgRect = GetRenderedImageRectInCanvas(canvas);
        double normX = Math.Clamp((canvasPoint.X - imgRect.X) / Math.Max(1.0, imgRect.Width), 0.0, 1.0);
        double normY = Math.Clamp((canvasPoint.Y - imgRect.Y) / Math.Max(1.0, imgRect.Height), 0.0, 1.0);
        return new Point(normX, normY);
    }

    private static void ClearSelectionHighlightRects(Canvas canvas)
    {
        canvas.Children.Clear();
    }

    private void RenderSelectionHighlightRects(Canvas canvas, IReadOnlyList<PdfNormalizedRect> rects)
    {
        canvas.Children.Clear();
        if (rects.Count == 0)
            return;

        Rect imgRect = GetRenderedImageRectInCanvas(canvas);
        Brush fillBrush = new SolidColorBrush(Color.FromArgb(0x52, 0x3B, 0x82, 0xF6));
        fillBrush.Freeze();
        Brush borderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x25, 0x63, 0xEB));
        borderBrush.Freeze();

        foreach (var r in rects)
        {
            double x = imgRect.X + r.X * imgRect.Width;
            double y = imgRect.Y + r.Y * imgRect.Height;
            double w = Math.Max(2.0, r.Width * imgRect.Width);
            double h = Math.Max(4.0, r.Height * imgRect.Height);

            var highlightBar = new Border
            {
                Width = w,
                Height = h,
                Background = fillBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false
            };

            Canvas.SetLeft(highlightBar, x);
            Canvas.SetTop(highlightBar, y);
            canvas.Children.Add(highlightBar);
        }
    }

    private void ReaderTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsTextSelectionMode)
            return;

        if (sender is TextBox tb && !string.IsNullOrWhiteSpace(tb.SelectedText) && tb.SelectedText.Length >= 2)
        {
            var text = tb.SelectedText.Trim();
            _viewModel.NewQuoteInput = text;
        }
    }

    private void ReaderTextBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsTextSelectionMode)
            return;

        if (sender is TextBox tb && !string.IsNullOrWhiteSpace(tb.SelectedText) && tb.SelectedText.Length >= 2)
        {
            string selected = tb.SelectedText.Trim();
            _viewModel.CommitOneShotSelectedText(selected);
        }
    }

    private async void PageJumpTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _viewModel.JumpToInputPageCommand.ExecuteAsync(null);
            FocusReadingViewport();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FocusReadingViewport();
        }
    }

    private void PageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderPreviewText != null)
        {
            SliderPreviewText.Text = $"Стор. {(int)Math.Round(e.NewValue)}";
        }
    }

    private async void PageSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.ActiveTab != null)
        {
            int target = (int)Math.Round(PageSlider.Value);
            await _viewModel.ActiveTab.NavigateToPagePriorityAsync(target);
            FocusReadingViewport();
        }
    }

    private void PdfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (e.Delta > 0)
                _viewModel.ZoomInCommand.Execute(null);
            else
                _viewModel.ZoomOutCommand.Execute(null);
        }
    }

    private void TabListBoxItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Prevent WPF's default MakeVisible from partially clipping tabs;
        // EnsureActiveTabVisibleInStrip handles exact boundary alignment.
        e.Handled = true;
    }

    private void TabStripScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        UpdateTabOverflowIndicators();

    private void TabStripScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureActiveTabVisibleInStrip();
        UpdateTabOverflowIndicators();
    }

    private void TabStripScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabStripScrollViewer.ScrollableWidth <= 0)
            return;

        e.Handled = true;
        TabStripScrollViewer.ScrollToHorizontalOffset(
            Math.Clamp(TabStripScrollViewer.HorizontalOffset - e.Delta, 0, TabStripScrollViewer.ScrollableWidth));
        UpdateTabOverflowIndicators();
    }

    private void MeasureTabOverflowCounts(
        out int hiddenLeft,
        out int hiddenRight,
        out ListBoxItem? nextLeftHiddenItem,
        out ListBoxItem? nextRightHiddenItem)
    {
        hiddenLeft = 0;
        hiddenRight = 0;
        nextLeftHiddenItem = null;
        nextRightHiddenItem = null;

        if (TabStripScrollViewer == null || TabListBox == null || !TabStripScrollViewer.IsLoaded)
            return;

        TabListBox.UpdateLayout();
        TabStripScrollViewer.UpdateLayout();

        double viewportWidth = TabStripScrollViewer.ViewportWidth > 0
            ? TabStripScrollViewer.ViewportWidth
            : TabStripScrollViewer.ActualWidth;

        if (viewportWidth <= 0)
            return;

        // A tab is considered completely visible ONLY when both its Left and Right edges
        // lie wholly within [0, viewportWidth] (within a 1px sub-pixel layout tolerance).
        const double BoundaryTolerancePx = 1.0;

        for (int i = 0; i < _viewModel.OpenTabs.Count; i++)
        {
            if (TabListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item || item.ActualWidth <= 0)
                continue;

            Rect bounds = item.TransformToAncestor(TabStripScrollViewer)
                .TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));

            if (bounds.Left < -BoundaryTolerancePx)
            {
                hiddenLeft++;
                nextLeftHiddenItem = item;
            }

            if (bounds.Right > viewportWidth + BoundaryTolerancePx)
            {
                hiddenRight++;
                nextRightHiddenItem ??= item;
            }
        }
    }

    private void UpdateTabOverflowIndicators()
    {
        if (TabStripScrollViewer == null || TabListBox == null || !TabStripScrollViewer.IsLoaded)
            return;

        MeasureTabOverflowCounts(out int hiddenLeft, out int hiddenRight, out _, out _);

        Visibility desiredLeftVis = hiddenLeft > 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility desiredRightVis = hiddenRight > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (LeftTabOverflowButton.Visibility != desiredLeftVis ||
            RightTabOverflowButton.Visibility != desiredRightVis)
        {
            LeftTabOverflowButton.Visibility = desiredLeftVis;
            RightTabOverflowButton.Visibility = desiredRightVis;
            TabStripScrollViewer.UpdateLayout();

            // Re-measure after overflow button visibility changes TabStripScrollViewer's available width
            MeasureTabOverflowCounts(out hiddenLeft, out hiddenRight, out _, out _);
            LeftTabOverflowButton.Visibility = hiddenLeft > 0 ? Visibility.Visible : Visibility.Collapsed;
            RightTabOverflowButton.Visibility = hiddenRight > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        LeftTabOverflowButton.Content = $"◀ {hiddenLeft}";
        RightTabOverflowButton.Content = $"{hiddenRight} ▶";
    }

    private void EnsureActiveTabVisibleInStrip()
    {
        if (TabStripScrollViewer == null || TabListBox == null || !TabStripScrollViewer.IsLoaded || _viewModel.ActiveTab == null)
            return;

        int activeIndex = _viewModel.OpenTabs.IndexOf(_viewModel.ActiveTab);
        if (activeIndex < 0)
            return;

        TabListBox.UpdateLayout();
        TabStripScrollViewer.UpdateLayout();

        if (TabListBox.ItemContainerGenerator.ContainerFromIndex(activeIndex) is not ListBoxItem activeItem || activeItem.ActualWidth <= 0)
            return;

        for (int pass = 0; pass < 2; pass++)
        {
            double viewportWidth = TabStripScrollViewer.ViewportWidth > 0
                ? TabStripScrollViewer.ViewportWidth
                : TabStripScrollViewer.ActualWidth;

            if (viewportWidth <= 0)
                break;

            Rect bounds = activeItem.TransformToAncestor(TabStripScrollViewer)
                .TransformBounds(new Rect(0, 0, activeItem.ActualWidth, activeItem.ActualHeight));

            if (activeIndex == 0 && bounds.Left < 0)
            {
                TabStripScrollViewer.ScrollToHorizontalOffset(0);
            }
            else if (bounds.Left < -0.5)
            {
                double target = Math.Max(0, TabStripScrollViewer.HorizontalOffset + bounds.Left);
                TabStripScrollViewer.ScrollToHorizontalOffset(target);
            }
            else if (bounds.Right > viewportWidth + 0.5)
            {
                double delta = bounds.Right - viewportWidth + 2.0;
                double target = Math.Clamp(TabStripScrollViewer.HorizontalOffset + delta, 0, TabStripScrollViewer.ScrollableWidth);
                TabStripScrollViewer.ScrollToHorizontalOffset(target);
            }

            UpdateTabOverflowIndicators();
        }
    }

    private async void LeftTabOverflowButton_Click(object sender, RoutedEventArgs e) =>
        await ScrollTabStripToHiddenTabAsync(left: true);

    private async void RightTabOverflowButton_Click(object sender, RoutedEventArgs e) =>
        await ScrollTabStripToHiddenTabAsync(left: false);

    private async Task ScrollTabStripToHiddenTabAsync(bool left)
    {
        if (TabStripScrollViewer == null || TabListBox == null || !TabStripScrollViewer.IsLoaded)
            return;

        MeasureTabOverflowCounts(out _, out _, out ListBoxItem? nextLeftItem, out ListBoxItem? nextRightItem);
        ListBoxItem? targetItem = left ? nextLeftItem : nextRightItem;
        if (targetItem == null)
            return;

        if (!left && LeftTabOverflowButton.Visibility != Visibility.Visible)
        {
            // Scrolling right from 0 will make the left overflow button appear; pre-layout so viewportWidth is exact.
            LeftTabOverflowButton.Visibility = Visibility.Visible;
            TabStripScrollViewer.UpdateLayout();
        }

        double viewportWidth = TabStripScrollViewer.ViewportWidth > 0
            ? TabStripScrollViewer.ViewportWidth
            : TabStripScrollViewer.ActualWidth;

        Rect bounds = targetItem.TransformToAncestor(TabStripScrollViewer)
            .TransformBounds(new Rect(0, 0, targetItem.ActualWidth, targetItem.ActualHeight));

        double from = TabStripScrollViewer.HorizontalOffset;
        double delta = left
            ? bounds.Left
            : (bounds.Right - viewportWidth + 2.0);

        int targetIndex = TabListBox.ItemContainerGenerator.IndexFromContainer(targetItem);
        double to = (left && targetIndex == 0)
            ? 0
            : Math.Clamp(from + delta, 0, TabStripScrollViewer.ScrollableWidth);

        if (Math.Abs(to - from) > 0.5)
        {
            const int steps = 8;
            for (int step = 1; step <= steps; step++)
            {
                TabStripScrollViewer.ScrollToHorizontalOffset(from + (to - from) * step / steps);
                await Task.Delay(10);
            }
        }

        TabStripScrollViewer.UpdateLayout();
        UpdateTabOverflowIndicators();

        // Final micro-snap to guarantee targetItem is 100% inside the post-button-layout viewport
        double finalViewportWidth = TabStripScrollViewer.ViewportWidth > 0
            ? TabStripScrollViewer.ViewportWidth
            : TabStripScrollViewer.ActualWidth;

        Rect finalBounds = targetItem.TransformToAncestor(TabStripScrollViewer)
            .TransformBounds(new Rect(0, 0, targetItem.ActualWidth, targetItem.ActualHeight));

        if (left && finalBounds.Left < -0.5)
        {
            TabStripScrollViewer.ScrollToHorizontalOffset(
                targetIndex == 0 ? 0 : Math.Max(0, TabStripScrollViewer.HorizontalOffset + finalBounds.Left));
            UpdateTabOverflowIndicators();
        }
        else if (!left && finalBounds.Right > finalViewportWidth + 0.5)
        {
            double extra = finalBounds.Right - finalViewportWidth + 2.0;
            TabStripScrollViewer.ScrollToHorizontalOffset(
                Math.Clamp(TabStripScrollViewer.HorizontalOffset + extra, 0, TabStripScrollViewer.ScrollableWidth));
            UpdateTabOverflowIndicators();
        }
    }

    // 1 & 4. Гарантоване відображення видимих сторінок за реально згенерованими контейнерами VirtualizingStackPanel
    private void ContinuousListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isProgrammaticScroll || Environment.TickCount64 < _suppressScrollChangedUntilTick)
            return;

        if (sender is not ListBox listBox || listBox.DataContext is not DocumentTabViewModel tab)
            return;

        if (!ReferenceEquals(tab, _viewModel.ActiveTab) ||
            tab.IsLoading ||
            tab.IsBackgroundUnloaded ||
            tab.IsResumingFromBackground)
        {
            return;
        }

        // Ignore pure layout/extent updates where the user did not scroll vertically
        if (Math.Abs(e.VerticalChange) < 0.5)
        {
            return;
        }

        if (e.OriginalSource is ScrollViewer sv)
        {
            tab.SavedScrollOffset = sv.VerticalOffset;
        }

        var (_, _, dominantPage) = GetActuallyVisiblePageRange(listBox, tab, fallbackPage: tab.CurrentPage);
        if (dominantPage >= 1 && dominantPage <= tab.TotalPages)
        {
            tab.CurrentPage = dominantPage;
            tab.PageJumpInput = dominantPage.ToString();

            if (dominantPage <= tab.ContinuousWindowPages.Count &&
                FindRealizedContainerForItem(listBox, tab.ContinuousWindowPages[dominantPage - 1]) is { } domContainer)
            {
                double itemTop = domContainer.TransformToAncestor(listBox).Transform(new Point(0, 0)).Y;
                tab.SavedPageRelativeOffset = Math.Max(0, -itemTop);
            }
        }

        _pendingScrollListBox = listBox;
        _pendingScrollTab = tab;
        _pendingVerticalChange = e.VerticalChange;

        _scrollDebounceTimer.Stop();
        _scrollDebounceTimer.Start();
    }

    private void OnScrollDebounceTimerTick(object? sender, EventArgs e)
    {
        _scrollDebounceTimer.Stop();

        if (_isProgrammaticScroll ||
            Environment.TickCount64 < _suppressScrollChangedUntilTick ||
            _pendingScrollListBox is not { } listBox ||
            _pendingScrollTab is not { } tab ||
            !ReferenceEquals(tab, _viewModel.ActiveTab) ||
            tab.IsLoading ||
            tab.IsBackgroundUnloaded ||
            tab.IsResumingFromBackground)
        {
            return;
        }

        if (FindVisualChild<ScrollViewer>(listBox) is { } scrollViewer)
        {
            tab.SavedScrollOffset = scrollViewer.VerticalOffset;
        }

        var (firstVisible, lastVisible, dominantPage) = GetActuallyVisiblePageRange(listBox, tab, fallbackPage: tab.CurrentPage);
        if (dominantPage >= 1 && dominantPage <= tab.ContinuousWindowPages.Count &&
            FindRealizedContainerForItem(listBox, tab.ContinuousWindowPages[dominantPage - 1]) is { } domContainer)
        {
            double itemTop = domContainer.TransformToAncestor(listBox).Transform(new Point(0, 0)).Y;
            tab.SavedPageRelativeOffset = Math.Max(0, -itemTop);
        }

        bool isActiveUserScroll = Math.Abs(_pendingVerticalChange) > 2.5;
        _ = tab.RenderContinuousPagesAsync(firstVisible, lastVisible, isActiveScrollEvent: isActiveUserScroll, dominantPage: dominantPage);
    }

    /// <summary>
    /// Determines the exact visible page range by inspecting the realized ListBoxItem containers
    /// inside VirtualizingStackPanel rather than relying on non-linear ScrollViewer.ExtentHeight estimates.
    /// </summary>
    private static (int FirstVisible, int LastVisible, int DominantPage) GetActuallyVisiblePageRange(
        ListBox listBox,
        DocumentTabViewModel tab,
        int fallbackPage)
    {
        double viewportHeight = listBox.ActualHeight;
        if (viewportHeight <= 10 || tab.TotalPages <= 0)
        {
            int clamped = Math.Clamp(fallbackPage, 1, Math.Max(1, tab.TotalPages));
            return (clamped, Math.Min(tab.TotalPages, clamped + 1), clamped);
        }

        var panel = FindVisualChild<VirtualizingStackPanel>(listBox);
        if (panel != null && panel.Children.Count > 0)
        {
            int minPage = int.MaxValue;
            int maxPage = int.MinValue;
            int dominantPage = fallbackPage;
            double bestVisibleOverlap = -1;

            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not ListBoxItem { DataContext: ContinuousPageViewModel pageVm } item ||
                    item.ActualHeight <= 0)
                {
                    continue;
                }

                Point topPt = item.TransformToAncestor(listBox).Transform(new Point(0, 0));
                double itemTop = topPt.Y;
                double itemBottom = itemTop + item.ActualHeight;

                // Consider item visible if it intersects the viewport (with 24px margin)
                if (itemBottom > 8 && itemTop < viewportHeight - 8)
                {
                    if (pageVm.PageNumber < minPage) minPage = pageVm.PageNumber;
                    if (pageVm.PageNumber > maxPage) maxPage = pageVm.PageNumber;

                    double overlap = Math.Min(itemBottom, viewportHeight) - Math.Max(itemTop, 0);
                    if (overlap > bestVisibleOverlap)
                    {
                        bestVisibleOverlap = overlap;
                        dominantPage = pageVm.PageNumber;
                    }
                }
            }

            if (minPage <= maxPage && minPage >= 1 && maxPage <= tab.TotalPages)
            {
                return (minPage, maxPage, Math.Clamp(dominantPage, minPage, maxPage));
            }
        }

        int safeFallback = Math.Clamp(fallbackPage, 1, Math.Max(1, tab.TotalPages));
        return (safeFallback, Math.Min(tab.TotalPages, safeFallback + 1), safeFallback);
    }

    private static ListBoxItem? FindRealizedContainerForItem(ListBox listBox, object targetDataItem)
    {
        if (listBox.ItemContainerGenerator.ContainerFromItem(targetDataItem) is ListBoxItem direct)
            return direct;

        var panel = FindVisualChild<VirtualizingStackPanel>(listBox);
        if (panel != null)
        {
            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is ListBoxItem item && ReferenceEquals(item.DataContext, targetDataItem))
                    return item;
            }
        }

        return null;
    }

    private void PagedScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isProgrammaticScroll || Environment.TickCount64 < _suppressScrollChangedUntilTick)
            return;

        if (sender is ScrollViewer scrollViewer &&
            scrollViewer.DataContext is DocumentTabViewModel tab &&
            ReferenceEquals(tab, _viewModel.ActiveTab) &&
            !tab.IsLoading &&
            !tab.IsBackgroundUnloaded &&
            !tab.IsResumingFromBackground &&
            Math.Abs(e.VerticalChange) >= 0.5)
        {
            tab.SavedScrollOffset = scrollViewer.VerticalOffset;
        }
    }

    private void RestoreActiveTabScrollPosition()
    {
        if (_viewModel.ActiveTab is not { } tab)
            return;

        ScrollToPageDeterministic(tab, tab.CurrentPage, tab.SavedScrollOffset, restoreSubPageOffset: true);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }

        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;

            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                current = VisualTreeHelper.GetParent(current);
            else if (current is FrameworkContentElement fce)
                current = fce.Parent;
            else
                break;
        }

        return null;
    }

    private static bool IsEditableInputFocused() =>
        Keyboard.FocusedElement is TextBox { IsReadOnly: false };

    private ScrollViewer? GetActiveReadingScrollViewer()
    {
        if (_viewModel.ActiveTab is not { } tab)
            return null;

        if (tab.IsContinuousVerticalMode)
        {
            ListBox listBox = tab.IsPdfMode ? PdfContinuousListBox : TextContinuousListBox;
            return FindVisualChild<ScrollViewer>(listBox);
        }

        return tab.IsPdfMode ? PdfScrollViewer : TextScrollViewer;
    }

    private FrameworkElement GetActiveReadingControl()
    {
        if (_viewModel.ActiveTab is { } tab)
        {
            if (tab.IsContinuousVerticalMode)
            {
                return tab.IsPdfMode ? PdfContinuousListBox : TextContinuousListBox;
            }

            return tab.IsPdfMode ? PdfScrollViewer : TextScrollViewer;
        }

        return PrimaryViewportHost;
    }

    public void FocusReadingViewport()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusReadingViewport));
            return;
        }

        Keyboard.ClearFocus();
        FrameworkElement target = GetActiveReadingControl();
        target.Focus();
        Keyboard.Focus(target);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            FrameworkElement deferredTarget = GetActiveReadingControl();
            deferredTarget.Focus();
            Keyboard.Focus(deferredTarget);
        }));
    }

    private void ReadingViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsEditableInputFocused())
        {
            Keyboard.ClearFocus();
            FrameworkElement target = GetActiveReadingControl();
            target.Focus();
            Keyboard.Focus(target);
        }
        else if (Keyboard.FocusedElement is not ScrollViewer and not ListBox)
        {
            FrameworkElement target = GetActiveReadingControl();
            target.Focus();
            Keyboard.Focus(target);
        }
    }

    private void ReadingViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        FocusReadingViewport();
    }

    private async Task ScrollOrStepVerticalAsync(double deltaPixels)
    {
        if (_viewModel.ActiveTab is not { } tab)
            return;

        ScrollViewer? sv = GetActiveReadingScrollViewer();
        if (sv != null && sv.ScrollableHeight > 1.0)
        {
            double current = sv.VerticalOffset;
            double target = Math.Clamp(current + deltaPixels, 0.0, sv.ScrollableHeight);
            if (Math.Abs(target - current) > 0.5)
            {
                sv.ScrollToVerticalOffset(target);
                return;
            }
        }

        if (!tab.IsContinuousVerticalMode)
        {
            if (deltaPixels > 0)
                await _viewModel.NextPageCommand.ExecuteAsync(null);
            else if (deltaPixels < 0)
                await _viewModel.PreviousPageCommand.ExecuteAsync(null);
        }
    }

    public void FocusSearchBox()
    {
        if (!_viewModel.CanSelectText)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusSearchBox));
            return;
        }

        if (_viewModel.IsFullScreen)
        {
            _viewModel.ToggleFullScreenCommand.Execute(null);
        }

        if (DocumentSearchTextBox != null && DocumentSearchTextBox.IsEnabled)
        {
            DocumentSearchTextBox.Focus();
            Keyboard.Focus(DocumentSearchTextBox);
            DocumentSearchTextBox.SelectAll();
        }
    }

    private async void DocumentSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                await _viewModel.PreviousSearchMatchCommand.ExecuteAsync(null);
            }
            else
            {
                await _viewModel.NextSearchMatchCommand.ExecuteAsync(null);
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(_viewModel.SearchQuery))
            {
                _viewModel.ClearSearchCommand.Execute(null);
            }
            FocusReadingViewport();
        }
    }

    private void PdfSearchHighlightCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Canvas canvas)
        {
            _loadedSearchHighlightCanvases.Add(canvas);
            RenderSearchHighlightsForCanvas(canvas);
        }
    }

    private void PdfSearchHighlightCanvas_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Canvas canvas)
        {
            _loadedSearchHighlightCanvases.Remove(canvas);
            canvas.Children.Clear();
        }
    }

    private void PdfSearchHighlightCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Canvas canvas)
        {
            RenderSearchHighlightsForCanvas(canvas);
        }
    }

    private void PdfPageImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Image { Parent: Panel parentPanel })
        {
            foreach (UIElement child in parentPanel.Children)
            {
                if (child is Canvas canvas && _loadedSearchHighlightCanvases.Contains(canvas))
                {
                    RenderSearchHighlightsForCanvas(canvas);
                }
            }
        }
    }

    private void RefreshAllSearchHighlightCanvases()
    {
        foreach (Canvas canvas in _loadedSearchHighlightCanvases.ToArray())
        {
            if (canvas.IsLoaded)
            {
                RenderSearchHighlightsForCanvas(canvas);
            }
            else
            {
                _loadedSearchHighlightCanvases.Remove(canvas);
            }
        }
    }

    private void RenderSearchHighlightsForCanvas(Canvas canvas)
    {
        canvas.Children.Clear();

        if (!canvas.IsLoaded ||
            _viewModel.ActiveTab is not { } tab ||
            _viewModel.AllSearchMatches.Count == 0)
        {
            return;
        }

        int pageNumber = canvas.Tag is int p && p > 0 ? p : tab.CurrentPage;
        Rect imgRect = GetRenderedImageRectInCanvas(canvas);
        if (imgRect.Width <= 4.0 || imgRect.Height <= 4.0)
            return;

        DocumentSearchMatch? activeMatch = _viewModel.ActiveSearchMatch;

        Brush passiveFill = new SolidColorBrush(Color.FromArgb(0x64, 0xFA, 0xCC, 0x15));
        passiveFill.Freeze();
        Brush passiveBorder = new SolidColorBrush(Color.FromArgb(0xB0, 0xCA, 0x8A, 0x04));
        passiveBorder.Freeze();

        Brush activeFill = new SolidColorBrush(Color.FromArgb(0x88, 0xF9, 0x73, 0x16));
        activeFill.Freeze();
        Brush activeBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0xEA, 0x58, 0x0C));
        activeBorder.Freeze();

        foreach (DocumentSearchMatch match in _viewModel.AllSearchMatches)
        {
            if (match.PageNumber != pageNumber || match.HighlightRects.Count == 0)
                continue;

            bool isActive = activeMatch != null &&
                            activeMatch.PageNumber == match.PageNumber &&
                            activeMatch.CharIndex == match.CharIndex;

            double padX = isActive ? 2.5 : 1.0;
            double padY = isActive ? 2.0 : 1.0;

            foreach (PdfNormalizedRect r in match.HighlightRects)
            {
                double x = imgRect.X + r.X * imgRect.Width - padX;
                double y = imgRect.Y + r.Y * imgRect.Height - padY;
                double w = Math.Max(4.0, r.Width * imgRect.Width + padX * 2.0);
                double h = Math.Max(6.0, r.Height * imgRect.Height + padY * 2.0);

                var highlightBox = new Border
                {
                    Width = w,
                    Height = h,
                    Background = isActive ? activeFill : passiveFill,
                    BorderBrush = isActive ? activeBorder : passiveBorder,
                    BorderThickness = new Thickness(isActive ? 1.5 : 0.75),
                    CornerRadius = new CornerRadius(2.5),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(highlightBox, x);
                Canvas.SetTop(highlightBox, y);
                canvas.Children.Add(highlightBox);
            }
        }
    }

    private void OnSearchMatchChanged(DocumentSearchMatch? match)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => OnSearchMatchChanged(match)));
            return;
        }

        RefreshAllSearchHighlightCanvases();

        if (match == null || _viewModel.ActiveTab is not { } tab)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_viewModel.ActiveTab != tab || _viewModel.ActiveSearchMatch != match)
                return;

            RefreshAllSearchHighlightCanvases();
            ScrollAndHighlightActiveSearchMatch(tab, match);
        }));
    }

    private void ScrollAndHighlightActiveSearchMatch(DocumentTabViewModel tab, DocumentSearchMatch match)
    {
        if (tab.IsPdfMode)
        {
            if (tab.IsContinuousVerticalMode)
            {
                if (match.PageNumber >= 1 &&
                    match.PageNumber <= tab.ContinuousWindowPages.Count &&
                    FindVisualChild<ScrollViewer>(PdfContinuousListBox) is { } sv)
                {
                    var pageItem = tab.ContinuousWindowPages[match.PageNumber - 1];
                    if (FindRealizedContainerForItem(PdfContinuousListBox, pageItem) is { } container &&
                        container.ActualHeight > 20)
                    {
                        double itemTop = container.TransformToAncestor(PdfContinuousListBox).Transform(new Point(0, 0)).Y;
                        double matchYInViewport = itemTop + match.PrimaryNormY * container.ActualHeight;
                        double viewportH = Math.Max(120.0, PdfContinuousListBox.ActualHeight);

                        if (matchYInViewport < 48.0 || matchYInViewport > viewportH - 72.0)
                        {
                            double desiredOffset = Math.Clamp(
                                sv.VerticalOffset + matchYInViewport - viewportH * 0.32,
                                0.0,
                                sv.ScrollableHeight);
                            sv.ScrollToVerticalOffset(desiredOffset);
                            PdfContinuousListBox.UpdateLayout();
                            RefreshAllSearchHighlightCanvases();
                        }
                    }
                }
            }
            else if (PdfScrollViewer.ScrollableHeight > 8.0)
            {
                double desiredOffset = Math.Clamp(
                    match.PrimaryNormY * PdfScrollViewer.ExtentHeight - PdfScrollViewer.ViewportHeight * 0.32,
                    0.0,
                    PdfScrollViewer.ScrollableHeight);
                PdfScrollViewer.ScrollToVerticalOffset(desiredOffset);
            }
        }
        else
        {
            // Non-PDF text mode: select the matched character span inside the active page's TextBox
            TextBox? targetTextBox = null;
            if (tab.IsContinuousVerticalMode)
            {
                if (match.PageNumber >= 1 && match.PageNumber <= tab.ContinuousWindowPages.Count)
                {
                    var pageItem = tab.ContinuousWindowPages[match.PageNumber - 1];
                    if (FindRealizedContainerForItem(TextContinuousListBox, pageItem) is { } container)
                    {
                        targetTextBox = FindVisualChild<TextBox>(container);
                    }
                }
            }
            else
            {
                targetTextBox = FindVisualChild<TextBox>(TextScrollViewer);
            }

            if (targetTextBox != null &&
                match.CharIndex >= 0 &&
                match.CharIndex + match.CharLength <= targetTextBox.Text.Length)
            {
                targetTextBox.Select(match.CharIndex, match.CharLength);
            }
        }
    }

    private void OpenSettingsWindowButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    private void OpenAboutWindowButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAboutWindow();
    }

    private void OpenSettingsWindow()
    {
        var settingsWin = new SettingsWindow(_viewModel)
        {
            Owner = this
        };
        settingsWin.ShowDialog();
        FocusReadingViewport();
    }

    private void OpenAboutWindow()
    {
        var aboutWin = new AboutWindow
        {
            Owner = this
        };
        aboutWin.ShowDialog();
        FocusReadingViewport();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            e.Handled = true;
            OpenAboutWindow();
            return;
        }

        if (e.Key == Key.F2)
        {
            e.Handled = true;
            OpenSettingsWindow();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            FocusSearchBox();
            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            if (_viewModel.CanSelectText && _viewModel.SearchTotalMatches > 0)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    await _viewModel.PreviousSearchMatchCommand.ExecuteAsync(null);
                else
                    await _viewModel.NextSearchMatchCommand.ExecuteAsync(null);
            }
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            await _viewModel.OpenAnotherFileInTabCommand.ExecuteAsync(null);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            e.Handled = true;
            await _viewModel.CloseTabCommand.ExecuteAsync(_viewModel.ActiveTab);
            return;
        }

        // Only allow typing in editable TextBoxes (Quote, Comment, PageJump); Escape leaves the input box
        if (IsEditableInputFocused())
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                FocusReadingViewport();
            }
            return;
        }

        if (e.Key is Key.S or Key.V ||
            (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S))
        {
            e.Handled = true;
            if (_viewModel.CanSelectText)
            {
                _viewModel.ToggleTextSelectionMode();
            }
            else
            {
                _viewModel.IsTextSelectionMode = false;
            }
            return;
        }

        if (e.Key is Key.Escape && _viewModel.IsTextSelectionMode)
        {
            e.Handled = true;
            _viewModel.IsTextSelectionMode = false;
            return;
        }

        if (e.Key is Key.F11 or Key.F)
        {
            e.Handled = true;
            _viewModel.ToggleFullScreenCommand.Execute(null);
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsFullScreen)
        {
            e.Handled = true;
            _viewModel.ToggleFullScreenCommand.Execute(null);
            return;
        }

        // Priority keyboard navigation for reading regardless of intermediate non-editable focus
        if (e.Key == Key.Down)
        {
            e.Handled = true;
            await ScrollOrStepVerticalAsync(84.0);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            await ScrollOrStepVerticalAsync(-84.0);
        }
        else if (e.Key == Key.Space)
        {
            e.Handled = true;
            double viewportStep = Math.Max(260.0, PrimaryViewportHost.ActualHeight * 0.85);
            double direction = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1.0 : 1.0;
            await ScrollOrStepVerticalAsync(direction * viewportStep);
        }
        else if (e.Key is Key.Right or Key.PageDown)
        {
            e.Handled = true;
            await _viewModel.NextPageCommand.ExecuteAsync(null);
        }
        else if (e.Key is Key.Left or Key.PageUp)
        {
            e.Handled = true;
            await _viewModel.PreviousPageCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Home)
        {
            e.Handled = true;
            await _viewModel.FirstPageCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.End)
        {
            e.Handled = true;
            await _viewModel.LastPageCommand.ExecuteAsync(null);
        }
    }
}
