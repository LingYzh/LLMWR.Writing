using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class SandboxWindowStation : IDisposable
{
    public const string DesktopName = "llmw.desk";

    private static readonly object Gate = new();
    private static SandboxWindowStation? instance;

    private readonly IntPtr windowStation;
    private readonly IntPtr desktop;
    private readonly HashSet<string> granted = new(StringComparer.OrdinalIgnoreCase);

    private SandboxWindowStation(IntPtr windowStation, IntPtr desktop, string stationName)
    {
        this.windowStation = windowStation;
        this.desktop = desktop;
        StationName = stationName;
        DesktopPath = stationName + "\\" + DesktopName;
    }

    public string StationName { get; }

    public string DesktopPath { get; }

    public static SandboxWindowStation Ensure()
    {
        lock (Gate)
        {
            if (instance is not null)
            {
                return instance;
            }

            var previous = NativeMethods.GetProcessWindowStation();
            if (previous == IntPtr.Zero)
            {
                throw new SandboxLayerException(
                    SandboxError.AppContainerAclFailed,
                    $"GetProcessWindowStation failed: {Marshal.GetLastWin32Error()}.");
            }

            // Named CreateWindowStationW is ACCESS_DENIED (5) for an interactive medium-IL
            // user token. A NULL name is the documented path: Windows assigns a
            // noninteractive station (Service-0x0-<logon>$). Do not fall back to WinSta0\Default.
            var station = NativeMethods.CreateWindowStationW(
                null,
                0,
                NativeConstants.WINSTA_ALL_ACCESS,
                IntPtr.Zero);
            if (station == IntPtr.Zero)
            {
                throw new SandboxLayerException(
                    SandboxError.AppContainerAclFailed,
                    $"CreateWindowStationW(NULL) failed: {Marshal.GetLastWin32Error()}.");
            }

            var stationName = AppContainerAclManager.UserObjectName(station);
            if (string.IsNullOrWhiteSpace(stationName) ||
                stationName.Equals("WinSta0", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.CloseWindowStation(station);
                throw new SandboxLayerException(
                    SandboxError.AppContainerAclFailed,
                    $"CreateWindowStationW(NULL) returned interactive station '{stationName}'.");
            }

            IntPtr createdDesktop = IntPtr.Zero;
            var restoreError = 0;
            try
            {
                if (!NativeMethods.SetProcessWindowStation(station))
                {
                    throw new SandboxLayerException(
                        SandboxError.AppContainerAclFailed,
                        $"SetProcessWindowStation(sandbox) failed: {Marshal.GetLastWin32Error()}.");
                }

                createdDesktop = NativeMethods.CreateDesktopW(
                    DesktopName,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    NativeConstants.DESKTOP_ALL_ACCESS,
                    IntPtr.Zero);
                if (createdDesktop == IntPtr.Zero)
                {
                    createdDesktop = NativeMethods.OpenDesktopW(DesktopName, 0, false, NativeConstants.DESKTOP_ALL_ACCESS);
                }

                if (createdDesktop == IntPtr.Zero)
                {
                    throw new SandboxLayerException(
                        SandboxError.AppContainerAclFailed,
                        $"Create/OpenDesktopW('{DesktopName}') failed: {Marshal.GetLastWin32Error()}.");
                }
            }
            finally
            {
                if (!NativeMethods.SetProcessWindowStation(previous))
                {
                    restoreError = Marshal.GetLastWin32Error();
                }
            }

            if (restoreError != 0)
            {
                throw new SandboxLayerException(
                    SandboxError.AppContainerAclFailed,
                    $"SetProcessWindowStation restore failed: {restoreError}.");
            }

            instance = new SandboxWindowStation(station, createdDesktop, stationName);
            return instance;
        }
    }

    public void GrantSandboxIdentity(string appContainerSid, ISandboxFaultInjector faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appContainerSid);
        lock (Gate)
        {
            if (!granted.Add(appContainerSid))
            {
                return;
            }

            AppContainerAclManager.GrantUserObject(
                windowStation,
                appContainerSid,
                NativeConstants.SandboxWindowStationAccess,
                faultInjector);
            AppContainerAclManager.GrantUserObject(
                desktop,
                appContainerSid,
                NativeConstants.SandboxDesktopAccess,
                faultInjector);
        }
    }

    public void Dispose()
    {
        if (desktop != IntPtr.Zero)
        {
            NativeMethods.CloseDesktop(desktop);
        }

        if (windowStation != IntPtr.Zero)
        {
            NativeMethods.CloseWindowStation(windowStation);
        }
    }
}
