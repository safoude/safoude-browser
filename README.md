# Safoude

A lightweight, custom borderless desktop web browser for Windows written in C# (WinForms). It is built on top of the **WebView2 SDK DLLs** (`Microsoft.Web.WebView2.Core.dll` and `Microsoft.Web.WebView2.WinForms.dll`) and relies lightly on the system **Microsoft Edge WebView2 Runtime** for modern web rendering.

---

## Key Features

- **WebView2 DLL-Based Architecture:** Driven by the `Microsoft.Web.WebView2` libraries (`Core` & `WinForms`) with a minimal dependency on the Windows Edge WebView2 Runtime. Includes an automatic fallback to a legacy built-in compatibility mode (`WebBrowser`) if WebView2 is disabled or unavailable.
- **Custom Borderless UI:** Frameless window with custom-rendered tabs, smooth loading spinners, rounded address bar, security indicators, and full High-DPI scaling support.
- **Built-in System Pages (`safoude://`):** All internal pages are embedded directly into the executable and served from memory without external HTML files:
  - `safoude://newtab` — Customizable start page with live clock, animated particle background, quick-access bookmark tiles, and a one-click **AI Mode** button.
  - `safoude://settings` — Configuration panel for language, region, search engine, appearance, and bookmarks.
  - `safoude://downloads` — Built-in download manager supporting pause, resume, cancel, file launch, and folder reveal.
  - `safoude://subscription` — Unlocks extended customization (up to 40 bookmarks, custom accent colors, wallpaper blur, and background dimming).
- **Single-Instance & File Handling:** Uses a `Mutex` and `NamedPipeServerStream` so opening external links or local HTML/media files via Safoude opens them as new tabs in the already running window.
- **System Tray & Fullscreen Mode:** Minimize/hide to the system tray and switch to distraction-free fullscreen (`F11` or the focus button in the tab bar).
- **Multi-Language Support:** Built-in localization for 10 languages (`English`, `Русский`, `Українська`, `Deutsch`, `Français`, `Español`, `Italiano`, `Português`, `Türkçe`, `中文`).
- **8 Search Engines:** Out-of-the-box support for Google, Bing, DuckDuckGo, Yandex, Brave Search, Ecosia, Yahoo, and Startpage.

---

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl + T` | Open a new tab |
| `Ctrl + W` | Close the current tab |
| `Ctrl + Tab` / `Ctrl + Shift + Tab` | Switch to the next / previous tab |
| `Ctrl + L` | Focus and select the address bar |
| `Ctrl + D` | Add or remove current page from bookmarks |
| `Ctrl + J` | Open the Downloads tab |
| `F5` / `Ctrl + R` | Reload the current page |
| `Alt + Left` / `Alt + Right` | Navigate back / forward |
| `F11` | Toggle fullscreen mode |
| `Esc` | Exit manual fullscreen mode |

---

## Configuration (`config.ini`)

Safoude requires a `config.ini` file located in the same directory as the executable to verify version integrity on startup:

```ini
name="Safoude"
version="1"
packversion="1"
has_webview="true"
```

- `has_webview`: Set to `"true"` to run using the WebView2 DLLs, or `"false"` to force compatibility mode (removing the Edge Runtime dependency).

### User Data Location
Settings, bookmarks, wallpapers, and WebView2 profile data are stored in:
```text
%APPDATA%\Safoude\
├── settings.json
└── WebView2Data\
```

---

## Building from Source

### Prerequisites
- Windows 10 / 11
- Visual Studio (with **.NET desktop development** workload) or MSBuild
- .NET Framework 4.7.2+ (uses `System.Web.Script.Serialization`)
- **WebView2 Libraries:** `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`, and native `WebView2Loader.dll` (via the [Microsoft.Web.WebView2](https://nuget.org) NuGet package)
- [Microsoft Edge WebView2 Runtime](https://microsoft.com) (pre-installed on Windows 11 and most Windows 10 systems)

### Build Steps
1. Create a new **Windows Forms App (.NET Framework)** project in Visual Studio.
2. Replace the default source code with the `SafoudeApp` source file.
3. Add a reference to `System.Web.Extensions` (required for `JavaScriptSerializer`).
4. Install the `Microsoft.Web.WebView2` NuGet package (or reference `Microsoft.Web.WebView2.Core.dll` and `Microsoft.Web.WebView2.WinForms.dll` directly).
5. Build the solution in `Release` mode and ensure the WebView2 DLLs, `config.ini`, and `assets\app-icon.ico` are placed next to `Safoude.exe`.
