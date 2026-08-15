using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LLMW.Writing.SandboxProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.WriteLine("{\"ok\":true}");
                return 0;
            }

            return args[0] switch
            {
                "whoami-token" => WhoamiToken(),
                "argv" => Argv(args),
                "print-env" => PrintEnv(),
                "print-env-has" => PrintEnvHas(args),
                "read-file" => ReadFile(args),
                "write-file" => WriteFile(args),
                "connect" => Connect(args),
                "sleep" => Sleep(args),
                "flood-output" => Flood(args),
                "spawn-child" => SpawnChild(args),
                "spawn-breakaway" => SpawnBreakaway(args),
                "allocate-memory" => Allocate(args),
                "spawn-many" => SpawnMany(args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name);
            return 2;
        }
    }

    private static int WhoamiToken()
    {
        using var token = OpenProcessToken();
        var payload = new Dictionary<string, object?>
        {
            ["isRestricted"] = QueryBool(token, 40),
            ["hasRestrictions"] = QueryBool(token, 21),
            ["isAppContainer"] = QueryBool(token, 29),
            ["appContainerSid"] = QueryAppContainerSid(token),
            ["elevated"] = QueryElevated(token),
            ["inJob"] = InJob(),
            ["privileges"] = QueryPrivilegeNames(token)
        };
        Console.WriteLine(JsonSerializer.Serialize(payload));
        return 0;
    }

    private static int Argv(string[] args)
    {
        Console.WriteLine(JsonSerializer.Serialize(Environment.GetCommandLineArgs()));
        Console.WriteLine(JsonSerializer.Serialize(args));
        return 0;
    }

    private static int PrintEnv()
    {
        var names = Environment.GetEnvironmentVariables().Keys.Cast<string>().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(names));
        return 0;
    }

    private static int PrintEnvHas(string[] args)
    {
        var name = args.Length > 1 ? args[1] : "";
        var exists = !string.IsNullOrEmpty(name) && Environment.GetEnvironmentVariable(name) is not null;
        Console.WriteLine(JsonSerializer.Serialize(new { name, exists }));
        return 0;
    }

    private static int ReadFile(string[] args)
    {
        var path = args.Length > 1 ? args[1] : "";
        try
        {
            var bytes = File.ReadAllBytes(path);
            Console.WriteLine(Convert.ToHexString(bytes).ToLowerInvariant());
            return 0;
        }
        catch
        {
            Console.WriteLine("READ_DENIED");
            return 3;
        }
    }

    private static int WriteFile(string[] args)
    {
        var path = args.Length > 1 ? args[1] : "";
        var content = args.Length > 2 ? args[2] : "probe";
        try
        {
            File.WriteAllText(path, content);
            Console.WriteLine("WRITE_OK");
            return 0;
        }
        catch
        {
            Console.WriteLine("WRITE_DENIED");
            return 3;
        }
    }

    private static int Connect(string[] args)
    {
        var host = args.Length > 1 ? args[1] : "127.0.0.1";
        var port = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 9;
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)))
            {
                Console.WriteLine("CONNECT_DENIED");
                return 4;
            }

            Console.WriteLine("CONNECT_OK");
            return 0;
        }
        catch
        {
            Console.WriteLine("CONNECT_DENIED");
            return 4;
        }
    }

    private static int Sleep(string[] args)
    {
        var ms = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5000;
        Thread.Sleep(ms);
        Console.WriteLine("SLEPT");
        return 0;
    }

    private static int Flood(string[] args)
    {
        var stdoutBytes = args.Length > 1 && int.TryParse(args[1], out var parsedOut) ? parsedOut : 300 * 1024;
        var stderrBytes = args.Length > 2 && int.TryParse(args[2], out var parsedErr) ? parsedErr : 300 * 1024;
        WriteFlood(Console.OpenStandardOutput(), stdoutBytes, (byte)'A');
        WriteFlood(Console.OpenStandardError(), stderrBytes, (byte)'B');
        return 0;
    }

    private static void WriteFlood(Stream stream, int bytes, byte value)
    {
        var chunk = new byte[8192];
        Array.Fill(chunk, value);
        var remaining = bytes;
        while (remaining > 0)
        {
            var take = Math.Min(chunk.Length, remaining);
            stream.Write(chunk, 0, take);
            remaining -= take;
        }

        stream.Flush();
    }

    private static int SpawnChild(string[] args)
    {
        var sleepMs = args.Length > 1 && int.TryParse(args[1], out var parsedSleep) ? parsedSleep : 20_000;
        var depth = args.Length > 2 && int.TryParse(args[2], out var parsedDepth) ? parsedDepth : 2;
        if (depth <= 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, child = 0, grandchild = 0 }));
            Thread.Sleep(sleepMs);
            return 0;
        }

        var self = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var child = StartSelf(self, ["spawn-child", sleepMs.ToString(System.Globalization.CultureInfo.InvariantCulture), (depth - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)], breakaway: false);
        Thread.Sleep(500);
        Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, child = child?.Id ?? 0 }));
        Thread.Sleep(sleepMs);
        return 0;
    }

    private static int SpawnBreakaway(string[] args)
    {
        var sleepMs = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 8_000;
        var self = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var child = StartSelf(self, ["sleep", sleepMs.ToString(System.Globalization.CultureInfo.InvariantCulture)], breakaway: true);
        Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, child = child?.Id ?? 0, breakaway = true }));
        Thread.Sleep(sleepMs);
        return 0;
    }

    private static int SpawnMany(string[] args)
    {
        var count = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 32;
        var self = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var started = 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                StartSelf(self, ["sleep", "4000"], breakaway: false);
                started++;
            }
            catch
            {
                break;
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new { started }));
        Thread.Sleep(1000);
        return started > 0 ? 0 : 5;
    }

    private static int Allocate(string[] args)
    {
        var megabytes = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 64;
        var blob = new byte[Math.Max(1, megabytes) * 1024 * 1024];
        blob[0] = 1;
        blob[^1] = 2;
        Console.WriteLine(JsonSerializer.Serialize(new { allocated = blob.Length, marker = blob[0] }));
        Thread.Sleep(500);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine("UNKNOWN " + command);
        return 1;
    }

    private static System.Diagnostics.Process? StartSelf(string self, IReadOnlyList<string> arguments, bool breakaway)
    {
        var info = new System.Diagnostics.ProcessStartInfo(self)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (breakaway)
        {
            info.UseShellExecute = false;
        }

        var process = System.Diagnostics.Process.Start(info);
        if (breakaway && process is not null)
        {
            TryBreakaway(process);
        }

        return process;
    }

    private static void TryBreakaway(System.Diagnostics.Process process)
    {
        _ = process.Id;
        CreateProcessWithBreakaway();
    }

    private static void CreateProcessWithBreakaway()
    {
        var self = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var startup = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
        CreateProcessW(
            self,
            "\"" + self + "\" sleep 8000",
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            0x01000000,
            IntPtr.Zero,
            null,
            ref startup,
            out _);
    }

    private static SafeToken OpenProcessToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var handle) || handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("OpenProcessToken failed.");
        }

        return new SafeToken(handle);
    }

    private static bool QueryBool(SafeToken token, int infoClass)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            return GetTokenInformation(token.Handle, infoClass, buffer, 4, out _) && Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool QueryElevated(SafeToken token)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            return GetTokenInformation(token.Handle, 20, buffer, 4, out _) && Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? QueryAppContainerSid(SafeToken token)
    {
        GetTokenInformation(token.Handle, 31, IntPtr.Zero, 0, out var length);
        if (length <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token.Handle, 31, buffer, length, out _))
            {
                return null;
            }

            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return null;
            }

            if (!ConvertSidToStringSidW(sid, out var stringSid) || stringSid == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(stringSid);
            }
            finally
            {
                LocalFree(stringSid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string[] QueryPrivilegeNames(SafeToken token)
    {
        GetTokenInformation(token.Handle, 3, IntPtr.Zero, 0, out var length);
        if (length <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token.Handle, 3, buffer, length, out _))
            {
                return [];
            }

            var count = Marshal.ReadInt32(buffer);
            List<string> names = [];
            var cursor = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                var luidLow = Marshal.ReadInt32(cursor);
                var luidHigh = Marshal.ReadInt32(cursor, 4);
                var name = new char[256];
                var size = 256;
                var luid = new LUID { LowPart = (uint)luidLow, HighPart = luidHigh };
                if (LookupPrivilegeNameW(null, ref luid, name, ref size))
                {
                    names.Add(new string(name, 0, Math.Max(0, size)));
                }

                cursor += 12;
            }

            return names.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool InJob()
    {
        return IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var result) && result;
    }

    private sealed class SafeToken(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose() => CloseHandle(Handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeNameW(string? system, ref LUID luid, char[] name, ref int nameLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOW startupInfo,
        out PROCESS_INFORMATION processInformation);
}
