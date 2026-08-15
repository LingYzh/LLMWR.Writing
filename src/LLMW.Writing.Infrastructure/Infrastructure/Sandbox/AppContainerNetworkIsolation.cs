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
        if (faultInjector.Fault == SandboxFaultPoint.NetworkIsolationQuery)
        {
            throw new SandboxLayerException(
                SandboxError.SandboxUnavailable,
                "Injected NetworkIsolationGetAppContainerConfig failure.");
        }

        uint status;
        uint count;
        IntPtr buffer;
        try
        {
            status = NativeMethods.NetworkIsolationGetAppContainerConfig(out count, out buffer);
        }
        catch (DllNotFoundException exception)
        {
            throw new SandboxLayerException(
                SandboxError.SandboxUnavailable,
                "NetworkIsolationGetAppContainerConfig is unavailable: " + exception.Message);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new SandboxLayerException(
                SandboxError.SandboxUnavailable,
                "NetworkIsolationGetAppContainerConfig entry is unavailable: " + exception.Message);
        }

        if (status != 0)
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NativeMethods.NetworkIsolationFreeAppContainers(buffer);
            }

            throw new SandboxLayerException(
                SandboxError.SandboxUnavailable,
                $"NetworkIsolationGetAppContainerConfig failed: {status}.");
        }

        if (buffer == IntPtr.Zero || count == 0)
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NativeMethods.NetworkIsolationFreeAppContainers(buffer);
            }

            if (faultInjector.Fault == SandboxFaultPoint.NetworkIsolationSet)
            {
                throw new SandboxLayerException(
                    SandboxError.NetworkDenied,
                    "Injected NetworkIsolationSetAppContainerConfig failure.");
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
                if (faultInjector.Fault == SandboxFaultPoint.NetworkIsolationSet)
                {
                    throw new SandboxLayerException(
                        SandboxError.NetworkDenied,
                        "Injected NetworkIsolationSetAppContainerConfig failure.");
                }

                return;
            }

            if (faultInjector.Fault == SandboxFaultPoint.NetworkIsolationSet)
            {
                throw new SandboxLayerException(
                    SandboxError.NetworkDenied,
                    "Injected NetworkIsolationSetAppContainerConfig failure.");
            }

            uint setStatus;
            try
            {
                setStatus = NativeMethods.NetworkIsolationSetAppContainerConfig(
                    (uint)keepers.Count,
                    keepers.Count == 0 ? null : keepers.ToArray());
            }
            catch (DllNotFoundException exception)
            {
                throw new SandboxLayerException(
                    SandboxError.NetworkDenied,
                    "NetworkIsolationSetAppContainerConfig is unavailable: " + exception.Message);
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new SandboxLayerException(
                    SandboxError.NetworkDenied,
                    "NetworkIsolationSetAppContainerConfig entry is unavailable: " + exception.Message);
            }

            if (setStatus != 0)
            {
                throw new SandboxLayerException(
                    SandboxError.NetworkDenied,
                    $"Could not clear AppContainer loopback exemption: {setStatus}.");
            }
        }
        finally
        {
            _ = NativeMethods.NetworkIsolationFreeAppContainers(buffer);
        }
    }
}
