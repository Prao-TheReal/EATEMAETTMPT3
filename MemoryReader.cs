using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Remnant2ESP;

public class MemoryReader : IDisposable
{
    private IntPtr _processHandle;
    private Process? _process;
    private IntPtr _baseAddress;

    // --- DLL IMPORTS ---
    [DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    // --- PERMISSIONS ---
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    // --- PROPERTIES ---
    // This was likely missing or broken in your previous file
    public bool IsAttached => _processHandle != IntPtr.Zero && _process != null && !_process.HasExited;

    public IntPtr BaseAddress => _baseAddress;

    // --- METHODS ---
    public bool Attach(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);

            if (processes.Length == 0)
            {
                Debug.WriteLine($"[ESP Error] Process '{processName}' NOT FOUND.");
                return false;
            }

            _process = processes[0];

            // Open with READ + WRITE + OPERATION permissions for Aimbot
            _processHandle = OpenProcess(
                PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION,
                false,
                _process.Id
            );

            if (_processHandle == IntPtr.Zero)
            {
                Debug.WriteLine($"[ESP Error] Found process, but OpenProcess failed. NEED ADMIN?");
                return false;
            }

            _baseAddress = _process.MainModule?.BaseAddress ?? IntPtr.Zero;

            Debug.WriteLine($"[ESP Success] Attached! Base: 0x{_baseAddress:X}");
            return _baseAddress != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ESP Exception] {ex.Message}");
            return false;
        }
    }

    public bool Write<T>(IntPtr address, T value) where T : struct
    {
        try
        {
            var size = Marshal.SizeOf(typeof(T));
            var buffer = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);

            Marshal.StructureToPtr(value, ptr, true);
            Marshal.Copy(ptr, buffer, 0, size);
            Marshal.FreeHGlobal(ptr);

            return WriteProcessMemory(_processHandle, address, buffer, size, out _);
        }
        catch { return false; }
    }

    public Vector3 ReadVector3(IntPtr address)
    {
        byte[] buffer = new byte[24];
        if (ReadProcessMemory(_processHandle, address, buffer, 24, out _))
        {
            return new Vector3(
                BitConverter.ToDouble(buffer, 0),
                BitConverter.ToDouble(buffer, 8),
                BitConverter.ToDouble(buffer, 16)
            );
        }
        return new Vector3(0, 0, 0);
    }

    public (double Pitch, double Yaw) ReadRotation(IntPtr address)
    {
        byte[] buffer = new byte[16];
        if (ReadProcessMemory(_processHandle, address, buffer, 16, out _))
        {
            return (
                BitConverter.ToDouble(buffer, 0),
                BitConverter.ToDouble(buffer, 8)
            );
        }
        return (0, 0);
    }

    public byte[]? ReadBytes(IntPtr address, int size)
    {
        if (!IsAttached) return null;
        byte[] buffer = new byte[size];
        if (ReadProcessMemory(_processHandle, address, buffer, size, out _))
            return buffer;
        return null;
    }

    public IntPtr ReadPointer(IntPtr address)
    {
        var bytes = ReadBytes(address, 8);
        return bytes == null ? IntPtr.Zero : (IntPtr)BitConverter.ToInt64(bytes, 0);
    }

    public double ReadDouble(IntPtr address)
    {
        var bytes = ReadBytes(address, 8);
        return bytes == null ? 0 : BitConverter.ToDouble(bytes, 0);
    }

    public float ReadFloat(IntPtr address)
    {
        var bytes = ReadBytes(address, 4);
        return bytes == null ? 0 : BitConverter.ToSingle(bytes, 0);
    }

    public int ReadInt32(IntPtr address)
    {
        var bytes = ReadBytes(address, 4);
        return bytes == null ? 0 : BitConverter.ToInt32(bytes, 0);
    }

    public IntPtr FollowPointerChain(IntPtr baseAddr, params int[] offsets)
    {
        IntPtr current = baseAddr;
        for (int i = 0; i < offsets.Length; i++)
        {
            current = ReadPointer(current);
            if (current == IntPtr.Zero) return IntPtr.Zero;
            current = IntPtr.Add(current, offsets[i]);
        }
        return current;
    }

    public void Dispose()
    {
        if (_processHandle != IntPtr.Zero) CloseHandle(_processHandle);
        _process?.Dispose();
    }
}