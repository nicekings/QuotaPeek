using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NhipPC
{
internal static class CliUsage
{
    // A numbered or named menu choice may also start with a chevron.
    static readonly Regex InputPrompt = new Regex(
        @"^[ \t│┃]*[❯›>][ \t]*(?:(?:type (?:a |your )?(?:message|prompt)|try [""'“]).*)?[ \t│┃]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    static string SetupNotice(string provider, string screen)
    {
        string client = Tools.ClientName(provider);
        if (Regex.IsMatch(screen, @"trust (?:this|the) (?:folder|directory|workspace|files)|" +
                                  @"is this a project you (?:created|trust)|ready to code here"))
            return client + " yêu cầu bạn xác nhận độ tin cậy của thư mục dùng để đọc hạn mức.";
        if (Regex.IsMatch(screen, @"choose the text style|(?:choose|select) (?:a|your) (?:color )?theme"))
            return client + " đang chờ bạn chọn giao diện cho cửa sổ dòng lệnh.";
        if (Regex.IsMatch(screen, @"(?:accept|review|agree to) (?:the |our |updated |new )*terms\b"))
            return client + " đang chờ bạn xem điều khoản sử dụng.";
        if (Regex.IsMatch(screen, @"\b(?:enter|return) to (?:confirm|select|accept|continue)\b"))
            return client + " đang chờ bạn trả lời hộp thoại trong cửa sổ dòng lệnh.";
        return null;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Size
    {
        public short X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Startup
    {
        public int cb;
        public IntPtr reserved, desktop, title;
        public int x, y, width, height, cols, rows, fill, flags;
        public short show, bytes;
        public IntPtr reserved2, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Extended
    {
        public Startup startup;
        public IntPtr attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Child
    {
        public IntPtr process, thread;
        public int pid, tid;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security,
                                  uint size);
    [DllImport("kernel32.dll")]
    static extern int CreatePseudoConsole(Size size, SafeFileHandle input, SafeFileHandle output, uint flags,
                                          out IntPtr console);
    [DllImport("kernel32.dll")]
    static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr key, IntPtr value, IntPtr size,
                                                 IntPtr previous, IntPtr ret);
    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttr, IntPtr threadAttr,
                                     bool inherit, uint flags, IntPtr env, string cwd, ref Extended startup,
                                     out Child child);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    // Claude has its own structured reader. Antigravity answers in JSON. Grok Build only
    // shows /usage in its terminal interface, so it is the one client read from the screen.
    public static Reading Read(string provider)
    {
        if (provider == "claude")
            return ClaudeUsage.Read();
        string source = Tools.ClientName(provider);
        var blank = Reading.Start(provider, source);
        string exe = Tools.Client(provider);
        if (exe == null)
        {
            blank.NeedsInstall = true;
            blank.Notice = "Chưa tìm thấy " + Tools.ClientName(provider) + " trên máy.";
            return blank;
        }
        if (provider == "antigravity")
            return CliIdentity.Antigravity(exe, source);
        blank.Notice = "Grok Build chưa xác nhận phiên đăng nhập nền.";
        if (provider == "grok" && CliIdentity.GrokSignedIn(exe, blank))
            return ReadPanel(provider, source, exe);
        return blank;
    }
    static Reading ReadPanel(string provider, string source, string exe)
    {
        var blank = Reading.Start(provider, source);
        blank.Notice = "Grok Build chưa hiển thị bảng /usage. QuotaPeek sẽ tự thử lại.";
        SafeFileHandle fromInput, toInput, fromOutput, toOutput;
        if (!CreatePipe(out fromInput, out toInput, IntPtr.Zero, 0))
            return blank;
        using (fromInput) using (toInput)
        {
            if (!CreatePipe(out fromOutput, out toOutput, IntPtr.Zero, 0))
                return blank;
            using (fromOutput) using (toOutput)
            {
                IntPtr pc;
                if (CreatePseudoConsole(new Size { X = 128, Y = 44 }, fromInput, toOutput, 0, out pc) != 0)
                    return blank;
                fromInput.Dispose();
                toOutput.Dispose();
                var screen = new TerminalScreen(128, 44);
                IntPtr attr = IntPtr.Zero;
                var child = new Child();
                bool initialized = false;
                using (var input = new FileStream(toInput, FileAccess.Write)) using (
                    var output = new FileStream(fromOutput, FileAccess.Read))
                {
                    Task pump = Task.Factory.StartNew(delegate {
                        try
                        {
                            using (var reader =
                                       new StreamReader(output, new UTF8Encoding(false), false, 4096, true))
                            {
                                var buf = new char[2048];
                                int n;
                                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                                    screen.Feed(buf, n);
                            }
                        }
                        catch (IOException)
                        {
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }, TaskCreationOptions.LongRunning);
                    try
                    {
                        IntPtr size = IntPtr.Zero;
                        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                        attr = Marshal.AllocHGlobal(size);
                        initialized = InitializeProcThreadAttributeList(attr, 1, 0, ref size);
                        if (!initialized ||
                            !UpdateProcThreadAttribute(attr, 0, new IntPtr(0x20016), pc,
                                                       new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                            return blank;
                        var start = new Extended { attributes = attr };
                        start.startup.cb = Marshal.SizeOf(typeof(Extended));
                        // Prevent Windows from duplicating the worker's redirected pipes into the TUI.
                        start.startup.flags = 0x100; // STARTF_USESTDHANDLES, null handles attach to ConPTY.
                        if (!CreateProcess(exe, new StringBuilder("\"" + exe + "\""), IntPtr.Zero, IntPtr.Zero,
                                           false, 0x80000, IntPtr.Zero, Store.Root, ref start, out child))
                            return blank;
                        var clock = Stopwatch.StartNew();
                        bool sent = false;
                        string previousPrompt = null;
                        string last = null;
                        int stable = 0;
                        while (clock.Elapsed.TotalSeconds < 13 && WaitForSingleObject(child.process, 0) == 258)
                        {
                            Thread.Sleep(180);
                            string text = screen.Text();
                            // Example prompts are editable placeholders, not instructions from a dialog.
                            string visible = sent ? text : InputPrompt.Replace(text, "");
                            string lower = Fmt.Fold(visible);
                            if (CliIdentity.Failure(blank, visible))
                                return blank;
                            if (!sent)
                            {
                                string setup = SetupNotice(provider, Regex.Replace(lower, @"\s+", " "));
                                // An incomplete repaint can briefly contain only the selector or an input row.
                                string prompt = setup ?? (InputPrompt.IsMatch(text) ? "" : null);
                                if (prompt == null || prompt != previousPrompt)
                                {
                                    previousPrompt = prompt;
                                    continue;
                                }
                                if (setup != null)
                                {
                                    blank.NeedsSetup = true;
                                    blank.Notice = setup;
                                    return blank;
                                }
                                byte[] cmd = Encoding.UTF8.GetBytes("/usage\r");
                                input.Write(cmd, 0, cmd.Length);
                                input.Flush();
                                sent = true;
                            }
                            else if ((lower.Contains("usage") || lower.Contains("quota") ||
                                      lower.Contains("current session") || lower.Contains("weekly limit")) &&
                                     text.Contains("%"))
                            {
                                var r = UsagePanel.Parse(provider, source, text.Split('\n'));
                                if (r.Windows.Count == 0)
                                    continue;
                                string observed = String.Join("|", r.Windows.Select(w => w.Key + "=" + w.Used));
                                stable = observed == last ? stable + 1 : 0;
                                last = observed;
                                if (stable >= 2)
                                {
                                    if (provider == "grok")
                                    {
                                        var panel = text.Split('\n').Select(line => line.Trim())
                                            .SkipWhile(line => !UsagePanel.Heading(line, provider));
                                        AccountPanel.Bind(r, panel);
                                        if (r.AccountKey == null || String.IsNullOrWhiteSpace(r.Plan))
                                            r.Notice = "Bảng Usage của Grok Build chưa xác nhận tài khoản và gói.";
                                    }
                                    return r;
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (child.process != IntPtr.Zero)
                        {
                            if (WaitForSingleObject(child.process, 0) == 258)
                                TerminateProcess(child.process, 0);
                            WaitForSingleObject(child.process, 1000);
                            CloseHandle(child.thread);
                            CloseHandle(child.process);
                        }
                        ClosePseudoConsole(pc);
                        if (initialized)
                            DeleteProcThreadAttributeList(attr);
                        if (attr != IntPtr.Zero)
                            Marshal.FreeHGlobal(attr);
                        pump.Wait(500);
                    }
                }
            }
        }
        return blank;
    }
}

// Reconstruct cursor-positioned TUI rows before matching a period to its percentage.
internal sealed class TerminalScreen
{
    readonly char[,] cells;
    readonly int width, height;
    int x, y, savedX, savedY, state;
    readonly StringBuilder escape = new StringBuilder();
    public TerminalScreen(int width, int height)
    {
        this.width = width;
        this.height = height;
        cells = new char[height, width];
    }
    public void Feed(char[] data, int n)
    {
        lock (cells) for (int k = 0; k < n; k++)
        {
            char c = data[k];
            if (state == 3)
            {
                if (c == '\a')
                    state = 0;
                else if (c == (char)27)
                    state = 4;
                continue;
            }
            if (state == 4)
            {
                state = c == '\\' ? 0 : 3;
                continue;
            }
            if (state == 1)
            {
                state = c == '[' ? 2 : (c == ']' || c == 'P') ? 3 : 0;
                if (c == '7')
                {
                    savedX = x;
                    savedY = y;
                }
                if (c == '8')
                {
                    x = savedX;
                    y = savedY;
                }
                escape.Clear();
                continue;
            }
            if (state == 2)
            {
                if (c >= '@' && c <= '~')
                {
                    Command(c, escape.ToString());
                    state = 0;
                }
                else if (escape.Length < 80)
                    escape.Append(c);
                continue;
            }
            if (c == (char)27)
            {
                state = 1;
                continue;
            }
            if (c == '\r')
            {
                x = 0;
                continue;
            }
            if (c == '\n')
            {
                Down();
                continue;
            }
            if (c == '\b')
            {
                x = Math.Max(0, x - 1);
                continue;
            }
            if (c == '\t')
            {
                x = Math.Min(width - 1, 8 * (x / 8 + 1));
                continue;
            }
            if (c < ' ')
                continue;
            if (x >= width)
            {
                x = 0;
                Down();
            }
            cells[y, x++] = c;
        }
    }
    void Down()
    {
        if (y < height - 1)
        {
            y++;
            return;
        }
        for (int row = 1; row < height; row++)
            for (int col = 0; col < width; col++)
                cells[row - 1, col] = cells[row, col];
        ClearRow(height - 1, 0, width);
    }
    void ClearRow(int row, int start, int end)
    {
        for (int i = Math.Max(0, start); i < Math.Min(width, end); i++)
            cells[row, i] = ' ';
    }
    void Command(char c, string args)
    {
        if (args.StartsWith("?"))
        {
            if (args == "?1049" && c == 'h')
            {
                Array.Clear(cells, 0, cells.Length);
                x = y = 0;
            }
            return;
        }
        var fields = args.Split(';');
        int a, b = 0;
        Int32.TryParse(fields[0], out a);
        if (fields.Length > 1)
            Int32.TryParse(fields[1], out b);
        int count = Math.Max(1, a);
        switch (c)
        {
        case 'H':
        case 'f':
            y = count - 1;
            x = Math.Max(0, b - 1);
            break;
        case 'G':
            x = count - 1;
            break;
        case 'd':
            y = count - 1;
            break;
        case 'A':
            y -= count;
            break;
        case 'B':
            y += count;
            break;
        case 'C':
            x += count;
            break;
        case 'D':
            x -= count;
            break;
        case 'J':
            if (a >= 2)
                Array.Clear(cells, 0, cells.Length);
            else if (a == 0)
            {
                ClearRow(y, x, width);
                for (int row = y + 1; row < height; row++)
                    ClearRow(row, 0, width);
            }
            else
            {
                for (int row = 0; row < y; row++)
                    ClearRow(row, 0, width);
                ClearRow(y, 0, x + 1);
            }
            break;
        case 'K':
            ClearRow(y, a == 0 ? x : 0, a == 1 ? x + 1 : width);
            break;
        case 'X':
            ClearRow(y, x, x + count);
            break;
        case 's':
            savedX = x;
            savedY = y;
            break;
        case 'u':
            x = savedX;
            y = savedY;
            break;
        }
        x = Math.Max(0, Math.Min(width - 1, x));
        y = Math.Max(0, Math.Min(height - 1, y));
    }
    public string Text()
    {
        lock (cells)
        {
            var b = new StringBuilder();
            for (int row = 0; row < height; row++)
            {
                var line = new char[width];
                for (int col = 0; col < width; col++)
                    line[col] = cells[row, col] == '\0' ? ' ' : cells[row, col];
                b.AppendLine(new string(line).TrimEnd());
            }
            return b.ToString();
        }
    }
}
}
