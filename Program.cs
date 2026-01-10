// Program.cs
// WinTuiEditor (single-file):
// - Diff-based rendering
// - Frame/borders with colored title (title text only)
// - Line numbers gutter
// - Right-side scrollbar
// - Command palette (Ctrl+P) with New file
// - New file (Ctrl+N) with discard-changes confirmation
// - Open (Ctrl+O), Save (F2 / Ctrl+Shift+S / Ctrl+S if it arrives), Quit (Ctrl+Q)
// - Go to line (Ctrl+G)
// - Undo/Redo (Ctrl+Z / Ctrl+Y) (bounded snapshot history)
// - Clipboard: copy/cut line + paste (Ctrl+C / Ctrl+X / Ctrl+V) via TextCopy
// - Shortened status bar + F1 Help overlay (Esc to close)
// - Help overlay renders on a clean screen (no compositing artifacts)
// - Renderer cache invalidation after prompts
// - Live resize redraw + initial paint
// - Ctrl shortcuts detected via Modifiers OR control-character
//
// Dependencies:
//   dotnet add package Spectre.Console
//   dotnet add package TextCopy

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
                if (IsCtrl(key, ConsoleKey.Z, '\u001A')) { _s.Undo();                 RequestRender(); continue; } // Ctrl+Z
                if (IsCtrl(key, ConsoleKey.Y, '\u0019')) { _s.Redo();                 RequestRender(); continue; } // Ctrl+Y

                if (IsCtrl(key, ConsoleKey.C, '\u0003')) { _s.CopyLineToClipboard();  RequestRender(); continue; } // Ctrl+C
                if (IsCtrl(key, ConsoleKey.X, '\u0018')) { _s.CutLineToClipboard();   RequestRender(); continue; } // Ctrl+X
                if (IsCtrl(key, ConsoleKey.V, '\u0016')) { _s.PasteClipboard();       RequestRender(); continue; } // Ctrl+V

                if (IsCtrl(key, ConsoleKey.N, '\u000E')) { NewFile();                 RequestRender(); continue; } // Ctrl+N
                if (IsCtrl(key, ConsoleKey.O, '\u000F')) { OpenPicker();              RequestRender(); continue; } // Ctrl+O
                if (IsCtrl(key, ConsoleKey.P, '\u0010')) { CommandPalette();          RequestRender(); continue; } // Ctrl+P
                if (IsCtrl(key, ConsoleKey.S, '\u0013')) { Save();                    RequestRender(); continue; } // Ctrl+S (if delivered)
                if (IsCtrl(key, ConsoleKey.G, '\u0007')) { GoToLine();                RequestRender(); continue; } // Ctrl+G
                if (IsCtrl(key, ConsoleKey.Q, '\u0011')) { HandleQuit();              RequestRender(); continue; } // Ctrl+Q

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

sealed class ScreenRenderer
{
    private const string AppName = "WinTuiEditor";
    private const string AppVersion = "0.1.0";

    private string[] _lastRows = Array.Empty<string>();
    private int _lastRawW, _lastRawH;
    private bool _lastHelpVisible;

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
                DrawRow(y, rows[y], innerW, editorH);
                _lastRows[y] = rows[y];
            }
        }

        int viewY = Math.Clamp(s.CursorY - s.ScrollTop, 0, editorH - 1);
        int viewX = Math.Clamp(s.CursorX, 0, Math.Max(0, textW - 1));

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

    private void DrawRow(int y, string text, int innerW, int editorH)
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
        }
        else if (isMessage)
        {
            var inner = text.Substring(1, innerW);
            var style = string.IsNullOrWhiteSpace(inner.Trim()) ? "grey" : "yellow";
            AnsiConsole.Markup($"{Markup.Escape("│")}[{style}]{Markup.Escape(inner)}[/]{Markup.Escape("│")}");
        }
        else
        {
            AnsiConsole.Markup(Markup.Escape(text));
        }
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
            "  Ctrl+C            Copy line",
            "  Ctrl+X            Cut line",
            "  Ctrl+V            Paste",
            "",
            "Navigation",
            "  Arrows/Home/End, PgUp/PgDn",
            "  Ctrl+G        Go to line",
            "",
            "  Esc or F1      Close help"
        };

        // Box sizing
        int innerMaxLine = content.Max(s => s.Length);
        int boxInnerW = Math.Min(innerMaxLine, rawW - 6);   // leave margins
        int boxW = boxInnerW + 2;                           // borders
        int boxH = Math.Min(content.Length + 2, rawH - 4);  // borders + margins

        int x = (rawW - boxW) / 2;
        int y = (rawH - boxH) / 2;

        // Clear the whole screen (help is full-screen mode)
        Console.SetCursorPosition(0, 0);
        Console.Clear();

        // Draw top border
        Console.SetCursorPosition(x, y);
        Console.Write("┌" + new string('─', boxInnerW) + "┐");

        // Draw content lines (clipped)
        int usableLines = boxH - 2;
        for (int i = 0; i < usableLines; i++)
        {
            Console.SetCursorPosition(x, y + 1 + i);

            string line = i < content.Length ? content[i] : "";
            if (line.Length > boxInnerW) line = line[..boxInnerW];
            else line = line.PadRight(boxInnerW);

            Console.Write("│" + line + "│");
        }

        // Draw bottom border
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

    public int UndoDepth => Math.Max(0, _undo.Count - 1);
    public int RedoDepth => _redo.Count;

    public EditorState()
    {
        ClearHistory(seedCurrent: true);
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

    public void CopyLineToClipboard()
    {
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
        SetMessage("Cut line.");
    }

    public void PasteClipboard()
    {
        var text = ClipboardService.GetText() ?? "";
        if (text.Length == 0) { SetMessage("Clipboard empty."); return; }

        PushUndo();

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = text.Split('\n');

        if (parts.Length == 1)
        {
            InsertTextCore(parts[0]);
            Dirty = true;
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
        SetMessage("Pasted.");
    }

    public void HandleKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.LeftArrow:
                if (CursorX > 0) CursorX--;
                else if (CursorY > 0) { CursorY--; CursorX = Lines[CursorY].Length; }
                break;

            case ConsoleKey.RightArrow:
                if (CursorX < Lines[CursorY].Length) CursorX++;
                else if (CursorY < Lines.Count - 1) { CursorY++; CursorX = 0; }
                break;

            case ConsoleKey.UpArrow:
                if (CursorY > 0) { CursorY--; CursorX = Math.Min(CursorX, Lines[CursorY].Length); }
                break;

            case ConsoleKey.DownArrow:
                if (CursorY < Lines.Count - 1) { CursorY++; CursorX = Math.Min(CursorX, Lines[CursorY].Length); }
                break;

            case ConsoleKey.Home:
                CursorX = 0;
                break;

            case ConsoleKey.End:
                CursorX = Lines[CursorY].Length;
                break;

            case ConsoleKey.PageUp:
                CursorY = Math.Max(0, CursorY - Math.Max(1, Console.WindowHeight - 6));
                CursorX = Math.Min(CursorX, Lines[CursorY].Length);
                break;

            case ConsoleKey.PageDown:
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
        PushUndo();
        InsertTextCore(s);
        Dirty = true;
    }

    private void InsertTextCore(string s)
    {
        var line = Lines[CursorY];
        Lines[CursorY] = line.Insert(CursorX, s);
        CursorX += s.Length;
    }

    private void NewLine()
    {
        PushUndo();

        var line = Lines[CursorY];
        var left = line[..CursorX];
        var right = line[CursorX..];

        Lines[CursorY] = left;
        Lines.Insert(CursorY + 1, right);

        CursorY++;
        CursorX = 0;
        Dirty = true;
    }

    private void Backspace()
    {
        if (CursorX == 0 && CursorY == 0)
            return;

        PushUndo();

        if (CursorX > 0)
        {
            var line = Lines[CursorY];
            Lines[CursorY] = line.Remove(CursorX - 1, 1);
            CursorX--;
            Dirty = true;
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
        }
    }

    private void Delete()
    {
        var line = Lines[CursorY];
        if (CursorX == line.Length && CursorY == Lines.Count - 1)
            return;

        PushUndo();

        if (CursorX < line.Length)
        {
            Lines[CursorY] = line.Remove(CursorX, 1);
            Dirty = true;
            return;
        }

        if (CursorY < Lines.Count - 1)
        {
            Lines[CursorY] = line + Lines[CursorY + 1];
            Lines.RemoveAt(CursorY + 1);
            Dirty = true;
        }
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
