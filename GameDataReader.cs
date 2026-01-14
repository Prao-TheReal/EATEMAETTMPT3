using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace Remnant2ESP;

public class GameDataReader
{
    private readonly MemoryReader _memory;
    private IntPtr _gWorld;
    private NameReader? _nameReader;
    private bool _gNamesFound = false;

    // [SAFETY] Minimal lock
    private readonly object _dataLock = new object();

    private List<ItemData> _itemCache = new();
    private CancellationTokenSource _cancelSource;
    private bool _isScanning = false;
    public bool DebugMode { get; set; } = false;

    private Dictionary<IntPtr, Dictionary<string, int>> _boneNameCache = new();
    private Dictionary<IntPtr, Dictionary<int, string>> _boneIndexToNameCache = new();
    public IntPtr LocalPlayerAddress { get; private set; } = IntPtr.Zero;
    public IntPtr LocalPawn { get; private set; } = IntPtr.Zero;

    public GameDataReader(MemoryReader memory) { _memory = memory; }

    public void UpdateBaseAddress() { if (_memory.BaseAddress != IntPtr.Zero) { _gWorld = IntPtr.Add(_memory.BaseAddress, Offsets.GWorld); } }

    private void ScanAroundGWorld()
    {
        if (_gNamesFound) return; if (_memory.BaseAddress == IntPtr.Zero) return;
        UpdateBaseAddress(); long center = _gWorld.ToInt64(); if (center < 0x10000000) return;
        int range = 0x200000; long start = center - range; byte[] buffer = _memory.ReadBytes((IntPtr)start, range * 2); if (buffer == null) return;
        for (int i = 0; i < buffer.Length - 8; i += 8)
        {
            if (Math.Abs((start + i) - center) < 8) continue;
            long candidateVal = BitConverter.ToInt64(buffer, i);
            if (candidateVal < 0x10000000000 || candidateVal > 0x7FFFFFFFFFFF) continue;
            if (CheckCandidate((IntPtr)candidateVal, (IntPtr)(start + i))) return;
        }
    }

    private bool CheckCandidate(IntPtr structAddr, IntPtr sourceAddr) { if (ValidateChunk(structAddr, 0x00, sourceAddr)) return true; if (ValidateChunk(structAddr, 0x10, sourceAddr)) return true; return false; }
    private bool ValidateChunk(IntPtr baseAddr, int offset, IntPtr sourceAddr)
    {
        IntPtr chunk0 = _memory.ReadPointer(IntPtr.Add(baseAddr, offset)); if (chunk0 == IntPtr.Zero) return false; byte[] data = _memory.ReadBytes(chunk0, 16); if (data == null) return false;
        for (int k = 2; k <= 6; k += 2) { if (data[k] == 0x4E && data[k + 1] == 0x6F && data[k + 2] == 0x6E && data[k + 3] == 0x65) { byte h1 = data[k - 1]; byte h2 = data[k - 2]; if (h1 < 0x20 && h2 < 0x20) { Debug.WriteLine($"[ESP] GNames Found: 0x{sourceAddr:X}"); _nameReader = new NameReader(_memory, baseAddr, offset); _gNamesFound = true; return true; } } }
        return false;
    }

    public CameraData? GetCameraData()
    {
        try
        {
            if (_gWorld == IntPtr.Zero) UpdateBaseAddress(); var world = _memory.ReadPointer(_gWorld); if (world == IntPtr.Zero) return null;
            var gameInstance = _memory.ReadPointer(IntPtr.Add(world, Offsets.GameInstance)); if (gameInstance == IntPtr.Zero) return null;
            var localPlayersData = _memory.ReadPointer(IntPtr.Add(gameInstance, Offsets.LocalPlayers)); if (localPlayersData == IntPtr.Zero) return null;
            var localPlayer = _memory.ReadPointer(IntPtr.Add(localPlayersData, Offsets.LocalPlayer_First)); if (localPlayer == IntPtr.Zero) return null;
            LocalPlayerAddress = localPlayer;
            var playerController = _memory.ReadPointer(IntPtr.Add(localPlayer, Offsets.PlayerController)); if (playerController == IntPtr.Zero) return null;
            LocalPawn = _memory.ReadPointer(playerController + Offsets.AcknowledgedPawn);
            var cameraManager = _memory.ReadPointer(IntPtr.Add(playerController, Offsets.CameraManager)); if (cameraManager == IntPtr.Zero) return null;
            return new CameraData { Location = new Vector3(_memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraX)), _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraY)), _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraZ))), Pitch = _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraPitch)), Yaw = _memory.ReadDouble(IntPtr.Add(cameraManager, Offsets.CameraYaw)), FOV = _memory.ReadFloat(IntPtr.Add(cameraManager, Offsets.CameraFOV)) };
        }
        catch { return null; }
    }

    public bool IsVisible(IntPtr enemyMesh)
    {
        if (enemyMesh == IntPtr.Zero) return false;
        float enemyTime = _memory.ReadFloat(enemyMesh + 0x35C);
        IntPtr myMesh = _memory.ReadPointer(LocalPawn + Offsets.Mesh);
        if (myMesh == IntPtr.Zero) return true;
        float myTime = _memory.ReadFloat(myMesh + 0x35C);
        return Math.Abs(myTime - enemyTime) < 0.5f;
    }

    public void ScanForVisibility(CameraData camera) { }

    public void StartItemScanner()
    {
        if (_isScanning) return;
        _isScanning = true;
        _cancelSource = new CancellationTokenSource();
        Task.Run(async () => await ItemScanLoop(_cancelSource.Token));
    }

    public void StopItemScanner()
    {
        _cancelSource?.Cancel();
        _isScanning = false;
    }

    private async Task ItemScanLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var camera = GetCameraData();
                if (camera != null)
                {
                    var newItems = ScanForItemsInternal(camera.Value);
                    if (newItems != null) _itemCache = newItems;
                }
            }
            catch { }
            await Task.Delay(1000, token);
        }
    }

    public List<ItemData> GetItems(CameraData _) { return _itemCache; }

    private List<ItemData>? ScanForItemsInternal(CameraData camera)
    {
        var items = new List<ItemData>();
        if (!_gNamesFound) return null;

        try
        {
            var world = _memory.ReadPointer(_gWorld);
            if (world == IntPtr.Zero) return null;

            // [FIX] Use constructor instead of Vector3.Zero
            Vector3 myPos = new Vector3(0, 0, 0);
            if (LocalPawn != IntPtr.Zero)
            {
                IntPtr rootComp = _memory.ReadPointer(LocalPawn + Offsets.RootComponent);
                if (rootComp != IntPtr.Zero)
                {
                    IntPtr c2w = rootComp + Offsets.ComponentToWorld;
                    myPos = new Vector3(_memory.ReadDouble(c2w + 0x20), _memory.ReadDouble(c2w + 0x28), _memory.ReadDouble(c2w + 0x30));
                }
            }

            var levelsArray = _memory.ReadPointer(world + Offsets.Levels);
            var levelsCount = _memory.ReadInt32(world + Offsets.Levels + 8);
            if (levelsArray == IntPtr.Zero || levelsCount <= 0 || levelsCount > 1000) return null;

            for (int l = 0; l < levelsCount; l++)
            {
                IntPtr levelAddr = _memory.ReadPointer(levelsArray + (l * 8));
                if (levelAddr == IntPtr.Zero) continue;
                var actorsArray = _memory.ReadPointer(levelAddr + Offsets.ActorsTArray);
                var actorsCount = _memory.ReadInt32(levelAddr + Offsets.ActorsCount);
                if (actorsCount <= 0 || actorsCount > 10000) continue;

                for (int i = 0; i < actorsCount; i++)
                {
                    IntPtr actorAddr = _memory.ReadPointer(actorsArray + (i * 8));
                    if (actorAddr == IntPtr.Zero) continue;

                    int id = _memory.ReadInt32(actorAddr + Offsets.ActorID);
                    if (id == 0) continue;

                    string name = "";
                    lock (_dataLock) { name = _nameReader.GetName(id); }

                    bool isItem = name.Contains("Ring_") || name.Contains("Quest_") || name.Contains("Relic") || name.Contains("Weapon_") ||
                                  name.Contains("Amulet") || name.Contains("drop") || name.Contains("Loot") ||
                                  name.Contains("Ammo") || name.Contains("Scrap") || name.Contains("Tome") || name.Contains("Iron") ||
                                  name.Contains("Forged") || name.Contains("Galvanized") || name.Contains("Hardened") ||
                                  name.Contains("Simulacrum") || name.Contains("Lumenite") || name.Contains("Hidden") || name.Contains("Book") ||
                                  name.Contains("Chest") || name.Contains("Container") || name.Contains("Resource") ||
                                  name.Contains("Material") || name.Contains("Interactive_") || name.Contains("Item_") || name.Contains("Weapon_");

                    if (name.Contains("Default__") || name.Contains("Context") || name.Contains("3D") ||
                        name.Contains("Tree") || name.Contains("Leaves") || name.Contains("Dynamic") ||
                        name.Contains("Spawn") || name.Contains("Char") || name.Contains("FX") ||
                        name.Contains("Mesh") || name.Contains("Rock") || name.Contains("Zig") ||
                        name.Contains("Floor") || name.Contains("AI") || name.Contains("Vase") ||
                        name.Contains("Pot") || name.Contains("Pan") || name.Contains("VFX") ||
                        name.Contains("Sound") || name.Contains("") || name.Contains("Decal") || name.Contains("Volume") ||
                        name.Contains("Trigger") || name.Contains("Tile") || name.Contains("Camera") ||
                        name.Contains("LevelInstance") || name.Contains("Stand") || name.Contains("Vista")) { isItem = false; }

                    if (name.StartsWith("SM_") || name.Contains("StaticMesh")) { isItem = false; }
                    if (!isItem) continue;

                    Vector3 location = new Vector3(0, 0, 0);
                    IntPtr rootComp = _memory.ReadPointer(actorAddr + Offsets.RootComponent);
                    if (rootComp != IntPtr.Zero)
                    {
                        IntPtr c2w = rootComp + Offsets.ComponentToWorld;
                        location = new Vector3(_memory.ReadDouble(c2w + 0x20), _memory.ReadDouble(c2w + 0x28), _memory.ReadDouble(c2w + 0x30));
                    }
                    if (location.X == 0 && location.Y == 0) continue;

                    double distCalc = location.Distance(camera.Location) / 100.0;
                    if (distCalc > 150) continue;

                    // [FIX] Personal Bubble Check (Using IsZero property on instance)
                    if (!myPos.IsZero && location.Distance(myPos) < 1.5) continue;

                    Color color = Color.White;
                    if (name.Contains("Relic") || name.Contains("Gem") || name.Contains("Material")) color = Color.Orange;
                    else if (name.Contains("Ring") || name.Contains("Amulet")) color = Color.Purple;
                    else if (name.Contains("Simulacrum")) color = Color.Red;
                    else if (name.Contains("Lumenite")) color = Color.LightPink;
                    else if (name.Contains("Scrap")) color = Color.Gray;
                    else if (name.Contains("Iron")) color = Color.LightGreen;
                    else if (name.Contains("Ammo")) color = Color.Yellow;
                    else if (name.Contains("Book")) color = Color.Blue;
                    else if (name.Contains("Chest")) color = Color.Cyan;

                    items.Add(new ItemData { Address = actorAddr, Name = name, DisplayName = CleanName(name), Location = location, Distance = distCalc, RarityColor = color });
                }
            }
            if (items.Count == 0) return null;
            return items;
        }
        catch { return null; }
    }

    public string CleanName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "";
        string clean = rawName;
        clean = clean.Replace("BP_", "").Replace("_C", "").Replace("Character_", "").Replace("Enemy_", "").Replace("Item_", "").Replace("Default__", "");
        clean = Regex.Replace(clean, @"_\d+$", "");
        clean = clean.Replace("_", " ").Trim();
        if (clean.Length > 20) { clean = clean.Substring(0, 20); }
        return clean;
    }

    // --- [OPTIMIZED BROAD PHASE] ---
    public List<CharacterData> GetAllCharacters(CameraData camera)
    {
        var characters = new List<CharacterData>();
        if (!_gNamesFound) ScanAroundGWorld();

        try
        {
            var world = _memory.ReadPointer(_gWorld);
            if (world == IntPtr.Zero) return characters;

            var levelsArray = _memory.ReadPointer(world + Offsets.Levels);
            var levelsCount = _memory.ReadInt32(world + Offsets.Levels + 8);
            if (levelsArray == IntPtr.Zero || levelsCount <= 0 || levelsCount > 1000) return characters;

            for (int l = 0; l < levelsCount; l++)
            {
                IntPtr levelAddr = _memory.ReadPointer(levelsArray + (l * 8));
                if (levelAddr == IntPtr.Zero) continue;

                var actorsArray = _memory.ReadPointer(levelAddr + Offsets.ActorsTArray);
                var actorsCount = _memory.ReadInt32(levelAddr + Offsets.ActorsCount);
                if (actorsCount <= 0 || actorsCount > 10000) continue;

                for (int i = 0; i < actorsCount; i++)
                {
                    IntPtr charAddr = _memory.ReadPointer(actorsArray + (i * 8));
                    if (charAddr == IntPtr.Zero) continue;

                    // [OPTIMIZATION 1] Check Health First
                    float health = _memory.ReadFloat(charAddr + Offsets.HealthNormalized);
                    if (health <= 0.001f) continue;

                    // [OPTIMIZATION 2] Check Distance (Fast Math)
                    var moveComp = _memory.ReadPointer(IntPtr.Add(charAddr, Offsets.CharacterMovement));
                    if (moveComp == IntPtr.Zero) continue;

                    var location = new Vector3(_memory.ReadDouble(IntPtr.Add(moveComp, Offsets.LocationX)), _memory.ReadDouble(IntPtr.Add(moveComp, Offsets.LocationY)), _memory.ReadDouble(IntPtr.Add(moveComp, Offsets.LocationZ)));
                    if (location.IsZero) continue;

                    double dist = location.Distance(camera.Location) / 100.0;
                    if (dist > 150) continue; // Skip far objects

                    // [HEAVY OPERATION] Read Name
                    int id = _memory.ReadInt32(IntPtr.Add(charAddr, Offsets.ActorID));
                    string name = "";
                    lock (_dataLock) { name = _nameReader.GetName(id); }

                    if (string.IsNullOrEmpty(name)) continue;

                    if (name.Contains("Default__") || name.Contains("Context") ||
                        name.Contains("Camera") || name.Contains("Drone") || name.Contains("summon") || name.Contains("Volume") ||
                        name.Contains("Brush") || name.Contains("Light") || name.Contains("Deer") ||
                        name.Contains("Bird") || name.Contains("Critter") || name.StartsWith("BP") || name.Contains("Dep") || name.Contains("Crow") ||
                        name.Contains("Projectile") || name.Contains("Corpse")) continue;

                    bool isMe = (dist < 2.5) || (charAddr == LocalPlayerAddress);

                    var newChar = new CharacterData { Address = charAddr, Name = name, DisplayName = CleanName(name), Location = location, IsPlayer = isMe, Distance = dist };

                    if (!newChar.IsPlayer && dist < 80)
                    {
                        newChar.Bones = GetSkeleton(charAddr);
                        if (newChar.Bones.Count > 0)
                        {
                            IntPtr mesh = _memory.ReadPointer(charAddr + Offsets.Mesh);
                            newChar.WeakspotIndex = GetWeakspotBoneIndex(charAddr, mesh);
                            characters.Add(newChar);
                        }
                        else
                        {
                            characters.Add(newChar);
                        }
                    }
                }
            }
        }
        catch { }
        return characters;
    }

    private IntPtr GetCharacterManager() { try { var world = _memory.ReadPointer(_gWorld); if (world == IntPtr.Zero) return IntPtr.Zero; var gameInstance = _memory.ReadPointer(IntPtr.Add(world, Offsets.GameInstance)); if (gameInstance == IntPtr.Zero) return IntPtr.Zero; var p1 = _memory.ReadPointer(IntPtr.Add(gameInstance, Offsets.CharPath_288)); if (p1 == IntPtr.Zero) return IntPtr.Zero; var charMgr = _memory.ReadPointer(IntPtr.Add(p1, Offsets.CharPath_B10)); return charMgr; } catch { return IntPtr.Zero; } }

    public Dictionary<int, Vector3> GetSkeleton(IntPtr characterAddress)
    {
        var bones = new Dictionary<int, Vector3>();
        try
        {
            var mesh = _memory.ReadPointer(IntPtr.Add(characterAddress, Offsets.Mesh));
            if (mesh == IntPtr.Zero) return bones;

            // [FIX] OFFSET FALLBACK
            IntPtr skeletalMesh = _memory.ReadPointer(IntPtr.Add(mesh, 0x5B0));
            if (skeletalMesh == IntPtr.Zero) skeletalMesh = _memory.ReadPointer(IntPtr.Add(mesh, 0x5A0));
            if (skeletalMesh == IntPtr.Zero) skeletalMesh = _memory.ReadPointer(IntPtr.Add(mesh, 0x5C0));

            if (skeletalMesh != IntPtr.Zero && !_boneNameCache.ContainsKey(skeletalMesh)) MapSkeleton(skeletalMesh);

            long c2wBase = mesh.ToInt64() + Offsets.ComponentToWorld;
            byte[] c2wBuffer = _memory.ReadBytes((IntPtr)c2wBase, 0x60);
            if (c2wBuffer == null) return bones;

            var rot = new Vector4(BitConverter.ToDouble(c2wBuffer, 0), BitConverter.ToDouble(c2wBuffer, 8), BitConverter.ToDouble(c2wBuffer, 16), BitConverter.ToDouble(c2wBuffer, 24)); var trans = new Vector3(BitConverter.ToDouble(c2wBuffer, 0x20), BitConverter.ToDouble(c2wBuffer, 0x28), BitConverter.ToDouble(c2wBuffer, 0x30)); var scale = new Vector3(BitConverter.ToDouble(c2wBuffer, 0x40), BitConverter.ToDouble(c2wBuffer, 0x48), BitConverter.ToDouble(c2wBuffer, 0x50));
            FTransform componentToWorld = new FTransform(rot, trans, scale); Matrix4x4 c2wMatrix = componentToWorld.ToMatrixWithScale();
            var boneArray = _memory.ReadPointer(IntPtr.Add(mesh, Offsets.BoneArray)); var boneCount = _memory.ReadInt32(IntPtr.Add(mesh, Offsets.BoneCount)); if (boneArray == IntPtr.Zero || boneCount <= 0) return bones;

            // [FIX] INCREASED BONE LIMIT (Kept from prev step)
            int limit = Math.Min(boneCount, 200);
            int stride = 0x50; byte[] boneBuffer = _memory.ReadBytes(boneArray, limit * stride); if (boneBuffer == null) return bones;
            for (int i = 0; i < limit; i++) { int offset = i * stride; double x = BitConverter.ToDouble(boneBuffer, offset + 0x20); double y = BitConverter.ToDouble(boneBuffer, offset + 0x28); double z = BitConverter.ToDouble(boneBuffer, offset + 0x30); bones[i] = MultiplyMat(new Vector3(x, y, z), c2wMatrix); }
        }
        catch { }
        return bones;
    }
    private Vector3 MultiplyMat(Vector3 v, Matrix4x4 m) { return new Vector3(v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41, v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42, v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43); }
    public IntPtr GetHitLogComponent(IntPtr characterAddress) { return _memory.ReadPointer(characterAddress + 0x660); }

    public int GetWeakspotBoneIndex(IntPtr characterAddress, IntPtr meshAddress)
    {
        var hitLog = GetHitLogComponent(characterAddress); if (hitLog == IntPtr.Zero) return -1;
        IntPtr hitLocationsArray = IntPtr.Zero; int hitCount = 0;
        for (int offset = 0xA0; offset <= 0x100; offset += 8) { int count = _memory.ReadInt32(hitLog + offset + 8); if (count > 0 && count < 30) { hitLocationsArray = _memory.ReadPointer(hitLog + offset); hitCount = count; break; } }
        if (hitLocationsArray == IntPtr.Zero || hitCount == 0) return -1;
        int stride = 0x78;
        for (int i = 0; i < hitCount; i++) { IntPtr entryAddr = hitLocationsArray + (i * stride); byte[] weakBuffer = _memory.ReadBytes(entryAddr + 0x49, 1); bool isWeak = (weakBuffer != null && weakBuffer[0] != 0); if (isWeak) { int physMatID = _memory.ReadInt32(entryAddr + 0x00); string physMatName = ""; lock (_dataLock) { physMatName = _nameReader.GetName(physMatID); } if (string.IsNullOrEmpty(physMatName) || physMatName == "None") continue; string targetSocket = "VFX_" + physMatName; var sockets = ReadSockets(meshAddress); foreach (var socket in sockets) { if (socket.SocketName.Equals(targetSocket, StringComparison.OrdinalIgnoreCase)) { string weakBoneName = socket.BoneName; int boneIndex = GetBoneIndexByName(meshAddress, weakBoneName); if (boneIndex != -1) return boneIndex; } } } }
        return -1;
    }

    private List<SocketEntry> ReadSockets(IntPtr meshAddress)
    {
        var list = new List<SocketEntry>(); IntPtr skeletalMesh = _memory.ReadPointer(meshAddress + 0x5B0); if (skeletalMesh == IntPtr.Zero) return list; IntPtr socketsArray = _memory.ReadPointer(skeletalMesh + 0x4A0); int socketCount = _memory.ReadInt32(skeletalMesh + 0x4A0 + 8);
        if (socketsArray == IntPtr.Zero || socketCount <= 0 || socketCount > 200) return list;
        for (int i = 0; i < socketCount; i++) { IntPtr socketPtr = _memory.ReadPointer(socketsArray + (i * 8)); if (socketPtr == IntPtr.Zero) continue; int socketNameID = _memory.ReadInt32(socketPtr + 0x28); int boneNameID = _memory.ReadInt32(socketPtr + 0x30); string sName = ""; string bName = ""; lock (_dataLock) { sName = _nameReader.GetName(socketNameID); bName = _nameReader.GetName(boneNameID); } if (!string.IsNullOrEmpty(sName) && !string.IsNullOrEmpty(bName)) { list.Add(new SocketEntry { SocketName = sName, BoneName = bName }); } }
        return list;
    }

    private int[] MapSkeleton(IntPtr meshAsset)
    {
        IntPtr arrayData = _memory.ReadPointer(IntPtr.Add(meshAsset, 0x2E0)); int count = _memory.ReadInt32(IntPtr.Add(meshAsset, 0x2E0 + 8)); if (arrayData == IntPtr.Zero || count <= 0 || count > 512) return new int[0]; int[] parents = new int[count]; var nameMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var indexMap = new Dictionary<int, string>(); byte[] hierarchy = _memory.ReadBytes(arrayData, count * 12);
        if (hierarchy != null) { for (int i = 0; i < count; i++) { parents[i] = BitConverter.ToInt32(hierarchy, (i * 12) + 8); int nameID = BitConverter.ToInt32(hierarchy, (i * 12)); string boneName = ""; lock (_dataLock) { boneName = _nameReader.GetName(nameID); } if (!string.IsNullOrEmpty(boneName)) { if (!nameMap.ContainsKey(boneName)) nameMap[boneName] = i; if (!indexMap.ContainsKey(i)) indexMap[i] = boneName; } } }
        _boneNameCache[meshAsset] = nameMap; _boneIndexToNameCache[meshAsset] = indexMap; return parents;
    }

    public int GetBoneIndexByName(IntPtr meshAddress, string targetBoneName)
    {
        // Read the Mesh Asset (Skeleton Data)
        IntPtr meshAsset = _memory.ReadPointer(IntPtr.Add(meshAddress, 0x5B0));
        if (meshAsset == IntPtr.Zero) return -1;

        if (_boneNameCache.TryGetValue(meshAsset, out var nameMap))
        {
            // 1. Try Exact Match first (It's faster)
            if (nameMap.TryGetValue(targetBoneName, out int index)) return index;

            // 2. Try Partial Match (The Fix)
            // This iterates through all bones and checks if they CONTAIN your text.
            // It ignores case (Upper/Lower) to make it easier for you.
            foreach (var kvp in nameMap)
            {
                if (kvp.Key.Contains(targetBoneName, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }
        }
        return -1;
    }
    public string GetBoneName(IntPtr meshAddress, int index) { IntPtr meshAsset = _memory.ReadPointer(IntPtr.Add(meshAddress, 0x5B0)); if (meshAsset == IntPtr.Zero) return ""; if (_boneIndexToNameCache.TryGetValue(meshAsset, out var indexMap)) { if (indexMap.TryGetValue(index, out string name)) return name; } return ""; }
    public string GetDebugInfo() => _gNamesFound ? "GNames: OK" : "Scanning...";
    public void LogAllEntities() { }
}