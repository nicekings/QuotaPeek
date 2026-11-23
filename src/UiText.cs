using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NhipPC
{
internal static class UiText
{
    static bool english;
    public static void SetLanguage(string language) { english = language == "en"; }
    public static string Pick(string vi, string en) { return english ? en : vi; }
    public static CultureInfo NumberCulture { get { return english ? CultureInfo.InvariantCulture : Fmt.Vi; } }

    // Translate local messages from workers and earlier cached readings only when displayed.
    static readonly Dictionary<string, string> readings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "Còn lại ", "Remaining " },
        { " ngày", " d" },
        { " giờ", " h" },
        { " phút", " min" },
        { "Còn ", "Remaining " },
        { "Mạng", "Network" },
        { "Đọc / ghi", "Read / write" },
        { "Chưa tìm thấy Claude Code trên máy.", "Claude Code was not found on this PC." },
        { "Claude Code chưa trả kết quả /usage. QuotaPeek sẽ tự thử lại.", "Claude Code has not returned /usage results. QuotaPeek will retry." },
        { "Claude Code vừa đổi tài khoản hoặc gói. Chờ lần đọc tiếp theo.", "Your Claude Code account or plan just changed. Waiting for the next check." },
        { "Claude Code không chạy /usage như lệnh nội bộ nên QuotaPeek đã dừng đọc tự động để ", "Claude Code did not recognize /usage as a built-in command. QuotaPeek paused automatic checks to " },
        { "không tốn hạn mức. Cập nhật Claude Code rồi bấm “Đọc lại”.", "avoid using quota. Update Claude Code and select “Refresh quota”." },
        { "Claude Code chưa lấy được hạn mức của gói. QuotaPeek sẽ tự thử lại.", "Claude Code could not retrieve your plan limits. QuotaPeek will retry." },
        { "Claude Code chưa trả hạn mức. QuotaPeek sẽ tự thử lại.", "Claude Code has not returned quota data. QuotaPeek will retry." },
        { "5 giờ", "5 hours" },
        { "Tuần", "Weekly" },
        { "Tuần · ", "Weekly · " },
        { "Gói Claude hiện chưa có kỳ hạn mức nào để hiển thị.", "Your Claude plan has no quota periods to show yet." },
        { "Dịch vụ yêu cầu chờ trước khi đọc lại.", "The service has requested a delay before the next check." },
        { "Claude yêu cầu xác minh tài khoản. Kiểm tra thông báo trong cửa sổ đăng nhập chính thức của Claude.", "Claude requires account verification. Check the notice in the official Claude sign-in window." },
        { "Claude chưa cho phép đọc hạn mức. Kiểm tra thông báo tài khoản trong Claude trước khi thử lại.", "Claude has not granted access to quota data. Check your account notices in Claude and try again." },
        { "Mở CLI để kiểm tra phiên đăng nhập hoặc hoàn tất xác minh.", "Open the CLI to check your session or finish verification." },
        { "Claude Code chưa xác nhận được tài khoản và gói.", "Claude Code could not confirm your account and plan." },
        { "Claude Code chưa xác nhận được trạng thái đăng nhập. Kiểm tra lại sau.", "Claude Code could not confirm your sign-in status. Check again later." },
        { "Claude Code chưa đăng nhập. Đăng nhập bằng tài khoản có gói Claude của bạn.", "Sign in to Claude Code with the account that has your Claude subscription." },
        { "Claude Code đã đăng nhập nhưng chưa báo loại tài khoản. Kiểm tra lại tài khoản đang dùng trong Claude Code.", "Claude Code has not reported your account type. Check which account is signed in." },
        { "Claude Code đang kết nối qua API. Đăng nhập bằng tài khoản có gói Claude để đọc hạn mức thuê bao.", "Claude Code is using the API. Sign in with your Claude subscription account to view its quota." },
        { "Claude Code đang dùng cách đăng nhập khác. Đăng nhập bằng tài khoản có gói Claude để đọc hạn mức thuê bao.", "Claude Code is using another sign-in method. Sign in with your Claude subscription account to view its quota." },
        { "Bạn đã đăng nhập Claude. Claude Code chưa báo tên gói. Kiểm tra gói đang dùng trong Claude rồi đọc lại.", "Claude is signed in but has not reported a plan. Check your plan in Claude, then refresh." },
        { "Bạn đã đăng nhập Claude. Claude Code chưa cung cấp đủ thông tin để xác minh tài khoản và gói đang dùng.", "Claude is signed in but has not provided enough information to verify the account and plan." },
        { "Cần Antigravity CLI từ 1.1.11 để đọc lệnh /usage ở chế độ nền.", "Antigravity CLI 1.1.11 or later is required to read /usage in the background." },
        { "Antigravity CLI chưa trả được hạn mức tài khoản.", "Antigravity CLI has not returned your account limits." },
        { "Bảng /usage của Antigravity CLI chưa ghi tài khoản. Đăng nhập lại trong Antigravity CLI.", "The Antigravity /usage panel does not list an account. Sign in again in Antigravity CLI." },
        { "Bản /usage thiếu tài khoản hoặc gói. Bật nhận sự kiện status line của Antigravity.", "The /usage report is missing an account or plan. Enable Antigravity status line events." },
        { "Mở Grok Build và đăng nhập trước khi bật đọc nền.", "Open Grok Build and sign in before enabling background checks." },
        { "Cần đăng nhập Grok Build.", "Sign in to Grok Build." },
        { " yêu cầu bạn xác nhận độ tin cậy của thư mục dùng để đọc hạn mức.", " is waiting for you to confirm that you trust the folder used to check quota." },
        { " đang chờ bạn chọn giao diện cho cửa sổ dòng lệnh.", " is waiting for you to choose a terminal theme." },
        { " đang chờ bạn xem điều khoản sử dụng.", " is waiting for you to review its terms of use." },
        { " đang chờ bạn trả lời hộp thoại trong cửa sổ dòng lệnh.", " is waiting for a response in the terminal window." },
        { "Chưa tìm thấy ", "Could not find " },
        { " trên máy.", " on this PC." },
        { "Grok Build chưa xác nhận phiên đăng nhập nền.", "Grok Build has not confirmed its background sign-in session." },
        { "Grok Build chưa hiển thị bảng /usage. QuotaPeek sẽ tự thử lại.", "Grok Build has not displayed /usage yet. QuotaPeek will retry." },
        { "Bảng Usage của Grok Build chưa xác nhận tài khoản và gói.", "The Grok Build usage panel has not confirmed the account and plan." },
        { "Codex đang tạm dừng theo yêu cầu của dịch vụ.", "Codex paused checks at the service’s request." },
        { "Chưa tìm thấy Codex trên máy.", "Codex was not found on this PC." },
        { "Mất kết nối Codex. Đang thử lại.", "Lost the Codex connection. Retrying." },
        { "Codex đang dùng API key. Đăng nhập bằng ChatGPT để đọc hạn mức của gói.", "Codex is using an API key. Sign in with ChatGPT to read your plan limits." },
        { "Cần đăng nhập Codex bằng tài khoản ChatGPT.", "Sign in to Codex with your ChatGPT account." },
        { "Codex chưa xác nhận được tài khoản và gói.", "Codex could not confirm your account and plan." },
        { "Luồng cập nhật Codex bị gián đoạn. Đang xác minh lại tài khoản.", "Codex updates were interrupted. Verifying your account again." },
        { "Codex chưa phản hồi. Đang thử lại.", "Codex has not responded. Retrying." },
        { "Codex đang giới hạn lượt đọc.", "Codex is limiting how often usage can be checked." },
        { "Codex từ chối quyền đọc hạn mức. Kiểm tra tài khoản đang đăng nhập.", "Codex denied access to quota data. Check which account is signed in." },
        { "Codex chưa cho phép đọc hạn mức. Đăng nhập lại bằng ChatGPT.", "Codex has not granted access to quota data. Sign in with ChatGPT again." },
        { "Codex chưa cung cấp hạn mức.", "Codex has not provided any limits yet." },
        { "Codex chưa xác nhận được tài khoản. Đang chờ đọc lại.", "Codex could not confirm your account. Waiting for another check." },
        { "Tài khoản Codex chưa có số liệu hạn mức.", "Your Codex account has no quota data yet." },
        { "Tài khoản, workspace hoặc gói Codex đã thay đổi. Đang đọc lại.", "Your Codex account, workspace or plan has changed. Refreshing." },
        { "Ổ đĩa", "Disk" },
        { "Chờ cập nhật", "Awaiting update" },
        { "Kỳ sử dụng", "Usage period" },
        { "vừa xong", "just now" },
        { " giây trước", " sec ago" },
        { " phút trước", " min ago" },
        { " giờ trước", " h ago" },
        { "Mức sử dụng", "Usage" },
        { "Giới hạn sử dụng", "Usage limits" },
        { "Hạn mức", "Quota" },
        { "Hạn mức sử dụng", "Usage limits" },
        { "Mở hoặc khôi phục cửa sổ ", "Open or restore the " },
        { ", rồi chọn ", " window, then select " },
        { "Đã thấy số hạn mức. QuotaPeek cần thấy email và gói trong cùng bảng để xác minh tài khoản.", "Quota found. The same panel must show your email and plan so QuotaPeek can verify the account." },
        { "Đã thấy ", "Found " },
        { ". QuotaPeek đang chờ bảng hiển thị số hạn mức.", ". Waiting for the panel to display quota values." },
        { "Chưa đọc được cửa sổ ", "Could not read the " },
        { ". Giữ bảng ", " window. Keep the " },
        { " hiển thị để thử lại.", " panel visible and try again." },
        { "Chưa thấy bảng hạn mức. Trong ", "Quota panel not found. In " },
        { ", mở ", ", open " },
        { " và giữ bảng đó hiển thị.", " and keep that panel visible." },
        { "Có nhiều bảng hạn mức. Chỉ mở bảng của tài khoản cần theo dõi.", "Multiple quota panels found. Leave only the account you want to track open." },
        { "Nguồn chưa phản hồi. QuotaPeek sẽ tự thử lại.", "The source has not responded. QuotaPeek will retry." },
        { "Cần cài ", "Install " },
        { "Cần đăng nhập.", "Sign-in required." },
        { "Cần hoàn tất thiết lập trong ", "Finish setup in " },
        { "Nguồn chưa trả hạn mức của một tài khoản đã xác minh.", "The source has not returned quota data for a verified account." },
        { "Dịch vụ yêu cầu chờ. Đọc lại từ ", "The service requested a delay. Try again after " },
        { " Đọc lại từ ", " Try again after " },
        { "Kỳ chính", "Primary period" },
        { "Kỳ phụ", "Secondary period" },
        { "Số dư credit", "Credit balance" },
        { "Chi tiêu", "Spending" },
        { " · Tuần", " · Weekly" },
        { " · 5 giờ", " · 5 hours" },
        { "Nguồn CLI không hợp lệ.", "Invalid CLI source." },
        { "Tệp cài đặt CLI không hợp lệ.", "Invalid CLI settings file." },
        { "Cài đặt CLI vừa thay đổi. Vui lòng thử kết nối lại.", "CLI settings have changed. Please reconnect." },
        { "Đang kết nối Codex…", "Connecting to Codex…" },
        { "Đang đọc lại tài khoản Codex…", "Checking your Codex account again…" },
        { "Đang đọc lại hạn mức Codex…", "Refreshing Codex quota…" },
    };
    static readonly Lazy<Regex> phrases = new Lazy<Regex>(() => new Regex(String.Join("|",
        readings.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)), RegexOptions.CultureInvariant));

    public static string FromReading(string value)
    {
        if (!english || String.IsNullOrEmpty(value)) return value;
        string translated;
        if (readings.TryGetValue(value, out translated)) return translated;
        return phrases.Value.Replace(value, m => readings[m.Value]);
    }
}
}
