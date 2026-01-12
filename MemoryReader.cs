using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Remnant2ESP;

public class MemoryReader : IDisposable
{
    private IntPtr _processHandle;
    private Process? _process;
    private IntPtr _baseAddress;

    [DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    public bool IsAttached => _processHandle != IntPtr.Zero && _process != null && !_process.HasExited;
    public IntPtr BaseAddress => _baseAddress;
    public Process Process => _process!;

    public bool Attach(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0) return false;

            _process = processes[0];
            _processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION, false, _process.Id);

            if (_processHandle == IntPtr.Zero) return false;

            _baseAddress = _process.MainModule?.BaseAddress ?? IntPtr.Zero;
            return _baseAddress != IntPtr.Zero;
        }
        catch { return false; }
    }

    public byte[]? ReadBytes(IntPtr address, int size)
    {
        if (!IsAttached) return null;
        byte[] buffer = new byte[size];
        if (ReadProcessMemory(_processHandle, address, buffer, size, out _)) return buffer;
        return null;
    }

    public IntPtr ReadPointer(IntPtr address)
    {
        var bytes = ReadBytes(address, 8);
        return bytes == null ? IntPtr.Zero : (IntPtr)BitConverter.ToInt64(bytes, 0);
    }

    public int ReadInt32(IntPtr address)
    {
        var bytes = ReadBytes(address, 4);
        return bytes == null ? 0 : BitConverter.ToInt32(bytes, 0);
    }

    public float ReadFloat(IntPtr address)
    {
        var bytes = ReadBytes(address, 4);
        return bytes == null ? 0 : BitConverter.ToSingle(bytes, 0);
    }

    public double ReadDouble(IntPtr address)
    {
        var bytes = ReadBytes(address, 8);
        return bytes == null ? 0 : BitConverter.ToDouble(bytes, 0);
    }

    // --- NEW: SCAN ALL MATCHES ---
    public List<IntPtr> ScanAllPatterns(string signature, ProcessModule module)
    {
        var results = new List<IntPtr>();
        if (module == null) return results;

        long baseAddr = module.BaseAddress.ToInt64();
        int size = module.ModuleMemorySize;
        byte[]? moduleBytes = ReadBytes((IntPtr)baseAddr, size);
        if (moduleBytes == null) return results;

        var sigParts = signature.Split(' ');
        var pattern = new byte?[sigParts.Length];
        for (int i = 0; i < sigParts.Length; i++)
        {
            if (sigParts[i] == "?" || sigParts[i] == "??") pattern[i] = null;
            else pattern[i] = Convert.ToByte(sigParts[i], 16);
        }

        for (int i = 0; i < moduleBytes.Length - pattern.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (pattern[j] != null && moduleBytes[i + j] != pattern[j])
                {
                    found = false;
                    break;
                }
            }
            if (found) results.Add((IntPtr)(baseAddr + i));
        }
        return results;
    }

    // Legacy single scan support
    public IntPtr ScanPattern(string signature, ProcessModule module)
    {
        var list = ScanAllPatterns(signature, module);
        return list.Count > 0 ? list[0] : IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_processHandle != IntPtr.Zero) CloseHandle(_processHandle);
        _process?.Dispose();
    }
}