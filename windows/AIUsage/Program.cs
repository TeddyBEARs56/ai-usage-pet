namespace AIUsage;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 자동 업데이트로 받은 새 exe 로 띄워졌으면: 앞의 창이 꺼지길 기다렸다가 설치된 것을 바꾸고 다시 띄운다
        var replacing = Updater.ApplyTarget(args);
        if (replacing is { } oldPid) Updater.WaitForExit(oldPid);

        using var mutex = new Mutex(false, @"Local\AIUsagePet");
        if (!TakeMutex(mutex))
        {
            if (replacing is null)
                MessageBox.Show(L10n.T("AI 사용량이 이미 실행 중입니다."), L10n.T("AI 사용량"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                Updater.Log("바꾸지 않음: 다른 창이 이미 떠 있음");
            return 0;
        }
        if (replacing is not null)
        {
            Updater.ApplyAndRelaunch(mutex);
            return 0;
        }
        if (Installer.OfferOnStart(mutex)) return 0;  // 설치하고 설치된 쪽으로 다시 띄웠다

        Application.Run(new MainForm(mutex, afterInstall: args.Contains("--after-install")));
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }  // 메뉴에서 설치하며 이미 놓았을 수 있다
        return 0;
    }

    /// 하나만 뜨게 한다. 다시 띄울 때는 앞의 것이 꺼지는 중일 수 있어 3초까지 기다린다.
    static bool TakeMutex(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { return true; }  // 앞의 것이 비정상 종료했다
    }
}
