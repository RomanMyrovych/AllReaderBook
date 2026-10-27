using System.IO;
using System.Text;
using System.Windows;
using BookHub.Core.Data;
using BookHub.Core.Services;

namespace BookHub.Reader.Views;

public partial class FirstRunLicenseWindow : Window
{
    private readonly ReaderSettingsService _settingsService;

    public bool IsAccepted { get; private set; }

    private const string EmbeddedMitLicense = """
        MIT License

        Copyright (c) 2026 Roman Myrovych

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    public FirstRunLicenseWindow(ReaderSettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        AgreeButton.IsEnabled = false;
        LoadLicenseText();
    }

    private void LoadLicenseText()
    {
        var sb = new StringBuilder();
        string licensePath = LibraryDatabase.ResolveResourceFile("LICENSE");

        if (File.Exists(licensePath))
        {
            sb.AppendLine(File.ReadAllText(licensePath, Encoding.UTF8).Trim());
        }
        else
        {
            sb.AppendLine(EmbeddedMitLicense.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("----------------------------------------------------------------------------");
        sb.AppendLine("ПОВІДОМЛЕННЯ ПРО ГАРАНТІЇ (AS IS) ТА ВІДСУТНІСТЬ ЗБОРУ ДАНИХ:");
        sb.AppendLine("----------------------------------------------------------------------------");
        sb.AppendLine("1. Реліз AllReaderBook v0.9.0-beta (Public Beta) надається «ЯК Є» («AS IS»),");
        sb.AppendLine("   без жодних явних чи непрямих гарантій придатності або безпомилкової роботи.");
        sb.AppendLine("2. Програма працює на 100% автономно (офлайн): не збирає персональні дані,");
        sb.AppendLine("   не відправляє телеметрію та не виконує прихованих мережевих запитів.");
        sb.AppendLine("3. Усі користувацькі налаштування та історія читання зберігаються виключно");
        sb.AppendLine("   локально на вашому пристрої у файлі allreaderbook_settings.json.");

        LicenseTextBox.Text = sb.ToString();
    }

    private void AcceptCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (AgreeButton != null && AcceptCheckBox != null)
        {
            AgreeButton.IsEnabled = AcceptCheckBox.IsChecked == true;
        }
    }

    private void AgreeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AcceptCheckBox.IsChecked != true)
            return;

        _settingsService.Current.IsLicenseAccepted = true;
        _settingsService.Save();
        IsAccepted = true;
        DialogResult = true;
        Close();
    }

    private void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        IsAccepted = false;
        Application.Current.Shutdown();
    }
}
