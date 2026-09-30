<div align="right">

**English** · [简体中文](./README.md)

</div>

<p align="center">
  <img src="Launcher/icon/n8n_launcher_Gv.png" width="72" alt="n8n Portable Gv">
</p>

<h1 align="center">n8n Portable Gv</h1>

> A no-install, portable n8n bundle for Windows. Unzip and run.

[![Launcher source: MIT](https://img.shields.io/badge/launcher%20source-MIT-green.svg)](./LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Windows-x64-0078D4)](https://github.com/Gil-ver/n8n_portable_Gv/releases)

```
n8n_portable_Gv/
├─ n8n_launcher_Gv.exe    GUI launcher
├─ README.txt             Usage notes
├─ AGENTS.md              Operating manual for AI assistants
├─ app/                   n8n itself
├─ runtime/               Node.js · Python · FFmpeg
└─ data/                  Your workflows, credentials, execution history
```

Everything lives inside this one folder. No registry entries, no system services, nothing spills outside.

---

## 🤔 Why this exists

Running n8n on Windows takes more setup than it should, and the pieces end up scattered. The official routes are npm and Docker. With npm you install Node.js first, sort out your PATH, then run `npx n8n` from a terminal. Docker Desktop works but asks for a WSL2 backend and a fair amount of RAM just to keep a single service alive. Either way the parts live in different places: Node.js under Program Files, n8n's data in `.n8n` inside your user profile. Move to another machine and you repeat the whole thing, then hunt down your existing workflows and credentials by hand.

So I collected all of it into a single folder. Unzip it and it runs; delete the folder and it is gone, cleanly. Python and FFmpeg are bundled too, so workflows can call scripts and process audio and video without extra installs. The launcher handles starting and stopping n8n, its configuration, and its execution history, so you never have to touch a command line.

## 📥 Getting started

Download the archive from [Releases](https://github.com/Gil-ver/n8n_portable_Gv/releases), then:

1. Unzip it anywhere (avoid system directories, see Notes below)
2. Double-click `n8n_launcher_Gv.exe`
3. Click Start and your browser opens n8n automatically

No installation, no administrator rights.

## 📦 What's inside

| Component | Purpose |
|---|---|
| **n8n** | The workflow automation engine |
| **Node.js** | Runtime for n8n |
| **Python** | For workflows that call scripts, with yt-dlp, requests and browser-cookie3 preinstalled |
| **FFmpeg** | Audio and video processing |
| **Launcher** | GUI for managing n8n, the part open-sourced in this repo |

Component versions, licenses and where to obtain their sources are listed in [THIRD_PARTY_LICENSES.md](./THIRD_PARTY_LICENSES.md).

## 🤖 Let AI build your workflows

The bundle ships with `AGENTS.md`, an operating manual written for AI assistants. Hand it over along with your n8n address and API key to Claude Code, Cursor, Codex, Opencode, or an AI extension inside VS Code, and the assistant can create and modify workflows directly through the n8n REST API.

## 🚀 The launcher

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="images/interface-dark.png">
  <img src="images/interface-light.png" alt="n8n Launcher Gv interface">
</picture>

<sub>The interface can follow your system theme. The screenshot above switches between light and dark to match your GitHub appearance.</sub>

|  |  |
|---|---|
| **One-click start and stop** | Live n8n status at a glance |
| **Launch at login** | Starts with Windows, brings up n8n and tucks itself into the tray |
| **Lives in the tray** | Closing the window does not quit; start, stop and check status from the tray menu |
| **Low memory footprint** | Trims its own memory once minimised to the tray, suited to running for days |
| **Execution stats** | Reads n8n's SQLite directly: successes, failures and durations |
| **Execution history** | Filter by status and by workflow |
| **Built-in console** | n8n's output stays inside the window |
| **File access allowlist** | Configure `N8N_BLOCK_FILE_ACCESS_TO_N8N_FILES` and friends from the GUI |
| **Proxy injection** | One-click setup when n8n has to go through a corporate or local proxy |
| **Dependency repair** | Health check and automatic repair of `node_modules` |
| **Also** | Timezone, browser-open behaviour, Acrylic blur |

The launcher is the only original piece in the bundle. It is MIT licensed, with source under `Launcher/` in this repo.

<details>
<summary><b>🔨 Building the launcher yourself</b></summary>

Requires Windows 10 1809+ (x64) and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/Gil-ver/n8n_portable_Gv.git
cd n8n_portable_Gv/Launcher/n8n_Launcher_Gv
dotnet publish -c Release -o publish
```

The output is `publish/n8n_launcher_Gv.exe`, a self-contained single file that needs no .NET runtime on the target machine. Publish settings such as `RuntimeIdentifier`, `SelfContained`, `PublishSingleFile` and `ReadyToRun` are already in the csproj Release configuration, so you do not need to pass them again.

Drop the resulting exe over `n8n_launcher_Gv.exe` in the bundle root and it takes effect.

> **A launcher built on its own will not run n8n.** The launcher does not contain n8n; it looks for `app/`, `runtime/` and `data/` as siblings in its own directory. If you only clone this repo, build the exe and double-click it, it will fail to start n8n because those directories are missing.
>
> **In Visual Studio**: open `n8n_launcher_Gv.slnx`. Note that Build produces a set of dlls under `bin/Release/`, not a distributable single file. You still need the `dotnet publish` command above to get something you can ship.

</details>

<details>
<summary><b>🗂️ Launcher source layout</b></summary>

```
Launcher/
├─ icon/                          Shared icon assets (SVG sources + PNG/ICO)
│  ├─ launch/                     Launch-page illustrations and animation assets
│  └─ make_ico.py                 Multi-size ICO generator (7 sizes / LANCZOS)
└─ n8n_Launcher_Gv/
   ├─ MainWindow.xaml(.cs)              Main window: all five pages live here
   ├─ MainWindow.GradientRing.cs        Gradient ring rendering
   ├─ MainWindow.LaunchTileAnimation.cs Launch-page tile animation
   ├─ MainWindow.MemoryOptimization.cs  Animation suspension and working-set trim while in tray
   ├─ App.xaml(.cs)                     Entry point, theme resource dictionaries
   ├─ *Art.xaml                         Launch-page vector art (rocket / planets / clouds / flame)
   ├─ N8nExecutionStats*.cs             Execution stats: SQLite reads + caching
   ├─ N8nExecutionListService.cs        Execution history list
   ├─ TimezoneService.cs                Timezone enumeration and search
   ├─ TrayPopupMenu.cs                  Tray menu
   ├─ icon/                             Project-local icons (logo / app_icon)
   └─ Assets/Fonts/                     Bundled Source Han Sans (CJK fallback)
```

Keep the `Launcher/` level intact. The csproj reaches the parent directory's `Launcher/icon/` via `..\icon\`, so rearranging the hierarchy breaks the build with missing-resource errors.

</details>

<details>
<summary><b>⚙️ Tech stack</b></summary>

| Item | Details |
|---|---|
| Framework | .NET 8 / WPF (`net8.0-windows`) |
| UI library | [WPF-UI](https://github.com/lepoco/wpfui) (Fluent styling + tray) |
| Data | Microsoft.Data.Sqlite (reads n8n's `database.sqlite`) |
| Publish | Self-contained single file, win-x64, ReadyToRun |
| Bundled font | Source Han Sans CN Regular / Bold (SIL OFL 1.1) |

Two deliberate departures from the defaults. Single-file compression is off (`EnableCompressionInSingleFile=false`), trading a larger binary for lower memory use. Source Han Sans is embedded so the interface renders correctly on Windows installations without CJK fonts, where you would otherwise see tofu boxes; systems that already have CJK fonts fall back to Microsoft YaHei UI first and are unaffected.

</details>

## ⚠️ Notes

| Topic | Details |
|---|---|
| **Where to put it** | Keep it out of `C:\Program Files\` and similar system directories, which trigger elevation prompts and defeat the point of a portable bundle |
| **No semicolons in the path** | `;` separates PATH entries, so a semicolon anywhere in the path stops Python and FFmpeg from being found |
| **Delete `app/node_modules` before moving** | The tree is deeply nested and copying it directly exceeds the Windows 260-character path limit. Once moved, rebuild it with the launcher's dependency repair |
| **Only `data/` needs backing up** | Workflows, credentials and execution history all live there; everything else can be re-downloaded from Releases |
| **The launcher stays put** | `n8n_launcher_Gv.exe` belongs in the bundle root, since it resolves `app/`, `runtime/` and `data/` relative to itself |

Full usage notes are in `README.txt` inside the bundle.

## 💬 Feedback

Bugs, problems and feature suggestions are welcome in [Issues](https://github.com/Gil-ver/n8n_portable_Gv/issues).

These details help track things down:

- Bundle version (from the archive filename, e.g. `2026.7`) and launcher version (bottom-left of the window, e.g. `Gv_1.0.0`)
- Windows version (10 or 11)
- Steps to reproduce, plus the console output when it went wrong

If you are not comfortable describing technical details, just say what went wrong and I will ask for what I need.

For questions about n8n itself, such as configuring nodes or writing expressions, the [n8n community forum](https://community.n8n.io) is a better place with far more people to help. This repo covers the bundle and the launcher only.

## 📄 License

- **Launcher source**: [MIT](./LICENSE), free to use, modify and redistribute
- **Bundled Source Han Sans**: SIL OFL 1.1, keep `Assets/Fonts/LICENSE-OFL.txt` when redistributing
- **Third-party components**: n8n, Node.js, Python and FFmpeg are not part of this repo; see [THIRD_PARTY_LICENSES.md](./THIRD_PARTY_LICENSES.md) for their licenses

Note in particular that n8n uses the **Sustainable Use License** (fair-code, not an OSI-approved open-source license), which limits use to internal business purposes and forbids reselling it or offering it to third parties as a hosted service.

This bundle is non-commercial and permanently free. It is not resold, and it does not offer n8n as a hosted service.

## 🌹 Acknowledgements

[n8n](https://n8n.io) · [Node.js](https://nodejs.org) · [Python](https://www.python.org) · [WinPython](https://winpython.github.io) · [FFmpeg](https://ffmpeg.org) · [WPF-UI](https://github.com/lepoco/wpfui) · [Source Han Sans](https://github.com/adobe-fonts/source-han-sans)
