![Header](screenshots/github-header-banner-dev.png)

[![License](https://img.shields.io/github/license/dazlab/WinTuiEditor.svg)](LICENSE)
[![Issues](https://img.shields.io/github/issues/dazlab/WinTuiEditor.svg)](https://github.com/dazlab/WinTuiRss/issues)
![Downloads](https://img.shields.io/github/downloads/dazlab/WinTuiEditor/total)
[![Build](https://github.com/dazlab/WinTuiEditor/actions/workflows/dotnet-desktop.yml/badge.svg?branch=master&cacheBust=1)](https://github.com/dazlab/WinTuiEditor/actions/workflows/dotnet-desktop.yml)
[![Dev](https://img.shields.io/github/actions/workflow/status/dazlab/WinTuiEditor/dotnet-desktop.yml?branch=development&label=Dev)](https://github.com/dazlab/WinTuiEditor/actions/workflows/dotnet-desktop.yml?query=branch%3Adevelopment)


## A native Windows terminal UI (TUI) Text Editor built with .NET and [Spectre.Console](https://github.com/spectreconsole/spectre.console)

## Current Branch Diffs
Implemented:
- Theme cycling, along with 7 themes.
- Bold text (rendering and markup/saving)
- Underlined text
- Selection rendering, line select editing, SelectAll etc.

### Theme Switching
[▶ Watch demo video](screenshots/theme-switching.mp4)

### Bold Text Rendering
![WinTuiEditor main screen](screenshots/bold.png)

### Underlined Text Rendering
![WinTuiEditor main screen](screenshots/underlined.png)

### Selection
![WinTuiEditor main screen](screenshots/line-select.png)

## Requirements

- For development: .NET 8 SDK
- For running: none if you use the self-contained release build (single EXE)

## Install (from source)

```powershell
git clone https://github.com/dazlab/WinTuiEditor.git
cd WinTuiEditor
dotnet restore
dotnet run
```
