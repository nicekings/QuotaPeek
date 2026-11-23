using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NhipPC
{
// "Kết nối AI": one card per service. The card shows the service's ProviderState and offers
// exactly one next step, plus a link to the official guide or the desktop app.
internal sealed partial class SettingsWindow
{
    SettingsCard guide;
    ComboBox source;
    Label sourceState;
    Button primary, secondary;
    CheckBox hook;
    string clientPath;
    long detectAt;
    static readonly HashSet<string> openClients = new HashSet<string>();
    readonly Dictionary<string, string> actionErrors = new Dictionary<string, string>();
    Task<Dictionary<string, string>> inventory;
    Dictionary<string, string> desktopPaths = new Dictionary<string, string>();
    long desktopScanAt;
    Action primaryAction;

    string SourceId
    {
        get { return Catalog.Providers[Math.Max(0, source.SelectedIndex)]; }
    }
    void BuildConnections()
    {
        var select = Card(3, UiText.Pick("Dịch vụ AI", "AI service"));
        Caption(select, UiText.Pick("Dịch vụ AI", "AI service"), 0, 0, 2, true);
        source = Picker(select, UiText.Pick("Dịch vụ AI", "AI service"), 1);
        source.Items.AddRange(Catalog.Providers.Select(Catalog.Name).ToArray());
        source.SelectedIndex = 0;
        source.SelectedIndexChanged += delegate
        {
            if (constructed)
                BuildGuide();
        };
    }
    static string Docs(string id)
    {
        switch (id)
        {
        case "codex":
            return "https://developers.openai.com/codex/cli/";
        case "claude":
            return "https://code.claude.com/docs/en/quickstart";
        case "antigravity":
            return "https://antigravity.google/docs/getting-started?tab=cli";
        case "grok":
            return "https://docs.x.ai/build/overview";
        default:
            return "https://support.google.com/gemini/answer/16275805?hl=en";
        }
    }
    static string ClientOf(string id)
    {
        return id == "codex" ? "Codex" : id == "gemini" ? "Gemini" : Tools.ClientName(id);
    }
    static string Steps(string id)
    {
        switch (id)
        {
        case "codex":
            return UiText.Pick("QuotaPeek đọc hạn mức Codex của gói ChatGPT qua Codex trên máy, khoảng 30 giây một lần. ", "QuotaPeek checks your ChatGPT plan’s Codex limits through the local Codex client about every 30 seconds. ") +
                   UiText.Pick("Bạn có thể đóng cửa sổ ChatGPT và Codex.", "You can close the ChatGPT and Codex windows.");
        case "claude":
            return UiText.Pick("QuotaPeek chạy lệnh /usage của Claude Code ở chế độ nền, khoảng 2 phút một lần. Lệnh này ", "QuotaPeek runs the Claude Code /usage command in the background about every two minutes. The command ") +
                   UiText.Pick("không gửi tin nhắn tới mô hình nên không tốn hạn mức. Bạn có thể đóng Claude Desktop.", "reads your usage without sending a model request. You can close Claude Desktop.");
        case "antigravity":
            return UiText.Pick("QuotaPeek chạy lệnh /usage của Antigravity CLI ở chế độ nền, khoảng 2 phút một lần. ", "QuotaPeek runs the Antigravity CLI /usage command in the background about every two minutes. ") +
                   UiText.Pick("Hiện hạn mức của mọi nhóm mô hình: Gemini, và Claude & GPT. Cần Antigravity CLI từ bản 1.1.11.", "It shows the limits for the Gemini and Claude & GPT model groups. Requires Antigravity CLI 1.1.11 or later.");
        case "grok":
            return UiText.Pick("QuotaPeek mở Grok Build ở chế độ ẩn để đọc bảng /usage, khoảng 3 phút một lần. ", "QuotaPeek opens Grok Build in the background to read /usage about every three minutes. ") +
                   UiText.Pick("Grok Build cần đăng nhập sẵn bằng tài khoản Grok.", "Sign in to your Grok account in Grok Build first.");
        default:
            return UiText.Pick("Gemini Chat chưa có công cụ dòng lệnh cho hạn mức. Trong Gemini cho Windows, mở Settings → ", "To read Gemini Chat limits, open Gemini for Windows and go to Settings → ") +
                   UiText.Pick("Usage Limits và giữ bảng hiển thị. QuotaPeek đọc bảng khoảng 30 giây một lần khi cửa sổ mở.", "Usage Limits. Keep that panel open. QuotaPeek checks it about every 30 seconds.");
        }
    }
    void BuildGuide()
    {
        bool prior = syncing;
        syncing = true;
        try
        {
            if (guide != null) guide.Dispose();
            hook = null;
            string id = SourceId;
            guide = Card(3, Catalog.Name(id));
            Caption(guide, Catalog.Name(id), 0, 0, 2, true);
            Caption(guide, Steps(id), 1);
            sourceState = Caption(guide, "", 2);
            primary = Put(guide, Ui.Button("", "", delegate { if (primaryAction != null) primaryAction(); }), 3, 0, 1);
            secondary = Put(guide, Ui.Button("", "", delegate { RunSecondary(id); }), 3, 1, 1);
            if (id == "claude" || id == "antigravity")
            {
                hook = Toggle(guide, UiText.Pick("Đọc lại khi phiên ", "Refresh when the ") + Tools.ClientName(id) + UiText.Pick(" có tin nhắn mới", " session receives a message"), 4, v => ToggleHook(id, v));
                Caption(guide, UiText.Pick("QuotaPeek thêm một status line vào ", "QuotaPeek adds a status line to ") + Tools.ClientName(id) +
                                       UiText.Pick(". Mở lại phiên sau khi đổi tùy chọn này.", ". Reopen your session after changing this setting."), 5);
            }
            detectAt = 0;
            UpdateGuide();
        }
        finally
        {
            syncing = prior;
        }
    }
    void ToggleHook(string id, bool on)
    {
        try
        {
            StatusLine.Toggle(id);
            if (on)
                EnableSource(id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UiText.FromReading(ex.Message), "QuotaPeek");
        }
        detectAt = 0;
        UpdateValues();
    }
    void EnableSource(string id)
    {
        if (P.On(id))
            return;
        P.ShowAi = true;
        P.Enabled[id] = true;
        app.Apply();
    }
    void Read(string id)
    {
        actionErrors.Remove(id);
        EnableSource(id);
        if (id == "gemini" && DesktopUsage.Windows(id).Count == 0 && desktopPaths.ContainsKey(id))
            LaunchDesktop(id);
        app.Hub.Refresh(id);
        UpdateValues();
    }
    void RunSecondary(string id)
    {
        if (id == "gemini")
            LaunchDesktop(id);
        else
            app.OpenLink(Docs(id));
    }

    // Decide the card's single next step from the provider's phase.
    void Plan(string id, ProviderState state, out string text, out bool enabled, out Action action)
    {
        string client = ClientOf(id);
        enabled = true;
        bool cli = id != "gemini";
        if (openClients.Contains(id))
        {
            text = UiText.Pick("Đang mở ", "Opening ") + client + "…";
            enabled = false;
            action = null;
            return;
        }
        if (!P.On(id))
        {
            text = UiText.Pick("Bật và đọc", "Enable and refresh");
            action = () => Read(id);
            return;
        }
        if (cli && (state.Phase == Phase.NeedsInstall || clientPath == null))
        {
            text = UiText.Pick("Cài ", "Install ") + client + "…";
            action = () => app.OpenLink(Docs(id));
            return;
        }
        if (state.Phase == Phase.NeedsLogin)
        {
            if (id == "codex")
            {
                text = UiText.Pick("Đăng nhập bằng ChatGPT…", "Sign in with ChatGPT…");
                action = () => LaunchClient(id, "login");
            }
            else if (id == "claude")
            {
                text = UiText.Pick("Đăng nhập Claude Code…", "Sign in to Claude Code…");
                action = () => LaunchClient(id, "auth login");
            }
            else
            {
                text = UiText.Pick("Mở ", "Open ") + client + "…";
                action = () => LaunchClient(id, "");
            }
            return;
        }
        if (state.Phase == Phase.NeedsSetup && (id == "grok" || id == "antigravity"))
        {
            text = UiText.Pick("Mở ", "Open ") + client + "…";
            action = () => LaunchClient(id, "");
            return;
        }
        if (state.Phase == Phase.Cooldown && state.RetryAt > Fmt.Now)
        {
            text = UiText.Pick("Đợi đến ", "Wait until ") + Fmt.Local(state.RetryAt, "HH:mm:ss");
            enabled = false;
            action = null;
            return;
        }
        text = state.Busy ? UiText.Pick("Đang đọc…", "Refreshing…") : id == "gemini" ? UiText.Pick("Đọc bảng Usage Limits", "Read Usage Limits") : UiText.Pick("Đọc lại hạn mức", "Refresh quota");
        enabled = !state.Busy;
        action = () => Read(id);
    }

    void UpdateGuide()
    {
        if (sourceState == null || sourceState.IsDisposed)
            return;
        string id = SourceId;
        if (Fmt.Now >= detectAt)
        {
            clientPath = id == "gemini" ? null : id == "codex" ? Tools.Find("codex") : Tools.Client(id);
            detectAt = Fmt.Now + 10;
            if (hook != null)
                hook.Checked = StatusLine.Installed(id);
        }
        var state = app.Hub[id];
        string text;
        bool enabled;
        Plan(id, state, out text, out enabled, out primaryAction);
        primary.Text = primary.AccessibleName = text;
        primary.Enabled = enabled;
        if (id == "gemini")
            UpdateDesktopButton(id);
        else
            secondary.Text = secondary.AccessibleName = UiText.Pick("Hướng dẫn chính thức…", "Official guide…");
        if (hook != null)
            hook.Enabled = clientPath != null || hook.Checked;
        sourceState.Text = Describe(id, state);
    }

    string Describe(string id, ProviderState state)
    {
        string error;
        if (actionErrors.TryGetValue(id, out error))
            return error;
        if (openClients.Contains(id))
            return id == "codex" || id == "claude"
                ? UiText.Pick("Hoàn tất đăng nhập trong trình duyệt. QuotaPeek tự đọc lại khi cửa sổ ", "Finish signing in through your browser. QuotaPeek will refresh when the ") + ClientOf(id) + UiText.Pick(" đóng.", " window closes.")
                : UiText.Pick("Làm theo hướng dẫn trong cửa sổ ", "Follow the instructions in the ") + ClientOf(id) + UiText.Pick(", rồi đóng cửa sổ đó. QuotaPeek sẽ tự đọc lại.", " window, then close it. QuotaPeek will refresh automatically.");
        if (!P.On(id))
            return UiText.Pick("Nguồn đang tắt. Bấm “Bật và đọc” để theo dõi ", "Tracking is off. Select “Enable and refresh” to track ") + Catalog.Name(id) + ".";
        var lines = new List<string>();
        var r = state.Snapshot;
        long now = Fmt.Now;
        switch (state.Phase)
        {
        case Phase.Live:
            lines.Add(UiText.Pick("Đã đọc lúc ", "Last checked at ") + Fmt.Local(r.Time, "HH:mm:ss") + " · " + r.Source +
                      (String.IsNullOrWhiteSpace(r.Plan) ? "" : UiText.Pick(" · Gói ", " · Plan: ") + r.Plan));
            break;
        case Phase.Connecting:
            lines.Add(UiText.FromReading(state.Message) ?? UiText.Pick("Đang kết nối ", "Connecting to ") + ClientOf(id) + "…");
            break;
        default:
            lines.Add(UiText.FromReading(state.Message) ?? UiText.Pick("Đang chờ dữ liệu.", "Waiting for data."));
            break;
        }
        if (r != null && state.ShowsNumbers)
        {
            string summary = String.Join("  ·  ", r.Windows.Where(q => P.On("ai/" + id + "/" + q.Key)).Take(3)
                                                       .Select(q => DisplayData.QuotaSummary(id, q, now)));
            if (summary.Length > 0)
                lines.Add(summary);
            if (state.Phase != Phase.Live)
                lines.Add(UiText.Pick("Số liệu lần cuối ", "Last reading at ") + Fmt.Local(r.Time, "dd/MM HH:mm:ss") + ".");
        }
        if (state.Phase != Phase.Cooldown && state.Phase != Phase.NeedsSetup && !state.Busy &&
            state.NextRead > now && state.NextRead < now + 86400 && id != "codex")
            lines.Add(UiText.Pick("Lần đọc tiếp theo lúc ", "Next check at ") + Fmt.Local(state.NextRead, "HH:mm:ss") + ".");
        return String.Join("\n", lines);
    }

    void UpdateDesktopButton(string id)
    {
        if (inventory != null && inventory.IsCompleted)
        {
            if (inventory.Status == TaskStatus.RanToCompletion)
                desktopPaths = inventory.Result;
            inventory = null;
        }
        if (inventory == null && Fmt.Now >= desktopScanAt)
        {
            inventory = Task.Run((Func<Dictionary<string, string>>)DesktopClients.Find);
            desktopScanAt = Fmt.Now + 60;
        }
        bool found = desktopPaths.ContainsKey(id);
        bool searching = !found && inventory != null;
        secondary.Text = secondary.AccessibleName =
            searching ? UiText.Pick("Đang tìm Gemini…", "Looking for Gemini…") : found ? UiText.Pick("Mở Gemini cho Windows", "Open Gemini for Windows") : UiText.Pick("Tải Gemini cho Windows…", "Download Gemini for Windows…");
        secondary.Enabled = !searching;
    }
    void LaunchDesktop(string id)
    {
        string path;
        if (!desktopPaths.TryGetValue(id, out path))
        {
            app.OpenLink("https://gemini.google/desktop/");
            return;
        }
        if (!path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
        {
            app.OpenLink(path);
            return;
        }
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            Process.Start(new ProcessStartInfo(explorer, "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, UiText.FromReading(ex.Message), "QuotaPeek");
        }
    }

    // Open the official client in a visible window for login or first-run steps, then read again.
    async void LaunchClient(string id, string args)
    {
        if (clientPath == null)
        {
            app.OpenLink(Docs(id));
            return;
        }
        if (!openClients.Add(id))
            return;
        actionErrors.Remove(id);
        try
        {
            EnableSource(id);
            UpdateValues();
            using (var session = Process.Start(new ProcessStartInfo(clientPath, args) {
                WorkingDirectory = Store.Root, WindowStyle = ProcessWindowStyle.Normal,
                UseShellExecute = id != "codex" }))
            {
                if (session == null)
                    throw new InvalidOperationException(UiText.Pick("Windows chưa mở được cửa sổ ", "Windows could not open ") + ClientOf(id) + ".");
                await Task.Run((Action)session.WaitForExit);
                if (args.Length > 0 && session.ExitCode != 0)
                    actionErrors[id] = UiText.Pick("Đăng nhập chưa hoàn tất. Bấm nút đăng nhập để thử lại.", "Sign-in is incomplete. Select the sign-in button to try again.");
            }
            if (P.On(id))
                app.Hub.Refresh(id);
        }
        catch (Exception ex)
        {
            actionErrors[id] = UiText.Pick("Không mở được ", "Could not open ") + ClientOf(id) + ". " + ex.Message;
        }
        finally
        {
            openClients.Remove(id);
            if (!IsDisposed && !Disposing)
                UpdateValues();
        }
    }
}
}
