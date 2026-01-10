# WinTuiEditor

A native Windows terminal UI (TUI) Text Editor built with .NET and Spectre.Console

There are plenty of really good extant Terminal text editors; Microsoft's Edit, GNU Nano, NeoVim et al. So
why build a new one? Well, for me it's more of a learning opportunity. The sister project WinTuiRss
came about because I couldn't find any native Windows TUI news reader apps. After building that, I had the
notion that it would be really great to have an entire suite of native Windows TUI applications. 

## Screenshots

![WinTuiEditor main screen](screenshots/ui-main.png)

## Download

Releases are published on the GitHub **Releases** page.

## Requirements

- For development: .NET 8 SDK
- For running: none if you use the self-contained release build (single EXE)

## Install (from source)

```powershell
git clone https://github.com/dazlab/WinTuiEditor.git
cd WinTuiEditor
dotnet restore
dotnet run
