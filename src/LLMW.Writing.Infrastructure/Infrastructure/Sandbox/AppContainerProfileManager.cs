using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class AppContainerProfileManager
{
    private AppContainerProfileManager()
    {
    }
    public static SandboxIdentity CreateOrDerive(Guid projectId, ISandboxFaultInjector faultInjector)
    {
        if (faultInjector.Fault == SandboxFaultPoint.AppContainerProfile)
        {
            throw new SandboxLayerException(SandboxError.AppContainerProfileFailed, "Injected AppContainer profile failure.");
        }

        var name = SandboxPathPolicy.AppContainerName(projectId);
        var hr = NativeMethods.CreateAppContainerProfile(name, name, name, IntPtr.Zero, 0, out var sid);
        if (hr == NativeConstants.HRESULT_ERROR_ALREADY_EXISTS || sid == IntPtr.Zero)
        {
            if (sid != IntPtr.Zero)
            {
                NativeMethods.FreeSid(sid);
            }

            hr = NativeMethods.DeriveAppContainerSidFromAppContainerName(name, out sid);
        }

        if (hr < 0 || sid == IntPtr.Zero)
        {
            throw new SandboxLayerException(
                SandboxError.AppContainerProfileFailed,
                $"AppContainer profile '{name}' failed: HRESULT 0x{hr:X8}.");
        }

        try
        {
            var sidString = NativeSid.ToStringSid(sid);
            var derivedHr = NativeMethods.DeriveAppContainerSidFromAppContainerName(name, out var derived);
            if (derivedHr < 0 || derived == IntPtr.Zero)
            {
                throw new SandboxLayerException(
                    SandboxError.AppContainerProfileFailed,
                    $"Could not derive AppContainer SID for '{name}': HRESULT 0x{derivedHr:X8}.");
            }

            try
            {
                var derivedString = NativeSid.ToStringSid(derived);
                if (!StringComparer.OrdinalIgnoreCase.Equals(sidString, derivedString))
                {
                    throw new SandboxLayerException(
                        SandboxError.AppContainerProfileFailed,
                        "Derived AppContainer SID did not match the expected project identity.");
                }
            }
            finally
            {
                NativeMethods.FreeSid(derived);
            }

            return new SandboxIdentity(name, sidString);
        }
        finally
        {
            NativeMethods.FreeSid(sid);
        }
    }

    public static string ResolveProfileDirectory(SandboxIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var hr = NativeMethods.GetAppContainerFolderPath(identity.AppContainerSid, out var path);
        if (hr >= 0 && path != IntPtr.Zero)
        {
            try
            {
                var folder = Marshal.PtrToStringUni(path);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Directory.CreateDirectory(folder);
                    return folder;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            identity.AppContainerName,
            "AC");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class LpacCapability : IDisposable
{
    private readonly IntPtr capabilitySids;
    private readonly uint capabilitySidCount;
    private readonly IntPtr groupSids;
    private readonly uint groupCount;
    private GCHandle attributesHandle;

    private LpacCapability(IntPtr capabilitySids, uint capabilitySidCount, IntPtr groupSids, uint groupCount)
    {
        this.capabilitySids = capabilitySids;
        this.capabilitySidCount = capabilitySidCount;
        this.groupSids = groupSids;
        this.groupCount = groupCount;
    }

    public uint Count => capabilitySidCount == 0 ? 0u : 1u;

    public IntPtr AttributesPointer { get; private set; }

    public static LpacCapability? TryCreate()
    {
        try
        {
            if (!NativeMethods.DeriveCapabilitySidsFromName(
                    "lpacAppContainer",
                    out var groups,
                    out var groupCount,
                    out var sids,
                    out var sidCount) ||
                sids == IntPtr.Zero ||
                sidCount == 0)
            {
                return null;
            }

            return new LpacCapability(sids, sidCount, groups, groupCount);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public IntPtr PinAttributes()
    {
        if (capabilitySidCount == 0)
        {
            return IntPtr.Zero;
        }

        var sid = Marshal.ReadIntPtr(capabilitySids);
        var attributes = new SID_AND_ATTRIBUTES
        {
            Sid = sid,
            Attributes = NativeConstants.SE_GROUP_ENABLED
        };
        attributesHandle = GCHandle.Alloc(attributes, GCHandleType.Pinned);
        AttributesPointer = attributesHandle.AddrOfPinnedObject();
        return AttributesPointer;
    }

    public void Dispose()
    {
        if (attributesHandle.IsAllocated)
        {
            attributesHandle.Free();
        }

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
