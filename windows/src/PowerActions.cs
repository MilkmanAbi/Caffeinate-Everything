// Sleep, hibernate, wake the display, and turn the screen off while staying awake.
//
// Behaviour verified on a Modern Standby (S0 low power idle) Surface Laptop, Windows 11 build 28000:
//  * SC_MONITORPOWER "off" enters Modern Standby immediately, even while a SystemRequired request is
//    held (Kernel-Power 506 within 0.2 s, Wi-Fi disconnected). It is the "sleep now" path on S0 systems,
//    where SetSuspendState(sleep) is unsupported.
//  * The display *idle timeout* turns the screen off while the system "continues to run and all
//    applications continue to operate normally" (Microsoft, Display/sleep/hibernate idle timers).
//    With a SystemRequired request held, pings continued for the whole test. That is --screen-off.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Caffeinate;

internal static class PowerActions
{
    public static readonly Native.Capabilities Caps = Native.GetCapabilities();
    public static bool ModernStandby => Caps.ModernStandby;
    public static bool CanHibernate => Caps.S4 && Caps.HiberFile;

    public static void Sleep()
    {
        Log.Write("sleeping now");
        if (ModernStandby)
        {
            MessageWindow.MonitorOff();
            return;
        }
        if (!Native.EnablePrivilege("SeShutdownPrivilege"))
            throw new InvalidOperationException("not allowed to put the PC to sleep (missing shutdown privilege)");
        if (!Native.SetSuspendState(false, false, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "could not put the PC to sleep");
    }

    public static void Hibernate()
    {
        if (!CanHibernate)
            throw new InvalidOperationException("hibernation is turned off on this PC (an admin can enable it with: powercfg /hibernate on)");
        if (!Native.EnablePrivilege("SeShutdownPrivilege"))
            throw new InvalidOperationException("not allowed to hibernate the PC (missing shutdown privilege)");
        Log.Write("hibernating now");
        // Returns after the PC resumes.
        if (!Native.SetSuspendState(true, false, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "could not hibernate the PC");
    }

    /// Declare user activity: resets idle timers and wakes a display that is off.
    public static void DeclareUserActivity()
    {
        Native.SetThreadExecutionState(Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);

        // A zero-distance relative mouse move counts as user input: it wakes a display that is
        // already off and updates the last-input time, without moving the pointer.
        // It is refused on the secure desktop (lock screen); the display request covers that case.
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            mi = new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_MOVE },
        };
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>()) != 1)
            Log.Write($"SendInput refused: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    /// Wake the display and keep the PC up long enough to outlast the ~2 minute "unattended wake"
    /// timeout that Windows applies after a timer wake.
    public static void WakeFor(TimeSpan hold)
    {
        try { MessageWindow.Ensure(); } catch (Exception e) { Log.Write($"no display notifications: {e.Message}"); }
        using var req = new PowerRequest("caffeinate scheduled wake");
        req.Set(Native.PowerRequestType.DisplayRequired);
        req.Set(Native.PowerRequestType.SystemRequired);
        DeclareUserActivity();
        var until = DateTime.UtcNow + hold;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(5000);
            if (MessageWindow.DisplayState == 0) DeclareUserActivity();
        }
    }
}

/// --screen-off: use the display idle timeout (not SC_MONITORPOWER, which would sleep the PC).
internal static class ScreenOff
{
    public static bool Run(WaitHandle? cancel = null)
    {
        MessageWindow.Ensure();
        if (MessageWindow.DisplayState == 0) return true;

        Session.Info("turning the screen off; hands off the keyboard and mouse for a moment...");
        bool off = false;
        // A 3-second screen timeout, journaled so a crash can't leave it behind; restored as soon as the
        // screen is off, after which it stays off until there is input.
        Journal.Acquire(PlanSetting.DisplayTimeout, 3, 3);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (MessageWindow.DisplayState == 0) { off = true; break; }
                if (cancel?.WaitOne(0) == true) break;
                MessageWindow.DisplayChanged.WaitOne(250);
            }
        }
        finally
        {
            Journal.Release(PlanSetting.DisplayTimeout);
        }
        if (!off) Session.Warn("the screen didn't turn off (there was input the whole time?)");
        else Log.Write("screen turned off");
        return off;
    }
}

/// A hidden window on its own thread: receives display-state notifications and handles SC_MONITORPOWER.
internal static unsafe class MessageWindow
{
    // Microsoft: interactive apps should use GUID_SESSION_DISPLAY_STATUS; the console GUID is a fallback.
    private static readonly Guid SessionDisplayStatus = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    public static string RegistrationError = "";
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Ready = new(false);
    private static Thread? _thread;
    private static IntPtr _hwnd;

    /// 0 = off, 1 = on, 2 = dimmed, -1 = unknown.
    public static volatile int DisplayState = -1;
    public static readonly AutoResetEvent DisplayChanged = new(false);

    public static IntPtr Ensure()
    {
        lock (Gate)
        {
            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "caffeinate-window" };
                _thread.Start();
            }
        }
        if (!Ready.Wait(TimeSpan.FromSeconds(5)) || _hwnd == IntPtr.Zero)
            throw new InvalidOperationException("could not create the power notification window");
        // Registration delivers the current display state right away; give it a moment to arrive.
        for (int i = 0; i < 20 && DisplayState == -1; i++) Thread.Sleep(25);
        return _hwnd;
    }

    public static void MonitorOff() =>
        Native.SendMessageW(Ensure(), Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, 2);

    private static void Loop()
    {
        string cls = $"CaffeinateWindow{Environment.ProcessId}";
        fixed (char* name = cls)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = name,
            };
            Native.RegisterClassExW(ref wc);
        }
        // A real (never shown) top-level window rather than HWND_MESSAGE, so DefWindowProc acts on SC_MONITORPOWER.
        _hwnd = Native.CreateWindowExW(0, cls, "caffeinate", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero,
            Native.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd != IntPtr.Zero)
        {
            Guid g = SessionDisplayStatus;
            if (Native.RegisterPowerSettingNotification(_hwnd, ref g, 0) == IntPtr.Zero)
            {
                RegistrationError = $"session display: error {Marshal.GetLastWin32Error()}";
                g = ConsoleDisplayState;
                if (Native.RegisterPowerSettingNotification(_hwnd, ref g, 0) == IntPtr.Zero)
                    RegistrationError += $"; console display: error {Marshal.GetLastWin32Error()}";
            }
            if (RegistrationError.Length > 0) Log.Write($"display notifications: {RegistrationError}");
        }
        else Log.Write($"could not create the notification window: error {Marshal.GetLastWin32Error()}");
        Ready.Set();
        if (_hwnd == IntPtr.Zero) return;
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_POWERBROADCAST && (int)wParam == Native.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; UCHAR Data[] }
            Guid setting = *(Guid*)lParam;
            if (setting == SessionDisplayStatus || setting == ConsoleDisplayState)
            {
                DisplayState = *(int*)((byte*)lParam + 20);
                DisplayChanged.Set();
            }
            return 1;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
