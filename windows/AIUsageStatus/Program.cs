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

Write("사용법: AIUsageStatus --statusline (Claude Code 상태 표시줄) | --print (값 확인)\n");
return 1;
