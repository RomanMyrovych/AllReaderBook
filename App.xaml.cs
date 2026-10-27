using System.Windows;
using BookHub.Core.Services;
using BookHub.Reader.Services;
using BookHub.Reader.ViewModels;
using BookHub.Reader.Views;

namespace BookHub.Reader;

public partial class App : Application
{
    private SingleInstanceIpcService? _ipcService;
    private ReaderMainWindow? _mainWindow;
    private ReaderViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var launchRequest = ReaderCliParser.Parse(e.Args);
        _ipcService = new SingleInstanceIpcService();

        // Single-Instance Mode: if another AllReaderBook.exe is already running, forward CLI args over NamedPipe and exit
        if (!_ipcService.TryAcquirePrimaryInstance())
        {
            if (SingleInstanceIpcService.TryForwardToPrimaryInstance(launchRequest))
            {
                Shutdown(0);
                return;
            }
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settingsService = new ReaderSettingsService();
        ReaderThemeService.ApplyTheme(settingsService.Current.ThemeName);
        ReaderLocalizationService.ApplyLanguage(settingsService.Current.Language);

        // First-Run License Check: block workspace until MIT License (AS IS) is explicitly accepted
        if (!settingsService.Current.IsLicenseAccepted)
        {
            var licenseWindow = new FirstRunLicenseWindow(settingsService);
            bool? dialogResult = licenseWindow.ShowDialog();
            if (dialogResult != true || !licenseWindow.IsAccepted || !settingsService.Current.IsLicenseAccepted)
            {
                Shutdown(0);
                return;
            }
        }

        _viewModel = new ReaderViewModel(settingsService);
        _mainWindow = new ReaderMainWindow(_viewModel, settingsService, launchRequest);
        MainWindow = _mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _mainWindow.Show();

        // Listen for subsequent file launches from Windows Explorer or BookHub Pro Cataloger
        _ipcService.StartListening(incomingRequest =>
        {
            Dispatcher.BeginInvoke(async () =>
            {
                if (_mainWindow == null || _viewModel == null)
                    return;

                _mainWindow.BringToForeground();
                if (incomingRequest.HasFile)
                {
                    await _viewModel.OpenLaunchRequestAsync(incomingRequest);
                    _mainWindow.OnExternalDocumentOpened();
                }
            });
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipcService?.Dispose();
        base.OnExit(e);
    }
}
