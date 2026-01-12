using System.Diagnostics;
using System.Text;

namespace Remnant2ESP;

public class GameDataReader
{
    private readonly MemoryReader _memory;
    private IntPtr _gWorld;
    private NameReader? _nameReader;
    private bool _gNamesFound = false;

    private Dictionary<IntPtr, Dictionary<string, int>> _boneNameCache = new();
    private Dictionary<IntPtr, Dictionary<int, string>> _boneIndexToNameCache = new();

    public IntPtr LocalPlayerAddress { get; private set; } = IntPtr.Zero;

    public GameDataReader(MemoryReader memory)
    {
        _memory = memory;
    }

    public void UpdateBaseAddress()
    {
        if (_memory.BaseAddress != IntPtr.Zero)
        {
            _gWorld = IntPtr.Add(_memory.BaseAddress, Offsets.GWorld);
        }
    }

    private void ScanAroundGWorld()
    {
        if (_gNamesFound) return;
        if (_memory.BaseAddress == IntPtr.Zero) return;

        UpdateBaseAddress();
        long center = _gWorld.ToInt64();
        if (center < 0x10000000) return;

        int range = 0x200000;
        long start = center - range;
        byte[] buffer = _memory.ReadBytes((IntPtr)start, range * 2);
        if (buffer == null) return;

        for (int i = 0; i < buffer.Length - 8; i += 8)
        {
            if (Math.Abs((start + i) - center) < 8) continue;
            long candidateVal = BitConverter.ToInt64(buffer, i);
            if (candidateVal < 0x10000000000 || candidateVal > 0x7FFFFFFFFFFF) continue;
            if (CheckCandidate((IntPtr)candidateVal, (IntPtr)(start + i))) return;
        }
    }

    private bool CheckCandidate(IntPtr structAddr, IntPtr sourceAddr)
    {
        if (ValidateChunk(structAddr, 0x00, sourceAddr)) return true;
        if (ValidateChunk(structAddr, 0x10, sourceAddr)) return true;
        return false;
    }

    private bool ValidateChunk(IntPtr baseAddr, int offset, IntPtr sourceAddr)
    {
        IntPtr chunk0 = _memory.ReadPointer(IntPtr.Add(baseAddr, offset));
        if (chunk0 == IntPtr.Zero) return false;
        byte[] data = _memory.ReadBytes(chunk0, 16);
        if (data == null) return false;

        for (int k = 2; k <= 6; k += 2)
        {
            if (data[k] == 0x4E && data[k + 1] == 0x6F && data[k + 2] == 0x6E && data[k + 3] == 0x65)
            {
                byte h1 = data[k - 1];
                byte h2 = data[k - 2];
                if (h1 < 0x20 && h2 < 0x20)
                {
                    Debug.WriteLine($"[ESP] GNames Found: 0x{sourceAddr:X}");
                    _nameReader = new NameReader(_memory, baseAddr, offset);
                    _gNamesFound = true;
                    return true;
                }
            }
        }
        return false;
    }

    public CameraData? GetCameraData()
    {
        try
        {
            if (_gWorld == IntPtr.Zero) UpdateBaseAddress();
            var world = _memory.ReadPointer(_gWorld);
            if (world == IntPtr.Zero) return null;
            var gameInstance = _memory.ReadPointer(IntPtr.Add(world, Offsets.GameInstance));
            if (gameInstance == IntPtr.Zero) return null;
            var localPlayersData = _memory.ReadPointer(IntPtr.Add(gameInstance, Offsets.LocalPlayers));
            if (localPlayersData == IntPtr.Zero) return null;
            var localPlayer = _memory.ReadPointer(IntPtr.Add(localPlayersData, Offsets.LocalPlayer_First));
            if (localPlayer == IntPtr.Zero) return null;
            LocalPlayerAddress = localPlayer;
            var playerController = _memory.ReadPointer(IntPtr.Add(localPlayer, Offsets.PlayerController));
            if (playerController == IntPtr.Zero) return null;
            var cameraManager = _memory.ReadPointer(IntPtr.Add(playerController, Offsets.CameraManager));
            if (cameraManager == IntPtr.Zero) return null;
            return new CameraData
            {
                Location = new Vector3(
                    _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraX)),
                    _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraY)),
                    _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraZ))),
                Pitch = _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraPitch)),
                Yaw = _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraYaw)),
                FOV = _memory.ReadFloat(IntPtr.Add(cameraManager, Offsets.CameraFOV))
            };
        }
        catch { return null; }
    }

    // --- NEW DEBUG FUNCTION: DUMP EVERYTHING ---
    public void LogAllEntities()
    {
        if (!_gNamesFound) return;
        try
        {
            Debug.WriteLine("=== DUMPING ENTITY LIST ===");
            var charMgr = GetCharacterManager();
            if (charMgr == IntPtr.Zero) return;
            var charArrayData = _memory.ReadPointer(IntPtr.Add(charMgr, Offsets.Characters));
            var charCount = _memory.ReadInt32(IntPtr.Add(charMgr, Offsets.Characters + Offsets.CharacterCount));
            var firstCharAddr = _memory.ReadPointer(IntPtr.Add(charArrayData, Offsets.FirstCharacter));

            for (int i = 0; i < charCount; i++)
            {
                var charAddr = _memory.ReadPointer(IntPtr.Add(charArrayData, Offsets.FirstCharacter + (i * Offsets.CharacterStride)));
                if (charAddr == IntPtr.Zero) continue;

                int id = _memory.ReadInt32(IntPtr.Add(charAddr, 0x18));
                string name = _nameReader.GetName(id);

                // Print EVERYTHING to Output window
                Debug.WriteLine($"[{i}] Addr: {charAddr.ToString("X")} | Name: {name}");
            }
            Debug.WriteLine("=== DUMP COMPLETE ===");
        }
        catch (Exception ex) { Debug.WriteLine($"Dump Error: {ex.Message}"); }
    }

    public List<CharacterData> GetAllCharacters(CameraData camera)
    {
        var characters = new List<CharacterData>();
        if (!_gNamesFound) ScanAroundGWorld();

        try
        {
            var charMgr = GetCharacterManager();
            if (charMgr == IntPtr.Zero) return characters;
            var charArrayData = _memory.ReadPointer(IntPtr.Add(charMgr, Offsets.Characters));
            var charCount = _memory.ReadInt32(IntPtr.Add(charMgr, Offsets.Characters + Offsets.CharacterCount));
            if (charCount <= 0 || charCount > 200) return characters;
            var firstCharAddr = _memory.ReadPointer(IntPtr.Add(charArrayData, Offsets.FirstCharacter));

            for (int i = 0; i < charCount; i++)
            {
                var charAddr = _memory.ReadPointer(IntPtr.Add(charArrayData, Offsets.FirstCharacter + (i * Offsets.CharacterStride)));
                if (charAddr == IntPtr.Zero) continue;

                string name = "Unknown";
                if (_gNamesFound && _nameReader != null)
                {
                    int id = _memory.ReadInt32(IntPtr.Add(charAddr, 0x18));
                    name = _nameReader.GetName(id);

                    if (name.Contains("Bird") || name.Contains("Crow") || name.Contains("Ambient") ||
                        name.Contains("Critter") || name.Contains("Projectile") || name.Contains("Zone") ||
                        name.Contains("Default__") || name.Contains("VFX") || name.Contains("Context") ||
                        name.Contains("Manager") || name.Contains("Minion"))
                    {
                        continue;
                    }
                }

                bool isAddressMatch = (charAddr == LocalPlayerAddress) || (charAddr == firstCharAddr);
                var moveComp = _memory.ReadPointer(IntPtr.Add(charAddr, Offsets.CharacterMovement));
                if (moveComp == IntPtr.Zero) continue;

                var location = new Vector3(
                    _memory.ReadDouble(IntPtr.Add(moveComp, Offsets.LocationX)),
                    _memory.ReadDouble(IntPtr.Add(moveComp, Offsets.LocationY)),
                    _memory.ReadDouble(IntPtr.Add(moveComp, Offsets.LocationZ))
                );

                if (location.IsZero) continue;

                double dist = location.Distance(camera.Location) / 100.0;
                bool isMe = isAddressMatch || (dist < 2.5);

                var newChar = new CharacterData { Address = charAddr, Name = name, Location = location, IsPlayer = isMe, Distance = dist };

                if (!newChar.IsPlayer && dist < 60)
                {
                    newChar.Bones = GetSkeleton(charAddr);
                    IntPtr mesh = _memory.ReadPointer(charAddr + Offsets.Mesh);
                    newChar.WeakspotIndex = GetWeakspotBoneIndex(charAddr, mesh);
                }

                characters.Add(newChar);
            }
        }
        catch { }
        return characters;
    }

    private IntPtr GetCharacterManager()
    {
        try
        {
            var world = _memory.ReadPointer(_gWorld);
            if (world == IntPtr.Zero) return IntPtr.Zero;
            var gameInstance = _memory.ReadPointer(IntPtr.Add(world, Offsets.GameInstance));
            if (gameInstance == IntPtr.Zero) return IntPtr.Zero;
            var p1 = _memory.ReadPointer(IntPtr.Add(gameInstance, Offsets.CharPath_288));
            if (p1 == IntPtr.Zero) return IntPtr.Zero;
            var charMgr = _memory.ReadPointer(IntPtr.Add(p1, Offsets.CharPath_B10));
            return charMgr;
        }
        catch { return IntPtr.Zero; }
    }

    public Dictionary<int, Vector3> GetSkeleton(IntPtr characterAddress)
    {
        var bones = new Dictionary<int, Vector3>();
        try
        {
            var mesh = _memory.ReadPointer(IntPtr.Add(characterAddress, Offsets.Mesh));
            if (mesh == IntPtr.Zero) return bones;

            IntPtr skeletalMesh = _memory.ReadPointer(IntPtr.Add(mesh, 0x5B0));
            if (skeletalMesh != IntPtr.Zero)
            {
                if (!_boneNameCache.ContainsKey(skeletalMesh))
                {
                    MapSkeleton(skeletalMesh);
                }
            }

            long c2wBase = mesh.ToInt64() + Offsets.ComponentToWorld;
            byte[] c2wBuffer = _memory.ReadBytes((IntPtr)c2wBase, 0x60);
            if (c2wBuffer == null) return bones;

            var rot = new Vector4(BitConverter.ToDouble(c2wBuffer, 0), BitConverter.ToDouble(c2wBuffer, 8), BitConverter.ToDouble(c2wBuffer, 16), BitConverter.ToDouble(c2wBuffer, 24));
            var trans = new Vector3(BitConverter.ToDouble(c2wBuffer, 0x20), BitConverter.ToDouble(c2wBuffer, 0x28), BitConverter.ToDouble(c2wBuffer, 0x30));
            var scale = new Vector3(BitConverter.ToDouble(c2wBuffer, 0x40), BitConverter.ToDouble(c2wBuffer, 0x48), BitConverter.ToDouble(c2wBuffer, 0x50));

            FTransform componentToWorld = new FTransform(rot, trans, scale);
            Matrix4x4 c2wMatrix = componentToWorld.ToMatrixWithScale();

            var boneArray = _memory.ReadPointer(IntPtr.Add(mesh, Offsets.BoneArray));
            var boneCount = _memory.ReadInt32(IntPtr.Add(mesh, Offsets.BoneCount));
            if (boneArray == IntPtr.Zero || boneCount <= 0) return bones;

            int limit = Math.Min(boneCount, 150);
            int stride = 0x50;
            byte[] boneBuffer = _memory.ReadBytes(boneArray, limit * stride);
            if (boneBuffer == null) return bones;

            for (int i = 0; i < limit; i++)
            {
                int offset = i * stride;
                double x = BitConverter.ToDouble(boneBuffer, offset + 0x20);
                double y = BitConverter.ToDouble(boneBuffer, offset + 0x28);
                double z = BitConverter.ToDouble(boneBuffer, offset + 0x30);
                bones[i] = MultiplyMat(new Vector3(x, y, z), c2wMatrix);
            }
        }
        catch { }
        return bones;
    }

    private Vector3 MultiplyMat(Vector3 v, Matrix4x4 m)
    {
        return new Vector3(v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41, v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42, v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43);
    }

    public IntPtr GetHitLogComponent(IntPtr characterAddress)
    {
        return _memory.ReadPointer(characterAddress + 0x660);
    }

    public int GetWeakspotBoneIndex(IntPtr characterAddress, IntPtr meshAddress)
    {
        var hitLog = GetHitLogComponent(characterAddress);
        if (hitLog == IntPtr.Zero) return -1;

        IntPtr hitLocationsArray = IntPtr.Zero;
        int hitCount = 0;

        for (int offset = 0xA0; offset <= 0x100; offset += 8)
        {
            int count = _memory.ReadInt32(hitLog + offset + 8);
            if (count > 0 && count < 30)
            {
                hitLocationsArray = _memory.ReadPointer(hitLog + offset);
                hitCount = count;
                break;
            }
        }

        if (hitLocationsArray == IntPtr.Zero || hitCount == 0) return -1;

        int stride = 0x78;

        for (int i = 0; i < hitCount; i++)
        {
            IntPtr entryAddr = hitLocationsArray + (i * stride);
            byte[] weakBuffer = _memory.ReadBytes(entryAddr + 0x49, 1);
            bool isWeak = (weakBuffer != null && weakBuffer[0] != 0);

            if (isWeak)
            {
                int physMatID = _memory.ReadInt32(entryAddr + 0x00);
                string physMatName = _nameReader.GetName(physMatID);

                if (string.IsNullOrEmpty(physMatName) || physMatName == "None") continue;

                string targetSocket = "VFX_" + physMatName;
                var sockets = ReadSockets(meshAddress);

                foreach (var socket in sockets)
                {
                    if (socket.SocketName.Equals(targetSocket, StringComparison.OrdinalIgnoreCase))
                    {
                        string weakBoneName = socket.BoneName;
                        int boneIndex = GetBoneIndexByName(meshAddress, weakBoneName);
                        if (boneIndex != -1) return boneIndex;
                    }
                }
            }
        }

        return -1;
    }

    private List<SocketEntry> ReadSockets(IntPtr meshAddress)
    {
        var list = new List<SocketEntry>();
        IntPtr skeletalMesh = _memory.ReadPointer(meshAddress + 0x5B0);
        if (skeletalMesh == IntPtr.Zero) return list;

        IntPtr socketsArray = _memory.ReadPointer(skeletalMesh + 0x4A0);
        int socketCount = _memory.ReadInt32(skeletalMesh + 0x4A0 + 8);

        if (socketsArray == IntPtr.Zero || socketCount <= 0 || socketCount > 200) return list;

        for (int i = 0; i < socketCount; i++)
        {
            IntPtr socketPtr = _memory.ReadPointer(socketsArray + (i * 8));
            if (socketPtr == IntPtr.Zero) continue;

            int socketNameID = _memory.ReadInt32(socketPtr + 0x28);
            int boneNameID = _memory.ReadInt32(socketPtr + 0x30);

            string sName = _nameReader.GetName(socketNameID);
            string bName = _nameReader.GetName(boneNameID);

            if (!string.IsNullOrEmpty(sName) && !string.IsNullOrEmpty(bName))
            {
                list.Add(new SocketEntry { SocketName = sName, BoneName = bName });
            }
        }
        return list;
    }

    private int[] MapSkeleton(IntPtr meshAsset)
    {
        IntPtr arrayData = _memory.ReadPointer(IntPtr.Add(meshAsset, 0x2E0));
        int count = _memory.ReadInt32(IntPtr.Add(meshAsset, 0x2E0 + 8));

        if (arrayData == IntPtr.Zero || count <= 0 || count > 512) return new int[0];

        int[] parents = new int[count];
        var nameMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var indexMap = new Dictionary<int, string>();

        byte[] hierarchy = _memory.ReadBytes(arrayData, count * 12);

        if (hierarchy != null)
        {
            for (int i = 0; i < count; i++)
            {
                parents[i] = BitConverter.ToInt32(hierarchy, (i * 12) + 8);
                int nameID = BitConverter.ToInt32(hierarchy, (i * 12));
                string boneName = _nameReader.GetName(nameID);

                if (!string.IsNullOrEmpty(boneName))
                {
                    if (!nameMap.ContainsKey(boneName))
                        nameMap[boneName] = i;

                    if (!indexMap.ContainsKey(i))
                        indexMap[i] = boneName;
                }
            }
        }

        _boneNameCache[meshAsset] = nameMap;
        _boneIndexToNameCache[meshAsset] = indexMap;
        return parents;
    }

    public int GetBoneIndexByName(IntPtr meshAddress, string targetBoneName)
    {
        IntPtr meshAsset = _memory.ReadPointer(IntPtr.Add(meshAddress, 0x5B0));
        if (meshAsset == IntPtr.Zero) return -1;

        if (_boneNameCache.TryGetValue(meshAsset, out var nameMap))
        {
            if (nameMap.TryGetValue(targetBoneName, out int index))
                return index;
        }
        return -1;
    }

    public int GetHeadBoneIndex(IntPtr meshAddress)
    {
        IntPtr meshAsset = _memory.ReadPointer(IntPtr.Add(meshAddress, 0x5B0));
        if (meshAsset == IntPtr.Zero) return -1;

        if (_boneNameCache.TryGetValue(meshAsset, out var nameMap))
        {
            foreach (var kvp in nameMap)
            {
                string name = kvp.Key.ToLower();
                if (name.Contains("weakpoint") || name.Contains("Face") || name.Contains("upper") || name.Contains("eye"))
                {
                    return kvp.Value;
                }
            }
        }
        return -1;
    }

    public string GetBoneName(IntPtr meshAddress, int index)
    {
        IntPtr meshAsset = _memory.ReadPointer(IntPtr.Add(meshAddress, 0x5B0));
        if (meshAsset == IntPtr.Zero) return "";

        if (_boneIndexToNameCache.TryGetValue(meshAsset, out var indexMap))
        {
            if (indexMap.TryGetValue(index, out string name))
                return name;
        }
        return "";
    }

    public string GetDebugInfo() => _gNamesFound ? "GNames: OK" : "Scanning...";
}