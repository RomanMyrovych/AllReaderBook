# 📖 AllReaderBook v0.9.02 (Public Beta)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%20x64-0078D6.svg)](#системні-вимоги)
[![Runtime: .NET 9](https://img.shields.io/badge/.NET-9.0%20WPF-512BD4.svg)](#збірка-з-вихідного-коду)
[![Package: Winget](https://img.shields.io/badge/Winget-RomanMyrovych.AllReaderBook-blue.svg)](#встановлення-через-winget)

**AllReaderBook v0.9.02-beta** — надшвидкий, легкий і 100% офлайн-рідер для Windows (`win-x64`). Побудований на базі .NET 9 WPF та нативного рушія Google PDFium із нульовою телеметрією. Підтримує перегляд PDF, EPUB, FB2, MOBI, DOCX, CBZ, DJVU з миттєвим рендерингом сторінок.

---

## 📦 Варіанти дистрибутивів

1. **Автономний інсталятор:** `AllReaderBook_Setup_v0.9.02-beta_x64.exe` (Inno Setup, підтримка тихих ключів `/VERYSILENT /NORESTART`, асоціація типів файлів).
2. **Портативна збірка:** `AllReaderBook_Portable_v0.9.02-beta_win-x64.zip` (повністю автономний запуск без встановлення в систему).

### Встановлення через Windows Package Manager (Winget):
```powershell
winget install RomanMyrovych.AllReaderBook
