using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal static class AppContainerNetworkIsolation
{
    public static void EnsureLoopbackNotExempt(string appContainerSid, ISandboxFaultInjector faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appContainerSid);
        _ = faultInjector;
        try
        {
            var status = NativeMethods.NetworkIsolationGetAppContainerConfig(out var count, out var buffer);
            if (status != 0 || buffer == IntPtr.Zero || count == 0)
            {
                if (buffer != IntPtr.Zero)
                {
                    DiscardIsolationStatus(NativeMethods.NetworkIsolationFreeAppContainers(buffer));
                }

                return;
            }

            try
            {
                var keepers = new List<SID_AND_ATTRIBUTES>((int)count);
                var found = false;
                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<SID_AND_ATTRIBUTES>(buffer + (i * Marshal.SizeOf<SID_AND_ATTRIBUTES>()));
                    if (entry.Sid == IntPtr.Zero)
                    {
                        continue;
                    }

                    var sid = NativeSid.ToStringSid(entry.Sid);
                    if (StringComparer.OrdinalIgnoreCase.Equals(sid, appContainerSid))
                    {
                        found = true;
                        continue;
                    }

                    keepers.Add(entry);
                }

                if (!found)
                {
                    return;
                }

                var setStatus = NativeMethods.NetworkIsolationSetAppContainerConfig(
                    (uint)keepers.Count,
                    keepers.Count == 0 ? null : keepers.ToArray());
                if (setStatus != 0)
                {
                    throw new SandboxLayerException(
                        SandboxError.NetworkDenied,
                        $"Could not clear AppContainer loopback exemption: {setStatus}.");
                }
            }
            finally
            {
                DiscardIsolationStatus(NativeMethods.NetworkIsolationFreeAppContainers(buffer));
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static void DiscardIsolationStatus(uint status)
    {
        if (status != 0)
        {
            return;
        }
    }
}
