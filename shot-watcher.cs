// shot-watcher — shot-service 的自愈守护 (幂等: 活着跳过, 挂了拉起)
// 由 HKCU Run\shot-watch 随用户登录在 Session 1 启动; 无窗口无托盘
// 行为: 每 30s 探测 127.0.0.1:18800 端口, 不通则拉起 shot-service.exe (冷却 15s 防抖)
// 编译: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ ^
//       /out:shot-watcher.exe /r:System.dll shot-watcher.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

public class ShotWatcher
{
    const int PORT = 18800;
    const int CHECK_MS = 30000;      // 检查周期
    const int COOLDOWN_MS = 15000;   // 拉起后的防抖冷却
    static readonly string ExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot-service.exe");
    static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot-watcher.log");
    static DateTime lastLaunch = DateTime.MinValue;
    static int launches = 0;

    static void Log(string m) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + m + "\r\n"); } catch { } }

    // 存活判断: 端口 18800 可连接即视为健康
    static bool Alive()
    {
        try { using (TcpClient c = new TcpClient("127.0.0.1", PORT)) { return true; } }
        catch { }
        return false;
    }

    static void Check()
    {
        if (Alive()) { return; } // 活着: 跳过
        if ((DateTime.Now - lastLaunch).TotalMilliseconds < COOLDOWN_MS) { return; } // 冷却期: 跳过
        try
        {
            Process.Start(ExePath);
            lastLaunch = DateTime.Now;
            launches++;
            Log("shot-service DOWN -> relaunched (#" + launches + ")");
        }
        catch (Exception ex) { Log("relaunch fail: " + ex.Message); }
    }

    public static void Main()
    {
        Log("shot-watcher start session=" + Process.GetCurrentProcess().SessionId + " pid=" + Process.GetCurrentProcess().Id);
        Check(); // 启动立即查一次
        Timer t = new Timer(delegate { Check(); }, null, CHECK_MS, CHECK_MS);
        while (true) { Thread.Sleep(60000); }
    }
}