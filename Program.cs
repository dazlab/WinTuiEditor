using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Spectre.Console;
using TextCopy;

Console.OutputEncoding = Encoding.UTF8;
Console.TreatControlCAsInput = true;

var app = new EditorApp();
app.Run(args.Length > 0 ? args[0] : null);

sealed class EditorApp
{
    private readonly EditorState _s = new();
    private readonly ScreenRenderer _r = new();

    private bool _renderRequested = true;
    private void RequestRender() => _renderRequested = true;

    private static bool IsCtrl(ConsoleKeyInfo k, ConsoleKey key, char ctrlChar)
        => k.Key == key && (((k.Modifiers & ConsoleModifiers.Control) != 0) || k.KeyChar == ctrlChar);

    public void Run(string? initialPath)
    {
        if (!string.IsNullOrWhiteSpace(initialPath))
            TryOpen(initialPath);

        int lastW = Console.WindowWidth;
        int lastH = Console.WindowHeight;

        // Initial paint
        _r.Invalidate();
        RequestRender();

        while (!_s.ExitRequested)
        {
            // Live resize redraw (even when idle)
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;
            if (w != lastW || h != lastH)
            {
                lastW = w;
                lastH = h;
                _r.Invalidate();
                RequestRender();
            }

            if (_renderRequested)
            {
                _r.Render(_s);
                _renderRequested = false;
            }

            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                // Help overlay open/close
                if (_s.HelpVisible)
                {
                    if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.F1)
                    {
                        _s.HelpVisible = false;
                        _s.SetMessage("Help closed.");
                        _r.Invalidate();
                        RequestRender();
                    }
                    continue;
                }

                if (key.Key == ConsoleKey.F1)
                {
                    _s.HelpVisible = true;
                    _r.Invalidate();
                    RequestRender();
                    continue;
                }

                // Reliable save
                if (key.Key == ConsoleKey.F2)
                {
                    Save();
                    RequestRender();
                    continue;
                }

                // Ctrl+Shift+S Save (reliable alternative)
                if (key.Key == ConsoleKey.S &&
                    (key.Modifiers & ConsoleModifiers.Control) != 0 &&
                    (key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    Save();
                    RequestRender();
                    continue;
                }

                // Ctrl shortcuts (detect via Modifiers OR control-character)
                if (IsCtrl(key, ConsoleKey.B, '\u0002')) { _s.ToggleBoldMarkdown(); RequestRender(); continue; }
                if (IsCtrl(key, ConsoleKey.Z, '\u001A')) { _s.Undo(); RequestRender(); continue; } // Ctrl+Z
                if (IsCtrl(key, ConsoleKey.Y, '\u0019')) { _s.Redo(); RequestRender(); continue; } // Ctrl+Y
                if (IsCtrl(key, ConsoleKey.A, '\u0001')) { _s.SelectAll(); RequestRender(); continue; } // Ctrl+A

                if (IsCtrl(key, ConsoleKey.C, '\u0003')) { _s.CopyLineToClipboard(); RequestRender(); continue; } // Ctrl+C
                if (IsCtrl(key, ConsoleKey.X, '\u0018')) { _s.CutLineToClipboard(); RequestRender(); continue; } // Ctrl+X
                if (IsCtrl(key, ConsoleKey.V, '\u0016')) { _s.PasteClipboard(); RequestRender(); continue; } // Ctrl+V

                if (IsCtrl(key, ConsoleKey.N, '\u000E')) { NewFile(); RequestRender(); continue; } // Ctrl+N
                if (IsCtrl(key, ConsoleKey.O, '\u000F')) { OpenPicker(); RequestRender(); continue; } // Ctrl+O
                if (IsCtrl(key, ConsoleKey.P, '\u0010')) { CommandPalette(); RequestRender(); continue; } // Ctrl+P
                if (IsCtrl(key, ConsoleKey.S, '\u0013')) { Save(); RequestRender(); continue; } // Ctrl+S (if delivered)
                if (IsCtrl(key, ConsoleKey.G, '\u0007')) { GoToLine(); RequestRender(); continue; } // Ctrl+G
                if (IsCtrl(key, ConsoleKey.Q, '\u0011')) { HandleQuit(); RequestRender(); continue; } // Ctrl+Q

                _s.QuitArmed = false;
                _s.HandleKey(key);
                RequestRender();
            }
            else
            {
                Thread.Sleep(20);
            }
        }

        _r.Teardown();
    }

    private void HandleQuit()
    {
        if (_s.Dirty)
        {
            if (_s.QuitArmed) { _s.ExitRequested = true; return; }
            _s.QuitArmed = true;
            _s.SetMessage("Unsaved changes. Press Ctrl+Q again to quit, or Save first (F2 / Ctrl+Shift+S).");
        }
        else
        {
            _s.ExitRequested = true;
        }
    }

    private void NewFile()
    {
        _s.QuitArmed = false;

        if (_s.Dirty)
        {
            var confirm = UiPrompts.Confirm("Discard unsaved changes and create a new file?", defaultValue: false);
            _r.Invalidate();
            RequestRender();

            if (!confirm)
            {
                _s.SetMessage("Cancelled.");
                return;
            }
        }

        _s.NewEmptyBuffer();
        _s.FilePath = null;
        _s.SetMessage("New file.");
    }

    private void Save()
    {
        _s.QuitArmed = false;

        if (string.IsNullOrWhiteSpace(_s.FilePath))
        {
            var path = UiPrompts.AskPath("Save as", defaultValue: "notes.txt");
            _r.Invalidate();
            RequestRender();

            if (string.IsNullOrWhiteSpace(path))
            {
                _s.SetMessage("Save cancelled.");
                return;
            }

            _s.FilePath = path;
        }

        try
        {
            _s.SaveFile(_s.FilePath!);
            _s.SetMessage($"Saved: {_s.FilePath}");
        }
        catch (Exception ex)
        {
            _s.SetMessage($"Save failed: {ex.Message}");
        }
    }

    private void TryOpen(string path)
    {
        try
        {
            if (!File.Exists(path)) { _s.SetMessage("File not found."); return; }
            _s.LoadFile(path);
            _s.FilePath = path;
            _s.LastDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
            _s.SetMessage($"Opened: {_s.FilePath}");
        }
        catch (Exception ex)
        {
            _s.SetMessage($"Open failed: {ex.Message}");
        }
    }

    private void OpenPicker()
    {
        _s.QuitArmed = false;

        var picked = FilePicker.PickFile(
            title: "Open file",
            startDir: _s.LastDirectory ?? Environment.CurrentDirectory,
            filter: static _ => true);

        _r.Invalidate();
        RequestRender();

        if (string.IsNullOrWhiteSpace(picked))
        {
            _s.SetMessage("Open cancelled.");
            return;
        }

        TryOpen(picked);
    }

    private void GoToLine()
    {
        var max = Math.Max(1, _s.Lines.Count);
        var line = UiPrompts.AskInt("Go to line", defaultValue: _s.CursorY + 1, min: 1, max: max);

        _r.Invalidate();
        RequestRender();

        if (line is null) { _s.SetMessage("Cancelled."); return; }

        _s.CursorY = Math.Clamp(line.Value - 1, 0, _s.Lines.Count - 1);
        _s.CursorX = Math.Min(_s.CursorX, _s.Lines[_s.CursorY].Length);
        _s.ClearSelection();
        _s.SetMessage($"Moved to line {line}");
    }

    private void CommandPalette()
    {
        _s.QuitArmed = false;

        var choice = UiPrompts.Select("Command", new[]
        {
            "New file",
            "Open…",
            "Save",
            "Save As…",
            "Go to line…",
            "Help (F1)",
            "Quit"
        });

        _r.Invalidate();
        RequestRender();

        switch (choice)
        {
            case "New file":
                NewFile();
                break;

            case "Open…":
                OpenPicker();
                break;

            case "Save":
                Save();
                break;

            case "Save As…":
            {
                var path = UiPrompts.AskPath("Save as", defaultValue: _s.FilePath ?? "notes.txt");
                _r.Invalidate();
                RequestRender();

                if (string.IsNullOrWhiteSpace(path)) { _s.SetMessage("Save cancelled."); return; }
                _s.FilePath = path;
                Save();
                break;
            }

            case "Go to line…":
                GoToLine();
                break;

            case "Help (F1)":
                _s.HelpVisible = true;
                _r.Invalidate();
                break;

            case "Quit":
                HandleQuit();
                break;
        }
    }
}

static class InlineMarkdown
{
    public record Parsed(string Plain, int[] BufToVis);

    public static Parsed ParseBold(string raw)
    {
        var map = new int[raw.Length + 1];
        var sb = new StringBuilder();

        bool bold = false;
        int vis = 0;

        for (int i = 0; i < raw.Length;)
        {
            if (i + 1 < raw.Length && raw[i] == '*' && raw[i + 1] == '*')
            {
                map[i] = vis;
                map[i + 1] = vis;
                bold = !bold;
                i += 2;
                continue;
            }

            map[i] = vis;
            sb.Append(raw[i]);
            vis++;
            i++;
        }

        map[raw.Length] = vis;
        return new Parsed(sb.ToString(), map);
    }
}

sealed class ScreenRenderer
{
    private const string AppName = "WinTuiEditor";
    private const string AppVersion = "0.1.0";

    private string[] _lastRows = Array.Empty<string>();
    private int _lastRawW, _lastRawH;
    private bool _lastHelpVisible;

    // selection diff tracking (so highlight can update without full repaint)
    private bool _lastHasSel;
    private (int ax, int ay, int bx, int by) _lastSel;

    // Top border parts for row 0 drawing (title-only color)
    private string _topLeft = "";
    private string _topTitle = "";
    private string _topRight = "";

    public void Invalidate()
    {
        _lastRows = Array.Empty<string>();
        _lastRawW = 0;
        _lastRawH = 0;
        _lastHelpVisible = false;

        _lastHasSel = false;
        _lastSel = default;
    }
    
    private static string RenderInlineBoldClipped(string raw, int textW)
{
    var sb = new StringBuilder();
    bool bold = false;
    int vis = 0;

    for (int i = 0; i < raw.Length && vis < textW; )
    {
        // toggle on ** (do not render the markers)
        if (i + 1 < raw.Length && raw[i] == '*' && raw[i + 1] == '*')
        {
            bold = !bold;
            i += 2;
            continue;
        }

        var ch = Markup.Escape(raw[i].ToString());
        sb.Append(bold ? $"[bold deepskyblue1]{ch}[/]" : ch);

        vis++;
        i++;
    }

    // pad to full width so your borders/scrollbar line up
    if (vis < textW)
        sb.Append(new string(' ', textW - vis));

    return sb.ToString();
}

    
    private static string RenderInlineBold(string s)
    {
        var sb = new StringBuilder();
        bool bold = false;

        for (int i = 0; i < s.Length; i++)
        {
            // Detect **
            if (i + 1 < s.Length && s[i] == '*' && s[i + 1] == '*')
            {
                bold = !bold;
                i++;        // skip second *
                continue;   // DO NOT RENDER THE **
            }

            var ch = Markup.Escape(s[i].ToString());
            sb.Append(bold
                ? $"[bold deepskyblue1]{ch}[/]"
                : ch);
        }

        return sb.ToString();
    }

    private static void DrawEditorRowWithBold(string fullRow, int gutterW, int textW)
    {
        int ls = 1 + gutterW + 1; // "│" + gutter + " "
        if (fullRow.Length < ls + textW)
        {
            AnsiConsole.Markup(Markup.Escape(fullRow));
            return;
        }

        string pfx = fullRow.Substring(0, ls);
        string lp  = fullRow.Substring(ls, textW);
        string sfx = fullRow.Substring(ls + textW);

        AnsiConsole.Markup($"{Markup.Escape(pfx)}{RenderInlineBoldClipped(lp, textW)}{Markup.Escape(sfx)}");
    }

    public void Render(EditorState s)
    {
        int rawW = Console.WindowWidth;
        int rawH = Console.WindowHeight;

        // If help visibility changed, force a full repaint (help is full-screen)
        if (s.HelpVisible != _lastHelpVisible)
        {
            _lastHelpVisible = s.HelpVisible;
            _lastRows = Array.Empty<string>();
            Console.CursorVisible = false;
            Console.Clear();
        }

        // Help mode: render ONLY the help overlay on a clean screen.
        if (s.HelpVisible)
        {
            DrawHelpOverlayManual(rawW, rawH);
            Console.CursorVisible = false;
            return;
        }

        // Clamp only for layout (raw sizes are used for detection)
        int w = Math.Max(60, rawW);
        int h = Math.Max(12, rawH);

        int innerW = Math.Max(20, w - 2);
        int innerH = Math.Max(6, h - 2);
        int editorH = Math.Max(1, innerH - 2);

        s.ClampCursor();
        s.EnsureScroll(editorH);

        int totalLines = Math.Max(1, s.Lines.Count);
        int digits = Math.Max(3, totalLines.ToString().Length);
        int gutterW = digits + 1; // " 12│"
        int sepW = 1;             // space after gutter
        int scrollW = 1;          // scrollbar column
        int minTextW = 10;

        int textW = innerW - gutterW - sepW - scrollW;
        if (textW < minTextW)
        {
            digits = Math.Max(1, Math.Min(digits, innerW - minTextW - sepW - scrollW - 1));
            gutterW = digits + 1;
            textW = Math.Max(1, innerW - gutterW - sepW - scrollW);
        }

        // Selection change: only invalidate potentially affected editor rows
        bool hasSel = s.HasSelection;
        var sel = hasSel ? s.GetSelectionRange() : default;
        bool selChanged = hasSel != _lastHasSel || (hasSel && sel != _lastSel);

        if (selChanged && _lastRows.Length != 0)
        {
            InvalidateSelectionRows(
                s, editorH,
                _lastHasSel, _lastSel,
                hasSel, sel
            );

            _lastHasSel = hasSel;
            _lastSel = sel;
        }
        else
        {
            _lastHasSel = hasSel;
            _lastSel = sel;
        }

        var rows = new string[innerH + 2];

        BuildTopBorderParts(innerW, s);
        rows[0] = "┌" + _topLeft + _topTitle + _topRight + "┐";

        var sb = ComputeScrollbar(editorH, totalLines, s.ScrollTop);

        for (int i = 0; i < editorH; i++)
        {
            int lineIndex = s.ScrollTop + i;
            string line = lineIndex < s.Lines.Count ? s.Lines[lineIndex] : "";
            line = line.Replace('\t', ' ');

            if (line.Length > textW) line = line[..textW];
            else if (line.Length < textW) line = line.PadRight(textW);

            string ln = lineIndex < s.Lines.Count ? (lineIndex + 1).ToString() : "";
            ln = ln.PadLeft(digits).PadRight(digits);
            string gutter = ln + "│";

            char sbChar = GetScrollbarChar(sb, i);

            rows[1 + i] = "│" + gutter + " " + line + sbChar + "│";
        }

        rows[1 + editorH] = "│" + BuildStatusInner(s, gutterW, sepW, scrollW, textW) + "│";
        rows[1 + editorH + 1] = "│" + BuildMessageInner(s, gutterW, sepW, scrollW, textW) + "│";
        rows[^1] = "└" + new string('─', innerW) + "┘";

        bool sizeChanged =
            rawW != _lastRawW ||
            rawH != _lastRawH ||
            _lastRows.Length != rows.Length;

        if (sizeChanged)
        {
            _lastRows = new string[rows.Length];
            _lastRawW = rawW;
            _lastRawH = rawH;

            Console.CursorVisible = false;
            Console.Clear();
        }

        for (int y = 0; y < rows.Length; y++)
        {
            if (!string.Equals(rows[y], _lastRows[y], StringComparison.Ordinal))
            {
                DrawRow(y, rows[y], innerW, editorH, s, gutterW, textW);
                _lastRows[y] = rows[y];
            }
        }

        int viewY = Math.Clamp(s.CursorY - s.ScrollTop, 0, editorH - 1);

        var raw = s.Lines[s.CursorY].Replace('\t', ' ');
        var parsed = InlineMarkdown.ParseBold(raw);

        int bufX = Math.Clamp(s.CursorX, 0, parsed.BufToVis.Length - 1);
        int viewX = Math.Clamp(parsed.BufToVis[bufX], 0, Math.Max(0, textW - 1));

        int cursorConsoleX = 1 + gutterW + 1 + viewX;
        int cursorConsoleY = 1 + viewY;

        SafeSetCursor(cursorConsoleX, cursorConsoleY);
        Console.CursorVisible = true;
    }

    public void Teardown()
    {
        Console.CursorVisible = true;
        Console.Clear();
    }

    private void BuildTopBorderParts(int innerW, EditorState s)
    {
        string app = $"{AppName} v{AppVersion}";
        string file = string.IsNullOrWhiteSpace(s.FilePath) ? "Untitled" : Path.GetFileName(s.FilePath);
        string title = $" {app} ─ {file} ";

        if (title.Length > innerW)
            title = title[..innerW];

        int remaining = innerW - title.Length;
        int left = remaining / 2;
        int right = remaining - left;

        _topLeft = new string('─', left);
        _topTitle = title;
        _topRight = new string('─', right);
    }

    private void InvalidateSelectionRows(
        EditorState s,
        int editorH,
        bool oldHasSel, (int ax, int ay, int bx, int by) oldSel,
        bool newHasSel, (int ax, int ay, int bx, int by) newSel)
    {
        int startLine = int.MaxValue;
        int endLine = int.MinValue;

        if (oldHasSel)
        {
            startLine = Math.Min(startLine, oldSel.ay);
            endLine = Math.Max(endLine, oldSel.by);
        }

        if (newHasSel)
        {
            startLine = Math.Min(startLine, newSel.ay);
            endLine = Math.Max(endLine, newSel.by);
        }

        if (startLine == int.MaxValue)
            return;

        int viewStart = s.ScrollTop;
        int viewEnd = s.ScrollTop + editorH - 1;

        int a = Math.Max(startLine, viewStart);
        int b = Math.Min(endLine, viewEnd);

        if (a > b)
            return;

        for (int line = a; line <= b; line++)
        {
            int rowInEditor = line - s.ScrollTop; // 0..editorH-1
            int y = 1 + rowInEditor;              // console row index
            if (y >= 0 && y < _lastRows.Length)
                _lastRows[y] = ""; // force redraw of just this row
        }
    }

    private void DrawRow(int y, string text, int innerW, int editorH, EditorState s, int gutterW, int textW)
{
    SafeSetCursor(0, y);

    if (y == 0)
    {
        AnsiConsole.Markup(
            $"{Markup.Escape("┌")}{Markup.Escape(_topLeft)}" +
            $"[bold deepskyblue1]{Markup.Escape(_topTitle)}[/]" +
            $"{Markup.Escape(_topRight)}{Markup.Escape("┐")}"
        );
        return;
    }

    bool isStatus = (y == 1 + editorH);
    bool isMessage = (y == 1 + editorH + 1);

    if (isStatus)
    {
        var inner = text.Substring(1, innerW);
        AnsiConsole.Markup($"{Markup.Escape("│")}[black on grey]{Markup.Escape(inner)}[/]{Markup.Escape("│")}");
        return;
    }

    if (isMessage)
    {
        var inner = text.Substring(1, innerW);
        var style = string.IsNullOrWhiteSpace(inner.Trim()) ? "grey" : "yellow";
        AnsiConsole.Markup($"{Markup.Escape("│")}[{style}]{Markup.Escape(inner)}[/]{Markup.Escape("│")}");
        return;
    }

    // Editor rows
    int rowInEditor = y - 1;
    int lineIndex = s.ScrollTop + rowInEditor;

    int ls = 1 + gutterW + 1; // "│" + gutter + " "
    if (text.Length < ls + textW || lineIndex < 0 || lineIndex >= s.Lines.Count)
    {
        DrawEditorRowWithBold(text, gutterW, textW);
        return;
    }

    string prefix = text.Substring(0, ls);
    string suffix = text.Substring(ls + textW); // includes scrollbar + trailing border

    string raw = s.Lines[lineIndex].Replace('\t', ' ');
    var parsed = InlineMarkdown.ParseBold(raw);

    // Visible line region (no ** markers)
    string visible = parsed.Plain;
    if (visible.Length > textW) visible = visible[..textW];
    else visible = visible.PadRight(textW);

    // If no selection or this row isn't within selection, render with inline bold
    if (!s.HasSelection)
    {
        AnsiConsole.Markup($"{Markup.Escape(prefix)}{RenderInlineBoldClipped(raw, textW)}{Markup.Escape(suffix)}");
        return;
    }

    var (ax, ay, bx, by) = s.GetSelectionRange();
    if (lineIndex < ay || lineIndex > by)
    {
        AnsiConsole.Markup($"{Markup.Escape(prefix)}{RenderInlineBoldClipped(raw, textW)}{Markup.Escape(suffix)}");
        return;
    }

    // Map buffer indices (including ** in raw) to visible indices (without **)
    int MapBufToVis(int bufIndex)
    {
        bufIndex = Math.Clamp(bufIndex, 0, parsed.BufToVis.Length - 1);
        return Math.Clamp(parsed.BufToVis[bufIndex], 0, textW);
    }

    int selStartVis, selEndVis;

    if (ay == by)
    {
        selStartVis = MapBufToVis(ax);
        selEndVis = MapBufToVis(bx);
    }
    else if (lineIndex == ay)
    {
        selStartVis = MapBufToVis(ax);
        selEndVis = textW;
    }
    else if (lineIndex == by)
    {
        selStartVis = 0;
        selEndVis = MapBufToVis(bx);
    }
    else
    {
        selStartVis = 0;
        selEndVis = textW;
    }

    selStartVis = Math.Clamp(selStartVis, 0, textW);
    selEndVis = Math.Clamp(selEndVis, 0, textW);

    if (selEndVis <= selStartVis)
    {
        AnsiConsole.Markup($"{Markup.Escape(prefix)}{RenderInlineBoldClipped(raw, textW)}{Markup.Escape(suffix)}");
        return;
    }

    // Selection-highlight rendering (bold is not mixed inside the highlighted segment here)
    string a = visible.Substring(0, selStartVis);
    string b = visible.Substring(selStartVis, selEndVis - selStartVis);
    string c = visible.Substring(selEndVis);

    AnsiConsole.Markup(
        $"{Markup.Escape(prefix)}" +
        $"{Markup.Escape(a)}" +
        $"[black on deepskyblue1]{Markup.Escape(b)}[/]" +
        $"{Markup.Escape(c)}" +
        $"{Markup.Escape(suffix)}"
    );
}


    private static string BuildStatusInner(EditorState s, int gutterW, int sepW, int scrollW, int textW)
    {
        string name = string.IsNullOrWhiteSpace(s.FilePath) ? "Untitled" : Path.GetFileName(s.FilePath);
        string dirty = s.Dirty ? "*" : "";
        string pos = $"Ln {s.CursorY + 1}, Col {s.CursorX + 1}";
        string hist = $"Undo {s.UndoDepth}  Redo {s.RedoDepth}";
        string right = "F1 Help";

        string left = $"{name}{dirty}  {pos}  {hist}";
        int bodyW = gutterW + sepW + textW + scrollW;

        int pad = Math.Max(1, bodyW - left.Length - right.Length);
        string body = left + new string(' ', pad) + right;

        if (body.Length > bodyW) body = body[..bodyW];
        else if (body.Length < bodyW) body = body.PadRight(bodyW);

        return body;
    }

    private static string BuildMessageInner(EditorState s, int gutterW, int sepW, int scrollW, int textW)
    {
        int bodyW = gutterW + sepW + textW + scrollW;
        var msg = s.Message ?? "";
        if (msg.Length > bodyW) msg = msg[..bodyW];
        else if (msg.Length < bodyW) msg = msg.PadRight(bodyW);
        return msg;
    }

    private static void DrawHelpOverlayManual(int rawW, int rawH)
    {
        Console.CursorVisible = false;

        if (rawW < 30 || rawH < 12)
        {
            Console.SetCursorPosition(0, 0);
            Console.Write("Window too small for help. Resize larger.");
            return;
        }

        string[] content =
        {
            "WinTuiEditor Help",
            "",
            "File",
            "  Ctrl+N        New file",
            "  Ctrl+O        Open file",
            "  F2            Save",
            "  Ctrl+Q        Quit (press twice if dirty)",
            "",
            "Edit",
            "  Ctrl+Z / Ctrl+Y   Undo / Redo",
            "  Ctrl+A            Select all",
            "  Ctrl+C            Copy (selection or line)",
            "  Ctrl+X            Cut line",
            "  Ctrl+V            Paste",
            "",
            "Selection",
            "  Shift+Arrows/Home/End/PgUp/PgDn  Extend selection",
            "",
            "Navigation",
            "  Arrows/Home/End, PgUp/PgDn",
            "  Ctrl+G        Go to line",
            "",
            "  Esc or F1      Close help"
        };

        int innerMaxLine = content.Max(s => s.Length);
        int boxInnerW = Math.Min(innerMaxLine, rawW - 6);
        int boxW = boxInnerW + 2;
        int boxH = Math.Min(content.Length + 2, rawH - 4);

        int x = (rawW - boxW) / 2;
        int y = (rawH - boxH) / 2;

        Console.SetCursorPosition(0, 0);
        Console.Clear();

        Console.SetCursorPosition(x, y);
        Console.Write("┌" + new string('─', boxInnerW) + "┐");

        int usableLines = boxH - 2;
        for (int i = 0; i < usableLines; i++)
        {
            Console.SetCursorPosition(x, y + 1 + i);

            string line = i < content.Length ? content[i] : "";
            if (line.Length > boxInnerW) line = line[..boxInnerW];
            else line = line.PadRight(boxInnerW);

            Console.Write("│" + line + "│");
        }

        Console.SetCursorPosition(x, y + boxH - 1);
        Console.Write("└" + new string('─', boxInnerW) + "┘");
    }

    private static void SafeSetCursor(int x, int y)
    {
        try { Console.SetCursorPosition(x, y); } catch { }
    }

    private readonly record struct Scrollbar(int ThumbTop, int ThumbSize, bool Enabled);

    private static Scrollbar ComputeScrollbar(int viewH, int totalLines, int scrollTop)
    {
        if (totalLines <= viewH || viewH <= 0)
            return new Scrollbar(0, 0, Enabled: false);

        int thumbSize = Math.Max(1, (int)Math.Round((double)viewH * viewH / totalLines));
        thumbSize = Math.Clamp(thumbSize, 1, viewH);

        int maxScroll = totalLines - viewH;
        int maxThumbTop = viewH - thumbSize;

        int thumbTop = maxScroll <= 0 ? 0 : (int)Math.Round((double)scrollTop * maxThumbTop / maxScroll);
        thumbTop = Math.Clamp(thumbTop, 0, maxThumbTop);

        return new Scrollbar(thumbTop, thumbSize, Enabled: true);
    }

    private static char GetScrollbarChar(Scrollbar sb, int row)
    {
        if (!sb.Enabled) return ' ';
        bool inThumb = row >= sb.ThumbTop && row < sb.ThumbTop + sb.ThumbSize;
        return inThumb ? '█' : '░';
    }
}

sealed class EditorState
{
    private const int MaxHistory = 200;

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    private readonly record struct Snapshot(string[] Lines, int CursorX, int CursorY, int ScrollTop, bool Dirty);

    public List<string> Lines { get; private set; } = new() { "" };

    public int CursorX { get; set; }
    public int CursorY { get; set; }
    public int ScrollTop { get; set; }

    public bool Dirty { get; private set; }
    public bool ExitRequested { get; set; }

    public string? FilePath { get; set; }
    public string? LastDirectory { get; set; }

    public string? Message { get; private set; }
    private DateTime _messageUntil = DateTime.MinValue;

    public bool QuitArmed { get; set; }
    public bool HelpVisible { get; set; }

    // ===== Selection (MVP) =====
    public int SelAnchorX { get; private set; }
    public int SelAnchorY { get; private set; }
    public bool HasSelection => SelAnchorX != CursorX || SelAnchorY != CursorY;

    public void ClearSelection()
    {
        SelAnchorX = CursorX;
        SelAnchorY = CursorY;
    }

    public void BeginSelectionIfNone()
    {
        if (!HasSelection)
        {
            SelAnchorX = CursorX;
            SelAnchorY = CursorY;
        }
    }
    
    public void ToggleBoldMarkdown()
    {
        // If selection exists, wrap/unwrap with **
        if (HasSelection)
        {
            PushUndo();

            var selected = GetSelectedText();
            if (selected.Length == 0) { SetMessage("Nothing selected."); return; }

            if (selected.StartsWith("**") && selected.EndsWith("**") && selected.Length >= 4)
            {
                selected = selected.Substring(2, selected.Length - 4);
                ReplaceSelectionWith(selected);
                SetMessage("Bold removed.");
            }
            else
            {
                ReplaceSelectionWith("**" + selected + "**");
                SetMessage("Bold applied.");
            }

            Dirty = true;
            ClearSelection();
            return;
        }

        // No selection: insert **** and place caret between the middle **
        PushUndo();
        InsertTextCore("****");
        CursorX -= 2;

        Dirty = true;
        ClearSelection();
        SetMessage("Bold marker inserted.");
    }
    
    private void ReplaceSelectionWith(string text)
    {
        if (!HasSelection)
            return;

        var (ax, ay, bx, by) = GetSelectionRange();

        ay = Math.Clamp(ay, 0, Lines.Count - 1);
        by = Math.Clamp(by, 0, Lines.Count - 1);
        ax = Math.Clamp(ax, 0, Lines[ay].Length);
        bx = Math.Clamp(bx, 0, Lines[by].Length);

        if (ay == by)
        {
            var line = Lines[ay];
            Lines[ay] = line.Substring(0, ax) + text + line.Substring(bx);
            CursorY = ay;
            CursorX = ax + text.Length;
            return;
        }

        var left = Lines[ay].Substring(0, ax);
        var right = Lines[by].Substring(bx);

        var newText = (left + text + right).Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = newText.Split('\n').ToList();

        Lines[ay] = parts[0];
        Lines.RemoveRange(ay + 1, by - ay);

        for (int i = 1; i < parts.Count; i++)
            Lines.Insert(ay + i, parts[i]);

        CursorY = ay + parts.Count - 1;
        CursorX = parts[^1].Length - right.Length;
    }

    public (int ax, int ay, int bx, int by) GetSelectionRange()
    {
        var ax = SelAnchorX; var ay = SelAnchorY;
        var bx = CursorX; var by = CursorY;

        if (ay < by) return (ax, ay, bx, by);
        if (ay > by) return (bx, by, ax, ay);
        return ax <= bx ? (ax, ay, bx, by) : (bx, by, ax, ay);
    }

    public void SelectAll()
    {
        if (Lines.Count == 0) Lines.Add("");

        SelAnchorX = 0;
        SelAnchorY = 0;

        CursorY = Lines.Count - 1;
        CursorX = Lines[CursorY].Length;

        SetMessage("Selected all.");
    }

    public int UndoDepth => Math.Max(0, _undo.Count - 1);
    public int RedoDepth => _redo.Count;

    public EditorState()
    {
        ClearHistory(seedCurrent: true);
        ClearSelection();
    }

    public void SetMessage(string text, int ms = 2500)
    {
        Message = text;
        _messageUntil = DateTime.UtcNow.AddMilliseconds(ms);
    }

    private Snapshot Capture()
        => new Snapshot(Lines.ToArray(), CursorX, CursorY, ScrollTop, Dirty);

    private void Restore(Snapshot s)
    {
        Lines = s.Lines.ToList();
        if (Lines.Count == 0) Lines.Add("");
        CursorX = s.CursorX;
        CursorY = s.CursorY;
        ScrollTop = s.ScrollTop;
        Dirty = s.Dirty;

        ClampCursor();
        ClearSelection();
    }

    private void PushUndo()
    {
        if (_undo.Count >= MaxHistory)
        {
            var keep = _undo.Reverse().Skip(1).Take(MaxHistory - 1).Reverse().ToArray();
            _undo.Clear();
            foreach (var snap in keep) _undo.Push(snap);
        }

        _undo.Push(Capture());
        _redo.Clear();
    }

    private void ClearHistory(bool seedCurrent)
    {
        _undo.Clear();
        _redo.Clear();
        if (seedCurrent)
            _undo.Push(Capture());
    }

    public void Undo()
    {
        if (_undo.Count <= 1)
        {
            SetMessage("Nothing to undo.");
            return;
        }

        var current = Capture();
        var prev = _undo.Pop();

        _redo.Push(current);
        Restore(prev);
        SetMessage("Undo");
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            SetMessage("Nothing to redo.");
            return;
        }

        var current = Capture();
        var next = _redo.Pop();

        _undo.Push(current);
        Restore(next);
        SetMessage("Redo");
    }

    public void NewEmptyBuffer()
    {
        Lines = new List<string> { "" };
        CursorX = 0;
        CursorY = 0;
        ScrollTop = 0;
        Dirty = false;
        QuitArmed = false;
        HelpVisible = false;

        ClearSelection();
        ClearHistory(seedCurrent: true);
    }

    public void ClampCursor()
    {
        if (Lines.Count == 0) Lines.Add("");
        CursorY = Math.Clamp(CursorY, 0, Lines.Count - 1);
        CursorX = Math.Clamp(CursorX, 0, Lines[CursorY].Length);

        if (DateTime.UtcNow > _messageUntil)
            Message = null;
    }

    public void EnsureScroll(int editorHeight)
    {
        if (CursorY < ScrollTop) ScrollTop = CursorY;
        if (CursorY >= ScrollTop + editorHeight) ScrollTop = CursorY - editorHeight + 1;

        int maxTop = Math.Max(0, Lines.Count - editorHeight);
        ScrollTop = Math.Clamp(ScrollTop, 0, maxTop);
    }

    public string GetSelectedText()
    {
        if (!HasSelection)
            return "";

        var (ax, ay, bx, by) = GetSelectionRange();

        ay = Math.Clamp(ay, 0, Lines.Count - 1);
        by = Math.Clamp(by, 0, Lines.Count - 1);
        ax = Math.Clamp(ax, 0, Lines[ay].Length);
        bx = Math.Clamp(bx, 0, Lines[by].Length);

        var sb = new StringBuilder();

        if (ay == by)
        {
            sb.Append(Lines[ay].Substring(ax, bx - ax));
            return sb.ToString();
        }

        sb.AppendLine(Lines[ay].Substring(ax));

        for (int y = ay + 1; y < by; y++)
            sb.AppendLine(Lines[y]);

        sb.Append(Lines[by].Substring(0, bx));
        return sb.ToString();
    }

    public void CopyLineToClipboard()
    {
        if (HasSelection)
        {
            var text = GetSelectedText();
            if (text.Length == 0) { SetMessage("Nothing to copy."); return; }

            ClipboardService.SetText(text);
            SetMessage("Copied selection.");
            return;
        }

        if (Lines.Count == 0) { SetMessage("Nothing to copy."); return; }
        var line = Lines[Math.Clamp(CursorY, 0, Lines.Count - 1)];
        ClipboardService.SetText(line);
        SetMessage("Copied line.");
    }

    public void CutLineToClipboard()
    {
        if (Lines.Count == 0) { SetMessage("Nothing to cut."); return; }

        PushUndo();

        var idx = Math.Clamp(CursorY, 0, Lines.Count - 1);
        var line = Lines[idx];
        ClipboardService.SetText(line);

        Lines.RemoveAt(idx);
        if (Lines.Count == 0) Lines.Add("");

        CursorY = Math.Clamp(idx, 0, Lines.Count - 1);
        CursorX = Math.Min(CursorX, Lines[CursorY].Length);

        Dirty = true;
        ClearSelection();
        SetMessage("Cut line.");
    }

    public void PasteClipboard()
    {
        var text = ClipboardService.GetText() ?? "";
        if (text.Length == 0) { SetMessage("Clipboard empty."); return; }

        if (HasSelection)
        {
            DeleteSelection();
            // DeleteSelection already pushed undo and cleared selection
            // We'll push undo again only if DeleteSelection did nothing; safe to just continue without extra push.
        }
        else
        {
            PushUndo();
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = text.Split('\n');

        if (parts.Length == 1)
        {
            InsertTextCore(parts[0]);
            Dirty = true;
            ClearSelection();
            SetMessage("Pasted.");
            return;
        }

        var cur = Lines[CursorY];
        var left = cur[..CursorX];
        var right = cur[CursorX..];

        Lines[CursorY] = left + parts[0];

        int insertAt = CursorY + 1;
        for (int i = 1; i < parts.Length - 1; i++)
            Lines.Insert(insertAt++, parts[i]);

        Lines.Insert(insertAt, parts[^1] + right);

        CursorY = insertAt;
        CursorX = parts[^1].Length;

        Dirty = true;
        ClearSelection();
        SetMessage("Pasted.");
    }

    public void HandleKey(ConsoleKeyInfo k)
    {
        bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;

        switch (k.Key)
        {
            case ConsoleKey.LeftArrow:
                if (shift) BeginSelectionIfNone(); else ClearSelection();
                if (CursorX > 0) CursorX--;
                else if (CursorY > 0) { CursorY--; CursorX = Lines[CursorY].Length; }
                break;

            case ConsoleKey.RightArrow:
                if (shift) BeginSelectionIfNone(); else ClearSelection();
                if (CursorX < Lines[CursorY].Length) CursorX++;
                else if (CursorY < Lines.Count - 1) { CursorY++; CursorX = 0; }
                break;

            case ConsoleKey.UpArrow:
                if (shift) BeginSelectionIfNone(); else ClearSelection();
                if (CursorY > 0) { CursorY--; CursorX = Math.Min(CursorX, Lines[CursorY].Length); }
                break;

            case ConsoleKey.DownArrow:
                if (shift) BeginSelectionIfNone(); else ClearSelection();
                if (CursorY < Lines.Count - 1) { CursorY++; CursorX = Math.Min(CursorX, Lines[CursorY].Length); }
                break;

            case ConsoleKey.Home:
                if (shift) BeginSelectionIfNone(); else ClearSelection();
                CursorX = 0;
                break;

            case ConsoleKey.End:
                if (shift) BeginSelectionIfNone(); else ClearSelection();
                CursorX = Lines[CursorY].Length;
                break;

            case ConsoleKey.PageUp:
                if (shift) BeginSelectionIfNone(); else ClearSelection();
                CursorY = Math.Max(0, CursorY - Math.Max(1, Console.WindowHeight - 6));
                CursorX = Math.Min(CursorX, Lines[CursorY].Length);
                break;

            case ConsoleKey.PageDown:
                if (shift) BeginSelectionIfNone(); else ClearSelection();
                CursorY = Math.Min(Lines.Count - 1, CursorY + Math.Max(1, Console.WindowHeight - 6));
                CursorX = Math.Min(CursorX, Lines[CursorY].Length);
                break;

            case ConsoleKey.Backspace:
                Backspace();
                break;

            case ConsoleKey.Delete:
                Delete();
                break;

            case ConsoleKey.Enter:
                NewLine();
                break;

            case ConsoleKey.Tab:
                InsertText("    ");
                break;

            default:
                if (!char.IsControl(k.KeyChar))
                    InsertText(k.KeyChar.ToString());
                break;
        }
    }

    public void LoadFile(string path)
    {
        var text = File.ReadAllText(path);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        Lines = text.Split('\n').ToList();
        if (Lines.Count == 0) Lines.Add("");

        CursorX = 0;
        CursorY = 0;
        ScrollTop = 0;
        Dirty = false;
        QuitArmed = false;
        HelpVisible = false;

        ClearSelection();
        ClearHistory(seedCurrent: true);
    }

    public void SaveFile(string path)
    {
        var text = string.Join(Environment.NewLine, Lines);
        File.WriteAllText(path, text);

        Dirty = false;
        QuitArmed = false;
    }

    private void InsertText(string s)
    {
        if (HasSelection)
        {
            DeleteSelection(); // PushUndo + Dirty + ClearSelection
            InsertTextCore(s);
            Dirty = true;
            ClearSelection();
            return;
        }

        PushUndo();
        InsertTextCore(s);
        Dirty = true;
        ClearSelection();
    }

    private void InsertTextCore(string s)
    {
        var line = Lines[CursorY];
        Lines[CursorY] = line.Insert(CursorX, s);
        CursorX += s.Length;
    }

    private void NewLine()
    {
        if (HasSelection)
        {
            DeleteSelection();
        }
        else
        {
            PushUndo();
        }

        var line = Lines[CursorY];
        var left = line[..CursorX];
        var right = line[CursorX..];

        Lines[CursorY] = left;
        Lines.Insert(CursorY + 1, right);

        CursorY++;
        CursorX = 0;
        Dirty = true;
        ClearSelection();
    }

    private void Backspace()
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }

        if (CursorX == 0 && CursorY == 0)
            return;

        PushUndo();

        if (CursorX > 0)
        {
            var line = Lines[CursorY];
            Lines[CursorY] = line.Remove(CursorX - 1, 1);
            CursorX--;
            Dirty = true;
            ClearSelection();
            return;
        }

        if (CursorY > 0)
        {
            var prev = Lines[CursorY - 1];
            var cur = Lines[CursorY];
            int newX = prev.Length;

            Lines[CursorY - 1] = prev + cur;
            Lines.RemoveAt(CursorY);

            CursorY--;
            CursorX = newX;
            Dirty = true;
            ClearSelection();
        }
    }

    private void Delete()
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }

        var line = Lines[CursorY];
        if (CursorX == line.Length && CursorY == Lines.Count - 1)
            return;

        PushUndo();

        if (CursorX < line.Length)
        {
            Lines[CursorY] = line.Remove(CursorX, 1);
            Dirty = true;
            ClearSelection();
            return;
        }

        if (CursorY < Lines.Count - 1)
        {
            Lines[CursorY] = line + Lines[CursorY + 1];
            Lines.RemoveAt(CursorY + 1);
            Dirty = true;
            ClearSelection();
        }
    }

    private void DeleteSelection()
    {
        if (!HasSelection)
            return;

        PushUndo();

        var (ax, ay, bx, by) = GetSelectionRange();

        ay = Math.Clamp(ay, 0, Lines.Count - 1);
        by = Math.Clamp(by, 0, Lines.Count - 1);

        ax = Math.Clamp(ax, 0, Lines[ay].Length);
        bx = Math.Clamp(bx, 0, Lines[by].Length);

        if (ay == by && bx <= ax)
        {
            ClearSelection();
            return;
        }

        if (ay == by)
        {
            int count = bx - ax;
            Lines[ay] = Lines[ay].Remove(ax, count);
            CursorX = ax;
            CursorY = ay;
        }
        else
        {
            var left = Lines[ay].Substring(0, ax);
            var right = Lines[by].Substring(bx);

            Lines[ay] = left + right;
            Lines.RemoveRange(ay + 1, by - ay);

            CursorX = ax;
            CursorY = ay;
        }

        ClearSelection();
        Dirty = true;
    }
}

static class UiPrompts
{
    public static string? Select(string title, IEnumerable<string> options)
    {
        Console.CursorVisible = true;
        Console.Clear();

        var prompt = new SelectionPrompt<string>()
            .Title(Markup.Escape(title))
            .PageSize(10)
            .UseConverter(Markup.Escape)
            .AddChoices(options);

        try { return AnsiConsole.Prompt(prompt); }
        catch { return null; }
    }

    public static bool Confirm(string title, bool defaultValue)
    {
        Console.CursorVisible = true;
        Console.Clear();

        try
        {
            return AnsiConsole.Prompt(
                new ConfirmationPrompt(Markup.Escape(title))
                {
                    DefaultValue = defaultValue
                });
        }
        catch
        {
            return false;
        }
    }

    public static string? AskPath(string title, string defaultValue)
    {
        Console.CursorVisible = true;
        Console.Clear();

        var input = AnsiConsole.Prompt(
            new TextPrompt<string>($"{title}:")
                .DefaultValue(defaultValue)
                .AllowEmpty()
        );

        input = (input ?? "").Trim();
        return input.Length == 0 ? null : input;
    }

    public static int? AskInt(string title, int defaultValue, int min, int max)
    {
        Console.CursorVisible = true;
        Console.Clear();

        try
        {
            return AnsiConsole.Prompt(
                new TextPrompt<int>($"{title} ({min}-{max}):")
                    .DefaultValue(defaultValue)
                    .Validate(v => v >= min && v <= max
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Enter a number between {min} and {max}."))
            );
        }
        catch { return null; }
    }
}

static class FilePicker
{
    public static string? PickFile(string title, string startDir, Func<string, bool> filter)
    {
        var dir = Directory.Exists(startDir) ? startDir : Environment.CurrentDirectory;

        while (true)
        {
            Console.Clear();

            var entries = GetEntries(dir, filter);

            var prompt = new SelectionPrompt<string>()
                .Title($"{Markup.Escape(title)}\n[grey]{Markup.Escape(dir)}[/]")
                .PageSize(20)
                .UseConverter(Markup.Escape);

            prompt.AddChoice(".. (up)");
            foreach (var e in entries)
                prompt.AddChoice(e);

            prompt.AddChoice("Cancel");

            var choice = AnsiConsole.Prompt(prompt);

            if (choice == "Cancel") return null;

            if (choice == ".. (up)")
            {
                var parent = Directory.GetParent(dir);
                if (parent != null) dir = parent.FullName;
                continue;
            }

            if (choice.EndsWith(Path.DirectorySeparatorChar))
            {
                var nextDir = Path.Combine(dir, choice.TrimEnd(Path.DirectorySeparatorChar));
                if (Directory.Exists(nextDir)) dir = nextDir;
                continue;
            }

            var file = Path.Combine(dir, choice);
            if (File.Exists(file)) return file;
        }
    }

    private static List<string> GetEntries(string dir, Func<string, bool> filter)
    {
        var result = new List<string>();

        try
        {
            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
                result.Add(Path.GetFileName(d) + Path.DirectorySeparatorChar);

            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
            {
                if (filter(f))
                    result.Add(Path.GetFileName(f));
            }
        }
        catch
        {
            // ignore permission errors
        }

        return result;
    }
}
