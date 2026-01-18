using System.Text;

namespace Remnant2ESP;

public class NameReader
{
    private readonly MemoryReader _memory;
    private IntPtr _blocksArray;
    private int _blocksOffset;

    public NameReader(MemoryReader memory, IntPtr gNamesStruct, int offset)
    {
        _memory = memory;
        _blocksOffset = offset;
        _blocksArray = gNamesStruct;
    }

    public string GetName(int id)
    {
        try
        {
            if (id < 0 || id > 200000000) return "ID_ERR";

            long chunkIndex = (uint)id >> 16;
            long withinChunkIndex = (ushort)id;

            IntPtr chunkArrayBase = IntPtr.Add(_blocksArray, _blocksOffset);
            IntPtr chunkPtrAddress = IntPtr.Add(chunkArrayBase, (int)(chunkIndex * 8));
            IntPtr chunkPtr = _memory.ReadPointer(chunkPtrAddress);
            if (chunkPtr == IntPtr.Zero) return "NULL_CHUNK";

            // Standard UE5 Stride = 2
            // This points us to the [Header] of the Name Entry
            IntPtr nameEntry = IntPtr.Add(chunkPtr, (int)(withinChunkIndex * 2));

            // Read the 2-byte Header
            byte[] headerBytes = _memory.ReadBytes(nameEntry, 2);
            if (headerBytes == null) return "ERR_READ";

            // [FIXED LOGIC] 
            // UE5 Header is a 16-bit value (ushort).
            // Length is the value shifted right by 6 bits.
            // Wide-Flag is the very first bit.
            ushort header = BitConverter.ToUInt16(headerBytes, 0);

            int len = header >> 6;
            bool isWide = (header & 1) != 0;

            if (len <= 0 || len > 256)
            {
                // This will likely stop happening now, but good for debug
                return $"LEN_ERR[{headerBytes[0]:X2} {headerBytes[1]:X2}]";
            }

            // Offset 2 bytes to skip the header and read the text
            if (isWide)
            {
                byte[] b = _memory.ReadBytes(IntPtr.Add(nameEntry, 2), len * 2);
                return Encoding.Unicode.GetString(b);
            }
            else
            {
                byte[] b = _memory.ReadBytes(IntPtr.Add(nameEntry, 2), len);
                return Encoding.UTF8.GetString(b);
            }
        }
        catch { return "EX"; }
    }
}