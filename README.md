# ⚡ CyberpunkTerminalWidget

[![.NET](https://img.shields.io/badge/.NET-8.0--windows-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/UI-WPF-0078D4?logo=windows&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-00ADEF?logo=windows11&logoColor=white)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

A modern, lightweight, frameless desktop terminal widget for Windows CLI and PowerShell with retro-futuristic cyberpunk CRT visual effects, dual independent transparency controls, and dynamic background image scaling.

Built with the exact design DNA and visual styling of **`CyberpunkSlideshowWidget`**.

---

## ✨ Key Features

- **Borderless & Draggable HUD:** A chrome-free, transparent terminal console that floats seamlessly over your desktop wallpaper or open applications. Drag it from anywhere or grab the bottom-right grip to resize.
- **Dual Transparency Sliders:**
  - **Window Opacity (0% – 100%):** Independently dims or completely removes the window backdrop, glass border, and background image. Setting this to 0% produces a completely invisible window container.
  - **Font Opacity (10% – 100%):** Controls the terminal text foreground opacity independently.
  - **Floating Text Mode:** Set Window Opacity to 0% and Font Opacity to 100% to have crisp, glowing terminal text floating directly over your games, IDEs, or desktop wallpapers.
- **Dynamic Background Image Scaling:**
  - Select any `.png`, `.jpg`, `.jpeg`, `.bmp`, or `.webp` image as your terminal backdrop.
  - Smoothly scales (`UniformToFill`) and conforms to rounded corners as you resize the window.
  - Background image transparency is bound directly to the Window Opacity slider.
- **Interactive Shell Switching:**
  - Real-time interaction with **Windows PowerShell** (`pwsh.exe` or `powershell.exe`) and **Windows CLI** (`cmd.exe`).
  - Seamlessly switch active shells on the fly via the right-click menu without restarting the widget.
  - Asynchronous streaming of Standard Output and Standard Error with ANSI color sequence rendering.
- **Font & Color Customization:**
  - **Font Picker:** Detects and selects top monospace terminal fonts (`Cascadia Code`, `Consolas`, `Lucida Console`, `Courier New`, `Fira Code`, `JetBrains Mono`, etc.).
  - **Font Size:** 10pt, 11pt, 12pt (default), 14pt, 16pt, 18pt, 20pt, 24pt.
  - **Vibrant Cyberpunk Palette:** Quick-switch between *Matrix Green* (`#00FF66`), *Neon Cyan* (`#00F0FF`), *Synthwave Magenta* (`#FF007F`), *Solar Amber* (`#FFB000`), *Phosphor White* (`#F0F0F0`), *Glitch Red* (`#FF2244`), and *Night City Violet* (`#A040FF`), or enter a custom hex color code.
- **Retro Cyberpunk Visual FX Suite:**
  - **Cyberpunk Rainbow Border:** Rotating animated multi-stop RGB gradient (`#FF0055` $\to$ `#00F0FF` $\to$ `#FF007F`) with a cyan glowing halo and adjustable width slider (1px – 20px).
  - **Vintage CRT Scanlines:** Hardware-accelerated horizontal scanline raster with live thickness tuning (1px – 20px).
  - **CRT Glitch Effect:** Retro sync tears, horizontal scan drops, and chromatic aberration slices with an adjustable random percentage chance slider (0% – 100%).
  - **CRT Snow Static:** Analog TV phosphor static noise with adjustable strength slider (1% – 100%).
- **Terminal Ergonomics:**
  - Command history buffer: Press <kbd>↑</kbd> and <kbd>↓</kbd> to recall previous commands.
  - Clear terminal via `cls`, `clear`, header button (`CLR`), or right-click context menu.
  - Send break / interrupt via <kbd>Ctrl</kbd> + <kbd>C</kbd> or header button (`RST`).
  - Multi-instance support: Launch multiple independent terminals across displays.
  - Always on Top desktop pinning.
- **Zero-Footprint Cleanup:**
  - Clean process exit terminates all child shell processes (`powershell.exe`, `cmd.exe`) and worker threads, leaving 0 lingering processes in Windows Task Manager.

---

## 🎛️ Context Menu & Controls

Right-click anywhere on the terminal widget to access the control HUD:

| Menu Item | Description |
| :--- | :--- |
| **Shell > Windows PowerShell** | Switch active shell to Windows PowerShell (or PowerShell 7+). |
| **Shell > Command Prompt (CMD)** | Switch active shell to Windows Command Prompt (`cmd.exe`). |
| **Clear Terminal** | Clears the scrollable terminal feed buffer. |
| **Restart Shell / Send Break** | Sends interrupt or restarts the active shell process. |
| **Font Family** | Choose from installed terminal fonts (`Cascadia Code`, `Consolas`, `Courier New`, etc.). |
| **Font Size** | Quick-select terminal font size from 10pt to 24pt. |
| **Font Color** | Choose cyberpunk presets (Matrix Green, Neon Cyan, Magenta, Amber, etc.) or enter custom hex. |
| **Window Opacity (0% - 100%)** | Real-time slider to adjust transparency of the window background panel and image. |
| **Font Opacity (10% - 100%)** | Real-time slider to adjust transparency of terminal text and prompt independently. |
| **Select Background Image...** | Open file dialog to choose a custom background image. |
| **Clear Background Image** | Removes the custom background image and reverts to frosted dark glass. |
| **Effects > Rainbow Border** | Toggle rotating neon gradient border with 1px–20px width slider. |
| **Effects > Vintage CRT Scanlines** | Toggle vintage CRT horizontal scanlines with 1px–20px thickness slider. |
| **Effects > CRT Glitch** | Toggle retro CRT sync glitching and chromatic tears with 0%–100% chance slider. |
| **Effects > CRT Snow** | Toggle continuous analog TV static noise with 1%–100% amount slider. |
| **Always on Top** | Pin terminal above all other desktop windows. |
| **Window Shadow** | Toggle soft 3D floating desktop drop shadow. |
| **New Terminal Window** | Spawns an additional independent terminal widget. |
| **Close Widget** | Closes the active terminal widget. |
| **Exit All** | Cleanly terminates all open widgets and shuts down the application. |

---

## ⌨️ Keyboard Shortcuts & Gestures

- <kbd>Enter</kbd>: Execute command in active shell.
- <kbd>↑</kbd> / <kbd>↓</kbd>: Navigate command history backwards and forwards.
- <kbd>Ctrl</kbd> + <kbd>C</kbd>: If no text is selected, sends interrupt / break signal to the active shell. If text is selected, copies text to clipboard.
- **Left-Click & Drag:** Click and drag the header or empty space to reposition the widget on your desktop.
- **Corner Resize:** Drag the bottom-right grip to resize the window dynamically.
- **Mouse Wheel on Sliders:** Hover over any slider in the context menu and scroll mouse wheel to adjust incrementally.

---

## 🛠️ Build & Run

### Prerequisites
- Windows 10 or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher

### Compiling & Running
```powershell
cd "D:\Other Coding Projects\CyberpunkTerminalWidget"

# Build debug binary
dotnet build

# Run application
dotnet run

# Or publish single-file executable
dotnet publish -c Release -r win-x64 --self-contained false
```

