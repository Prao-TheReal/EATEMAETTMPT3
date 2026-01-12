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

            // [FIXED] BACK TO STRIDE 2 (Standard for UE5)
            IntPtr nameEntry = IntPtr.Add(chunkPtr, (int)(withinChunkIndex * 2));

            byte[] header = _memory.ReadBytes(nameEntry, 2);
            if (header == null) return "ERR_READ";

            int len = header[0] >> 1;
            bool isWide = (header[0] & 1) != 0;

            if (len <= 0 || len > 256) return "LEN_ERR";

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