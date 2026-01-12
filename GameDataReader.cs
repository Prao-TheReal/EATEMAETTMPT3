using System.Diagnostics;

namespace Remnant2ESP;

public class GameDataReader
{
    private readonly MemoryReader _memory;
    private IntPtr _gWorld;
    private NameReader? _nameReader;
    private bool _gNamesFound = false;

    public IntPtr LocalPlayerAddress { get; private set; } = IntPtr.Zero;

    public GameDataReader(MemoryReader memory)
    {
        _memory = memory;
        // We do NOT scan here anymore. We wait for the game loop to trigger it.
    }

    // [FIX] This method is PUBLIC so OverlayForm can call it
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

        // [SAFETY] Do not scan if we are not attached
        if (_memory.BaseAddress == IntPtr.Zero) return;

        // Update GWorld one last time to be sure
        UpdateBaseAddress();

        long center = _gWorld.ToInt64();

        // If GWorld is still "small" (just an offset), we are not ready.
        if (center < 0x10000000) return;

        Debug.WriteLine($"[ESP] GWorld Anchor Validated: 0x{center:X}");
        Debug.WriteLine("[ESP] Scanning Orbit (±2MB)...");

        // Scan 2MB around GWorld
        int range = 0x200000;
        long start = center - range;

        // Read memory
        byte[] buffer = _memory.ReadBytes((IntPtr)start, range * 2);
        if (buffer == null) return;

        // Iterate pointers
        for (int i = 0; i < buffer.Length - 8; i += 8)
        {
            if (Math.Abs((start + i) - center) < 8) continue; // Skip GWorld itself

            long candidateVal = BitConverter.ToInt64(buffer, i);

            // Fast Filter
            if (candidateVal < 0x10000000000 || candidateVal > 0x7FFFFFFFFFFF) continue;

            // Check if this pointer is GNames
            if (CheckCandidate((IntPtr)candidateVal, (IntPtr)(start + i))) return;
        }

        Debug.WriteLine("[ESP] Orbit Scan Pass Complete (No Lock yet).");
    }

    private bool CheckCandidate(IntPtr structAddr, IntPtr sourceAddr)
    {
        // Check Offset 0x00 and 0x10
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

        // "None" (4E 6F 6E 65)
        for (int k = 2; k <= 6; k += 2)
        {
            if (data[k] == 0x4E && data[k + 1] == 0x6F && data[k + 2] == 0x6E && data[k + 3] == 0x65)
            {
                // Reject Text Headers (Anti-AllowNone)
                byte h1 = data[k - 1];
                byte h2 = data[k - 2];

                if (h1 < 0x20 && h2 < 0x20)
                {
                    Debug.WriteLine("========================================");
                    Debug.WriteLine($"[ESP] ORBIT LOCK SUCCESS!");
                    Debug.WriteLine($"[ESP] GNames Address: 0x{sourceAddr:X}");
                    Debug.WriteLine($"[ESP] Internal Offset: 0x{offset:X}");
                    Debug.WriteLine("========================================");

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
                Location = new Vector3(_memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraX)), _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraY)), _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraZ))),
                Pitch = _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraPitch)),
                Yaw = _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraYaw)),
                FOV = _memory.ReadFloat(IntPtr.Add(cameraManager, Offsets.CameraFOV))
            };
        }
        catch { return null; }
    }

    public List<CharacterData> GetAllCharacters(CameraData camera)
    {
        var characters = new List<CharacterData>();

        // Ensure we have scanned
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

                if (_gNamesFound && _nameReader != null)
                {
                    // [FIX] Read Name ID from 0x18 (FName), NOT 0x0C (Object Index)
                    int id = _memory.ReadInt32(IntPtr.Add(charAddr, 0x18));
                    string name = _nameReader.GetName(id);

                    //Debug.WriteLine($"[Entity] {name}");

                    // Filter out garbage
                    if (name.Contains("Bird") ||
                        name.Contains("Crow") ||
                        name.Contains("Ambient") ||
                        name.Contains("Critter") ||
                        name.Contains("Projectile") ||
                        name.Contains("Zone") ||
                        name.Contains("Default__") ||
                        name.Contains("VFX") ||
                        name.Contains("Context") ||
                        name.Contains("Manager") ||
                        name.Contains("Minion"))
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
                var dist = location.Distance(camera.Location) / 100.0;
                bool isMe = isAddressMatch || (dist < 2.5);

                var newChar = new CharacterData { Address = charAddr, Location = location, IsPlayer = isMe, Distance = dist };

                // Only read bones for valid enemies
                if (!newChar.IsPlayer && dist < 150)
                {
                    newChar.Bones = GetSkeleton(charAddr);
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
            int limit = Math.Min(boneCount, 120);
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
    public string GetDebugInfo() => _gNamesFound ? "GNames: OK" : "Scanning...";
}