// AIUsageStatus — Claude Code 상태 표시줄(--statusline)과 값 확인(--print)
//   settings.json: "statusLine": { "type": "command", "command": "C:/.../AIUsageStatus.exe --statusline" }

using System.Text;
using AIUsage;

var utf8 = new UTF8Encoding(false);

void Write(string text)
{
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(utf8.GetBytes(text));
}

if (args.Contains("--trend-json") || args.Contains("--test-alerts") || args.Contains("--record-once") || args.Contains("--alerts-once"))
{
    try
    {
        var i = Array.IndexOf(args,"--test-alerts");
        if (i >= 0) Write(UsageCommands.TestAlerts(args[i+1]) + "\n");
        else if (args.Contains("--alerts-once")) Write(UsageCommands.AlertsOnce(args) + "\n");
        else if (args.Contains("--record-once")) Write(UsageCommands.RecordOnce(args) + "\n");
        else Write(UsageCommands.Trend(args) + "\n");
        return 0;
    }
    catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
}

if (args.Contains("--sessions"))  // 세션 목록만(값은 묻지 않는다)
{
    Write(Report.DescribeSessions(new SessionReader().Read(DateTimeOffset.Now), DateTimeOffset.Now) + "\n");
    return 0;
}

if (args.Contains("--print"))
{
    Write(await Report.PrintAsync() + "\n");
    return 0;
}

if (args.Contains("--statusline"))
{
    try
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        Write(StatusLine.Handle(stdin.ReadToEnd(), DateTimeOffset.Now));
    }
    catch (Exception)
    {
        // 상태 표시줄이 실패해도 Claude Code 는 그대로 돌아야 하므로 아무것도 내지 않는다
    }
    return 0;
}

Write(AIUsage.L10n.T("사용법: AIUsageStatus --statusline (Claude Code 상태 표시줄) | --print (값 확인)") + "\n");
return 1;
