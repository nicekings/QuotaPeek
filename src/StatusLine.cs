using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NhipPC
{
public class HookBackup
{
    public string Command { get; set; }
    public string Original { get; set; }
}
internal static class StatusLine
{
    public static int Receive(string id, bool tee)
    {
        if (id != "claude" && id != "antigravity")
            return 2;
        string input;
        using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, true))
        {
            var chars = new char[4096];
            var data = new StringBuilder();
            int n;
            while ((n = reader.Read(chars, 0, chars.Length)) > 0)
            {
                if (data.Length + n > 4194304)
                    return 2;
                data.Append(chars, 0, n);
            }
            input = data.ToString();
        }
        string output = "";
        try
        {
            var preferences = Store.Load();
            UiText.SetLanguage(preferences.Language);
            if (preferences.On(id))
            {
                // Only a wake-up signal: QuotaPeek reads the verified numbers itself, right away.
                Store.Write(Store.PathFor("bell-" + id), Json.Encode(new { Time = Fmt.Now }));
                var r = QuotaReaders.StatusLine(id, Json.Parse(input));
                long now = Fmt.Now;
                output = String.Join("  ·  ", r.Windows.Select(w => DisplayData.QuotaSummary(id, w, now)));
            }
        }
        catch (Exception)
        { /* A disconnected widget must not break the existing terminal status line. */
        }
        using (var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
            writer.Write(tee ? input : output);
        return 0;
    }
    static string Home
    {
        get {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
    public static string Config(string id)
    {
        if (id == "claude")
            return Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
                                    Path.Combine(Home, ".claude"),
                                "settings.json");
        if (id == "antigravity")
            return Path.Combine(Home, ".gemini", "antigravity-cli", "settings.json");
        throw new ArgumentException("Nguồn CLI không hợp lệ.");
    }
    static string Backup(string id)
    {
        return Store.PathFor("hook-" + id);
    }
    public static bool Installed(string id)
    {
        var old = Store.Read<HookBackup>(Backup(id));
        if (old == null)
            return false;
        try
        {
            return Json.Str(Json.Obj(Json.Parse(File.ReadAllText(Config(id))), "statusLine"), "command") ==
                   old.Command;
        }
        catch
        {
            return false;
        }
    }
    static bool UsesBash()
    {
        var custom = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        return (!String.IsNullOrEmpty(custom) && File.Exists(custom)) || Tools.Find("git") != null ||
               File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                        "Git", "bin", "bash.exe"));
    }
    public static void Toggle(string id)
    {
        string path = Config(id), original = File.Exists(path) ? File.ReadAllText(path) : "{}";
        var doc = Json.Parse(original);
        if (doc == null)
            throw new InvalidDataException("Tệp cài đặt CLI không hợp lệ.");
        var current = Json.Obj(doc, "statusLine");
        var previous = Store.Read<HookBackup>(Backup(id));
        if (previous != null && Json.Str(current, "command") == previous.Command)
        {
            WriteIfCurrent(path, original, Patch(original, previous.Original));
            File.Delete(Backup(id));
            return;
        }
        string oldCmd = Json.Str(current, "command");
        bool chain = !String.IsNullOrWhiteSpace(oldCmd) && !Object.Equals(Json.Get(current, "enabled"), false);
        string command = Invoke(id) + (chain ? " --tap " : " --status-line ") + id;
        if (chain)
            command += id == "claude" && !UsesBash() ? " | & { " + oldCmd + " }" : " | (" + oldCmd + ")";
        var entry =
            current == null ? new Dictionary<string, object>() : new Dictionary<string, object>(current);
        entry["type"] = "command";
        entry["command"] = command;
        if (id == "antigravity")
        {
            entry["enabled"] = true;
            if (!chain && !entry.ContainsKey("stack_with_default"))
                entry["stack_with_default"] = true;
        }
        var backup = new HookBackup { Command = command, Original = RawProperty(original) };
        Store.Write(Backup(id), Json.Encode(backup));
        WriteIfCurrent(path, original, Patch(original, Json.Encode(entry)));
    }
    static string Invoke(string id)
    {
        bool bash = id == "claude" && UsesBash(), ps = id == "claude" && !bash;
        string exe = Tools.Self.Replace('\\', '/');
        return ps     ? "& '" + exe.Replace("'", "''") + "'"
               : bash ? "'" + exe.Replace("'", "'\\''") + "'"
                      : "\"" + exe.Replace('/', '\\') + "\"";
    }
    // After QuotaPeek.exe moves, point the status line this app installed at the new file.
    // A command the user edited since then is left alone.
    public static void Repair(string id)
    {
        var old = Store.Read<HookBackup>(Backup(id));
        string path = Config(id);
        if (old == null || !File.Exists(path))
            return;
        string original = File.ReadAllText(path);
        var current = Json.Obj(Json.Parse(original), "statusLine");
        string command = Json.Str(current, "command");
        if (command == null || command != old.Command)
            return;
        int at = -1;
        foreach (string flag in new[] { " --status-line " + id, " --tap " + id })
            if (at < 0)
                at = command.IndexOf(flag, StringComparison.Ordinal);
        if (at < 0)
            return;
        string replaced = Invoke(id) + command.Substring(at);
        if (replaced == command)
            return;
        var entry = new Dictionary<string, object>(current);
        entry["command"] = replaced;
        WriteIfCurrent(path, original, Patch(original, Json.Encode(entry)));
        Store.Write(Backup(id), Json.Encode(new HookBackup { Command = replaced, Original = old.Original }));
    }
    static void WriteIfCurrent(string path, string original, string next)
    {
        Json.Parse(next);
        if (File.Exists(path) && File.ReadAllText(path) != original)
            throw new IOException("Cài đặt CLI vừa thay đổi. Vui lòng thử kết nối lại.");
        Store.Write(path, next);
    }
    // Locate just the root statusLine value, keeping all other properties and formatting intact.
    static int Skip(string s, int i)
    {
        while (i < s.Length && Char.IsWhiteSpace(s[i]))
            i++;
        return i;
    }
    static int StringEnd(string s, int i)
    {
        for (i++; i < s.Length; i++)
        {
            if (s[i] == '\\')
                i++;
            else if (s[i] == '"')
                return i + 1;
        }
        throw new InvalidDataException();
    }
    static int End(string s, int i)
    {
        if (s[i] == '"')
            return StringEnd(s, i);
        int depth = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"')
            {
                i = StringEnd(s, i) - 1;
                continue;
            }
            if (c == '{' || c == '[')
                depth++;
            else if (c == '}' || c == ']')
            {
                if (depth == 0)
                    return i;
                depth--;
            }
            else if (c == ',' && depth == 0)
                return i;
        }
        return i;
    }
    static int[] Span(string s)
    {
        int i = Skip(s, Skip(s, 0) + 1), lastComma = -1;
        while (i < s.Length && s[i] != '}')
        {
            int key = i, k = StringEnd(s, i);
            string name = Json.Decode<string>(s.Substring(i, k - i));
            int v = Skip(s, Skip(s, k) + 1), end = End(s, v), trim = end;
            while (trim > v && Char.IsWhiteSpace(s[trim - 1]))
                trim--;
            if (name == "statusLine")
                return new[] { key, v, trim, lastComma, end };
            if (end >= s.Length || s[end] != ',')
                break;
            lastComma = end;
            i = Skip(s, end + 1);
        }
        return null;
    }
    static string RawProperty(string s)
    {
        var a = Span(s);
        return a == null ? null : s.Substring(a[1], a[2] - a[1]);
    }
    internal static string Patch(string s, string value)
    {
        var a = Span(s);
        if (a != null)
        {
            if (value != null)
                return s.Substring(0, a[1]) + value + s.Substring(a[2]);
            if (s[a[4]] == ',')
                return s.Remove(a[0], a[4] + 1 - a[0]);
            int start = a[3] < 0 ? a[0] : a[3];
            return s.Remove(start, a[2] - start);
        }
        if (value == null)
            return s;
        int end = s.LastIndexOf('}');
        string before = s.Substring(0, end).TrimEnd();
        return before + (before.EndsWith("{") ? "" : ",") + Environment.NewLine + "  \"statusLine\": " + value +
               Environment.NewLine + s.Substring(end);
    }
}
}
