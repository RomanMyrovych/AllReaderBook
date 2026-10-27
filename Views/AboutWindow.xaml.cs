using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Navigation;
using BookHub.Core.Data;

namespace BookHub.Reader.Views;

public partial class AboutWindow : Window
{
    private readonly string _licensePath;
    private readonly string _thirdPartyPath;
    private readonly string _privacyPath;

    private const string FallbackMitText = """
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

    private const string FallbackPrivacyText = """
        Політика приватності — AllReaderBook v0.9.0-beta (Public Beta)

        1. Робота на 100% локально (Офлайн)
           AllReaderBook є повністю автономною настільною програмою. Програма не потребує підключення до Інтернету.

        2. Відсутність збору персональних даних та телеметрії
           • Програма НЕ збирає та НЕ передає жодних персональних даних користувача.
           • Програма НЕ містить модулів аналітики, трекерів чи рекламних SDK.
           • Програма НЕ виконує жодних зовнішніх мережевих запитів.

        3. Локальне зберігання налаштувань
           Усі налаштування (обрана мова, тема, розмір вікна, список останніх 10 відкритих книг та нотатки)
           зберігаються виключно локально у файлі allreaderbook_settings.json поруч із виконуваним файлом програми.
        """;

    public AboutWindow()
    {
        InitializeComponent();
        _licensePath = LibraryDatabase.ResolveResourceFile("LICENSE");
        _thirdPartyPath = LibraryDatabase.ResolveResourceFile("THIRD_PARTY_LICENSES.txt");
        _privacyPath = LibraryDatabase.ResolveResourceFile("PRIVACY.md");

        ShowMitLicense();
    }

    private void ShowMitLicense()
    {
        string text = File.Exists(_licensePath)
            ? File.ReadAllText(_licensePath, Encoding.UTF8)
            : FallbackMitText;

        DocumentViewerTextBox.Text = text.Trim();
        DocumentViewerTextBox.ScrollToHome();
    }

    private void ShowThirdPartyLicenses()
    {
        if (File.Exists(_thirdPartyPath))
        {
            DocumentViewerTextBox.Text = File.ReadAllText(_thirdPartyPath, Encoding.UTF8).Trim();
        }
        else
        {
            DocumentViewerTextBox.Text =
                "THIRD_PARTY_LICENSES.txt:\n\n" +
                "1. PDFium (bblanchon.PDFium.Win32) — BSD 3-Clause License (Copyright 2014 The PDFium Authors / Google Inc.)\n" +
                "2. VersOne.Epub — MIT License\n" +
                "3. .NET Runtime, WPF & CommunityToolkit.Mvvm — MIT License (Copyright (c) .NET Foundation and Contributors)\n" +
                "4. SQLite / Microsoft.Data.Sqlite — Public Domain / Apache License 2.0\n" +
                "5. UI Fonts & Icons — SIL Open Font License 1.1 / MIT License";
        }

        DocumentViewerTextBox.ScrollToHome();
    }

    private void ShowPrivacyPolicy()
    {
        string text = File.Exists(_privacyPath)
            ? File.ReadAllText(_privacyPath, Encoding.UTF8)
            : FallbackPrivacyText;

        DocumentViewerTextBox.Text = text.Trim();
        DocumentViewerTextBox.ScrollToHome();
    }

    private void ShowMitLicenseButton_Click(object sender, RoutedEventArgs e) => ShowMitLicense();

    private void ShowThirdPartyButton_Click(object sender, RoutedEventArgs e) => ShowThirdPartyLicenses();

    private void ShowPrivacyButton_Click(object sender, RoutedEventArgs e) => ShowPrivacyPolicy();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            // Ignore shell launch error if no mail client or browser is configured
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
