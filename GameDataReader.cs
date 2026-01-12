using System.Diagnostics;

namespace Remnant2ESP;

public class GameDataReader
{
    private readonly MemoryReader _memory;
    private IntPtr _gWorld;

    // Store your own address to prevent self-targeting
    public IntPtr LocalPlayerAddress { get; private set; } = IntPtr.Zero;

    public GameDataReader(MemoryReader memory)
    {
        _memory = memory;
        UpdateBaseAddress();
    }

    public void UpdateBaseAddress()
    {
        _gWorld = IntPtr.Add(_memory.BaseAddress, Offsets.GWorld);
    }

    public CameraData? GetCameraData()
    {
        try
        {
            var world = _memory.ReadPointer(_gWorld);
            if (world == IntPtr.Zero) return null;

            var gameInstance = _memory.ReadPointer(IntPtr.Add(world, Offsets.GameInstance));
            if (gameInstance == IntPtr.Zero) return null;

            var localPlayersData = _memory.ReadPointer(IntPtr.Add(gameInstance, Offsets.LocalPlayers));
            if (localPlayersData == IntPtr.Zero) return null;

            var localPlayer = _memory.ReadPointer(IntPtr.Add(localPlayersData, Offsets.LocalPlayer_First));
            if (localPlayer == IntPtr.Zero) return null;

            // [FIX] Save our own address so we can recognize ourselves later
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
                    _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraZ))
                ),
                Pitch = _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraPitch)),
                Yaw = _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraYaw)),
                FOV = _memory.ReadFloat(IntPtr.Add(cameraManager, Offsets.CameraFOV))
            };
        }
        catch
        {
            return null;
        }
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
        catch
        {
            return IntPtr.Zero;
        }
    }

    public List<CharacterData> GetAllCharacters(CameraData camera)
    {
        var characters = new List<CharacterData>();

        try
        {
            var charMgr = GetCharacterManager();
            if (charMgr == IntPtr.Zero) return characters;

            var charArrayData = _memory.ReadPointer(IntPtr.Add(charMgr, Offsets.Characters));
            if (charArrayData == IntPtr.Zero) return characters;

            var charCount = _memory.ReadInt32(IntPtr.Add(charMgr, Offsets.Characters + Offsets.CharacterCount));
            if (charCount <= 0 || charCount > 200) return characters;

            // The first character in the array is usually the local player, but we check explicitly
            var firstCharAddr = _memory.ReadPointer(IntPtr.Add(charArrayData, Offsets.FirstCharacter));

            for (int i = 0; i < charCount; i++)
            {
                var charAddr = _memory.ReadPointer(IntPtr.Add(charArrayData, Offsets.FirstCharacter + (i * Offsets.CharacterStride)));
                if (charAddr == IntPtr.Zero) continue;

                // [FIX] Strict Self-Check
                // 1. Is it the LocalPlayer pointer? 
                // 2. Is it the first char in the list? 
                // 3. Or is it insanely close to the camera?
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

                // Secondary distance check for TPS camera safety (8 meters)
                bool isMe = isAddressMatch || (dist < 2.5);

                var newChar = new CharacterData
                {
                    Address = charAddr,
                    Location = location,
                    IsPlayer = isMe,
                    Distance = dist
                };

                // Only read bones if it's NOT YOU and close enough
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

            var rot = new Vector4(
                BitConverter.ToDouble(c2wBuffer, 0),
                BitConverter.ToDouble(c2wBuffer, 8),
                BitConverter.ToDouble(c2wBuffer, 16),
                BitConverter.ToDouble(c2wBuffer, 24)
            );

            var trans = new Vector3(
                BitConverter.ToDouble(c2wBuffer, 0x20),
                BitConverter.ToDouble(c2wBuffer, 0x28),
                BitConverter.ToDouble(c2wBuffer, 0x30)
            );

            var scale = new Vector3(
                BitConverter.ToDouble(c2wBuffer, 0x40),
                BitConverter.ToDouble(c2wBuffer, 0x48),
                BitConverter.ToDouble(c2wBuffer, 0x50)
            );

            FTransform componentToWorld = new FTransform(rot, trans, scale);
            Matrix4x4 c2wMatrix = componentToWorld.ToMatrixWithScale();

            var boneArray = _memory.ReadPointer(IntPtr.Add(mesh, Offsets.BoneArray));
            var boneCount = _memory.ReadInt32(IntPtr.Add(mesh, Offsets.BoneCount));

            if (boneArray == IntPtr.Zero || boneCount <= 0) return bones;

            int limit = Math.Min(boneCount, 120);
            int stride = 0x50;
            int totalBytes = limit * stride;

            byte[] boneBuffer = _memory.ReadBytes(boneArray, totalBytes);
            if (boneBuffer == null) return bones;

            for (int i = 0; i < limit; i++)
            {
                int offset = i * stride;
                double x = BitConverter.ToDouble(boneBuffer, offset + 0x20);
                double y = BitConverter.ToDouble(boneBuffer, offset + 0x28);
                double z = BitConverter.ToDouble(boneBuffer, offset + 0x30);

                var bTrans = new Vector3(x, y, z);
                Vector3 finalPos = MultiplyMat(bTrans, c2wMatrix);
                bones[i] = finalPos;
            }
        }
        catch { }

        return bones;
    }

    private Vector3 MultiplyMat(Vector3 v, Matrix4x4 m)
    {
        return new Vector3(
            v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41,
            v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42,
            v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43
        );
    }

    public string GetDebugInfo()
    {
        try
        {
            var world = _memory.ReadPointer(_gWorld);
            if (world == IntPtr.Zero) return "GWorld: NULL";
            return "ESP Active";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}