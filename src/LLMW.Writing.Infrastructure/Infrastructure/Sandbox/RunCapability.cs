using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class RunCapability : IDisposable
{
    private readonly IntPtr capabilitySids;
    private readonly uint capabilitySidCount;
    private readonly IntPtr groupSids;
    private readonly uint groupCount;
    private readonly SafeSidHandle sidHandle;

    private RunCapability(
        SafeSidHandle sidHandle,
        string sidString,
        IntPtr capabilitySids,
        uint capabilitySidCount,
        IntPtr groupSids,
        uint groupCount)
    {
        this.sidHandle = sidHandle;
        SidString = sidString;
        this.capabilitySids = capabilitySids;
        this.capabilitySidCount = capabilitySidCount;
        this.groupSids = groupSids;
        this.groupCount = groupCount;
    }

    public string SidString { get; }

    public IntPtr SidPointer => sidHandle.DangerousGetHandle();

    public static RunCapability Derive(Guid projectId, string runId)
    {
        var name = SandboxPathPolicy.RunCapabilityName(projectId, runId);
        IntPtr groups = IntPtr.Zero;
        IntPtr sids = IntPtr.Zero;
        uint groupCount = 0;
        uint sidCount = 0;
        try
        {
            if (!NativeMethods.DeriveCapabilitySidsFromName(name, out groups, out groupCount, out sids, out sidCount) ||
                sids == IntPtr.Zero ||
                sidCount == 0)
            {
                throw new SandboxLayerException(
                    SandboxError.SecurityCapabilitiesFailed,
                    $"DeriveCapabilitySidsFromName('{name}') failed: {Marshal.GetLastWin32Error()}.");
            }

            var first = Marshal.ReadIntPtr(sids);
            if (first == IntPtr.Zero)
            {
                throw new SandboxLayerException(SandboxError.SecurityCapabilitiesFailed, "Run capability SID was null.");
            }

            var length = NativeMethods.GetLengthSid(first);
            var copy = Marshal.AllocHGlobal(length);
            if (!NativeMethods.CopySid(length, copy, first))
            {
                Marshal.FreeHGlobal(copy);
                throw new SandboxLayerException(
                    SandboxError.SecurityCapabilitiesFailed,
                    $"CopySid(run capability) failed: {Marshal.GetLastWin32Error()}.");
            }

            var sidHandle = new SafeSidHandle(copy, ownsHandle: true, SidReleaseKind.MarshalFree);
            var sidString = NativeSid.ToStringSid(copy);
            var capability = new RunCapability(sidHandle, sidString, sids, sidCount, groups, groupCount);
            sids = IntPtr.Zero;
            groups = IntPtr.Zero;
            return capability;
        }
        catch (DllNotFoundException exception)
        {
            throw new SandboxLayerException(SandboxError.SecurityCapabilitiesFailed, exception.Message);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new SandboxLayerException(SandboxError.SecurityCapabilitiesFailed, exception.Message);
        }
        finally
        {
            FreeSidArray(sids, sidCount);
            FreeSidArray(groups, groupCount);
        }
    }

    public void Dispose()
    {
        sidHandle.Dispose();
        FreeSidArray(capabilitySids, capabilitySidCount);
        FreeSidArray(groupSids, groupCount);
    }

    private static void FreeSidArray(IntPtr array, uint count)
    {
        if (array == IntPtr.Zero)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var sid = Marshal.ReadIntPtr(array, i * IntPtr.Size);
            if (sid != IntPtr.Zero)
            {
                NativeMethods.LocalFree(sid);
            }
        }

        NativeMethods.LocalFree(array);
    }
}
