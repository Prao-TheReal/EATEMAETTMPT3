using System.Collections.Generic;
using System.Drawing;

namespace Remnant2ESP;

public static class Offsets
{
    // [Global]
    public const int GWorld = 0x7B21CA0;

    // [UWorld]
    public const int GameInstance = 0x1B8;
    public const int PersistentLevel = 0x30;
    public const int Levels = 0x170; // <--- ADD THIS (TArray<ULevel*>)

    // [ULevel]
    public const int ActorsTArray = 0x98; // New: TArray<AActor*>
    public const int ActorsCount = 0xA0;

    // [New Offsets for Filtering]
    public const int ActorOwner = 0x140;       // AActor -> Owner
    public const int AcknowledgedPawn = 0x338; // PlayerController -> AcknowledgedPawn

    // [LocalPlayer]
    public const int LocalPlayers = 0x38;
    public const int LocalPlayer_First = 0x0;
    public const int PlayerController = 0x30;

    // [Camera]
    public const int CameraManager = 0x348;
    public const int CameraX = 0x12D0;
    public const int CameraY = 0x12D8;
    public const int CameraZ = 0x12E0;
    public const int CameraPitch = 0x12E8;
    public const int CameraYaw = 0x12F0;
    public const int CameraRoll = 0x12F8;
    public const int CameraFOV = 0x1300;

    // [Character Manager] (Existing Enemy List)
    public const int CharPath_288 = 0x288;
    public const int CharPath_B10 = 0xB10;
    public const int Characters = 0x38;
    public const int CharacterCount = 0x8;
    public const int FirstCharacter = 0x10;
    public const int CharacterStride = 0x8;

    // [AActor / APawn]
    public const int RootComponent = 0x198; // New: For Items
    public const int ActorID = 0x18;

    // [USceneComponent]
    public const int RelativeLocation = 0x128; // New

    // [ACharacter]
    public const int CharacterMovement = 0x320;
    public const int Mesh = 0x318;
    public const int HealthNormalized = 0x890; // New: Float (0.0 - 1.0)
    public const int SpawnState = 0x850; // FName

    // [UCharacterMovementComponent]
    public const int LocationX = 0x2D0;
    public const int LocationY = 0x2D8;
    public const int LocationZ = 0x2E0;

    // [Bones]
    public const int ComponentToWorld = 0x240;
    public const int BoneArray = 0x600;
    public const int BoneCount = 0x608;
}

public struct SocketEntry { public string SocketName; public string BoneName; }

public struct Vector3
{
    public double X, Y, Z;

    // --- ADD THIS LINE ---
    public static readonly Vector3 Zero = new Vector3(0, 0, 0);
    // ---------------------

    public Vector3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public double Distance(Vector3 other) { double dx = X - other.X; double dy = Y - other.Y; double dz = Z - other.Z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
    public bool IsZero => X == 0 && Y == 0 && Z == 0;
}

public struct Vector4 { public double X, Y, Z, W; public Vector4(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; } }

public struct Matrix4x4
{
    public double M11, M12, M13, M14;
    public double M21, M22, M23, M24;
    public double M31, M32, M33, M34;
    public double M41, M42, M43, M44;
}

public struct FTransform
{
    public Vector4 Rotation; public Vector3 Translation; public Vector3 Scale3D;
    public FTransform(Vector4 rot, Vector3 trans, Vector3 scale) { Rotation = rot; Translation = trans; Scale3D = scale; }
    public Matrix4x4 ToMatrixWithScale()
    {
        Matrix4x4 m = new Matrix4x4();
        double x2 = Rotation.X + Rotation.X; double y2 = Rotation.Y + Rotation.Y; double z2 = Rotation.Z + Rotation.Z;
        double xx2 = Rotation.X * x2; double yy2 = Rotation.Y * y2; double zz2 = Rotation.Z * z2;
        double yz2 = Rotation.Y * z2; double wx2 = Rotation.W * x2; double xy2 = Rotation.X * y2; double wz2 = Rotation.W * z2; double xz2 = Rotation.X * z2; double wy2 = Rotation.W * y2;
        m.M11 = (1.0 - (yy2 + zz2)) * Scale3D.X; m.M12 = (xy2 + wz2) * Scale3D.X; m.M13 = (xz2 - wy2) * Scale3D.X; m.M14 = 0.0;
        m.M21 = (xy2 - wz2) * Scale3D.Y; m.M22 = (1.0 - (xx2 + zz2)) * Scale3D.Y; m.M23 = (yz2 + wx2) * Scale3D.Y; m.M24 = 0.0;
        m.M31 = (xz2 + wy2) * Scale3D.Z; m.M32 = (yz2 - wx2) * Scale3D.Z; m.M33 = (1.0 - (xx2 + yy2)) * Scale3D.Z; m.M34 = 0.0;
        m.M41 = Translation.X; m.M42 = Translation.Y; m.M43 = Translation.Z; m.M44 = 1.0;
        return m;
    }
}

public struct CameraData { public Vector3 Location; public double Pitch; public double Yaw; public double Roll; public float FOV; public bool IsValid => !double.IsNaN(Yaw) && FOV > 0; }

public class CharacterData
{
    public IntPtr Address;
    public string Name; // Raw Name (e.g. BP_RootFlyer_C)
    public string DisplayName; // Clean Name (e.g. Root Flyer)
    public Vector3 Location;
    public bool IsPlayer;
    public double Distance;
    public int WeakspotIndex = -1;
    public Dictionary<int, Vector3> Bones { get; set; } = new();
}

public class ItemData
{
    public IntPtr Address;
    public string Name;
    public string DisplayName;
    public Vector3 Location;
    public double Distance;
    public Color RarityColor;
}