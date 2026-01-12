namespace Remnant2ESP;

/// <summary>
/// VERIFIED offsets from manual testing - January 2025
/// </summary>
public static class Offsets
{
    // Base address offset from module base
    public const int GWorld = 0x7B21CA0;
    
    // ============================================
    // PATH TO CAMERA (verified working)
    // GWorld → GameInstance → LocalPlayers → PlayerController → CameraManager
    // ============================================
    public const int GameInstance = 0x1B8;
    public const int LocalPlayers = 0x38;        // TArray<ULocalPlayer*>
    public const int LocalPlayer_First = 0x0;    // First element
    public const int PlayerController = 0x30;    // LocalPlayer → PlayerController
    public const int CameraManager = 0x348;      // PlayerController → CameraManager

    // Camera data offsets (from CameraManager) - NEW OFFSETS
    public const int CameraX = 0x12D0;           // Double
    public const int CameraY = 0x12D8;           // Double
    public const int CameraZ = 0x12E0;           // Double
    public const int CameraPitch = 0x12E8;       // Double (8 bytes)
    public const int CameraYaw = 0x12F0;         // Double (8 bytes) - CHANGE THIS
    public const int CameraRoll = 0x12F8;        // Double (8 bytes) - ADD THIS
    public const int CameraFOV = 0x1300;         // Float (4 bytes)

    // ============================================
    // PATH TO CHARACTERS (new stable path via GameInstance)
    // GWorld → 1B8 → 288 → B10 → 38 → Characters
    // ============================================
    public const int CharPath_288 = 0x288;       // From GameInstance
    public const int CharPath_B10 = 0xB10;
    public const int Characters = 0x38;          // TArray<ACharacterGunfire*>
    public const int CharacterCount = 0x8;       // Offset within TArray for count
    
    // Character array indexing
    public const int FirstCharacter = 0x10;      // Player is usually first
    public const int CharacterStride = 0x8;      // Each character pointer
    
    // Character → Movement Component → Location
    public const int CharacterMovement = 0x320;
    public const int LocationX = 0x2D0;          // Double
    public const int LocationY = 0x2D8;          // Double
    public const int LocationZ = 0x2E0;          // Double

    // ============================================
    // BONE & MESH OFFSETS (UE5 / Remnant 2)
    // ============================================
    public const int Mesh = 0x318;               // Character -> Mesh (USkeletalMeshComponent)
    public const int ComponentToWorld = 0x240;   // USceneComponent -> ComponentToWorld (FTransform)
    public const int BoneArray = 0x600;          // USkinnedMeshComponent -> CachedBoneSpaceTransforms (TArray<FTransform>)
    public const int BoneCount = 0x608;          // Array Count (usually +0x8 from Data)
}

public struct Vector3
{
    public double X, Y, Z;
    
    public Vector3(double x, double y, double z)
    {
        X = x; Y = y; Z = z;
    }
    
    public double Distance(Vector3 other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        double dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
    
    public static Vector3 operator -(Vector3 a, Vector3 b) 
        => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    
    public double Dot(Vector3 other) 
        => X * other.X + Y * other.Y + Z * other.Z;
    
    public bool IsZero => X == 0 && Y == 0 && Z == 0;
    
    public override string ToString() => $"({X:F1}, {Y:F1}, {Z:F1})";
}

public struct FTransform
{
    public Vector4 Rotation; // Quat (X, Y, Z, W)
    public Vector3 Translation;
    public Vector3 Scale3D;

    public FTransform(Vector4 rot, Vector3 trans, Vector3 scale)
    {
        Rotation = rot;
        Translation = trans;
        Scale3D = scale;
    }

    public Matrix4x4 ToMatrixWithScale()
    {
        Matrix4x4 m = new Matrix4x4();

        double x2 = Rotation.X + Rotation.X;
        double y2 = Rotation.Y + Rotation.Y;
        double z2 = Rotation.Z + Rotation.Z;

        double xx2 = Rotation.X * x2;
        double yy2 = Rotation.Y * y2;
        double zz2 = Rotation.Z * z2;

        double yz2 = Rotation.Y * z2;
        double wx2 = Rotation.W * x2;
        double xy2 = Rotation.X * y2;
        double wz2 = Rotation.W * z2;
        double xz2 = Rotation.X * z2;
        double wy2 = Rotation.W * y2;

        m.M11 = (1.0 - (yy2 + zz2)) * Scale3D.X;
        m.M12 = (xy2 + wz2) * Scale3D.X;
        m.M13 = (xz2 - wy2) * Scale3D.X;
        m.M14 = 0.0;

        m.M21 = (xy2 - wz2) * Scale3D.Y;
        m.M22 = (1.0 - (xx2 + zz2)) * Scale3D.Y;
        m.M23 = (yz2 + wx2) * Scale3D.Y;
        m.M24 = 0.0;

        m.M31 = (xz2 + wy2) * Scale3D.Z;
        m.M32 = (yz2 - wx2) * Scale3D.Z;
        m.M33 = (1.0 - (xx2 + yy2)) * Scale3D.Z;
        m.M34 = 0.0;

        m.M41 = Translation.X;
        m.M42 = Translation.Y;
        m.M43 = Translation.Z;
        m.M44 = 1.0;

        return m;
    }
}

public struct Vector4
{
    public double X, Y, Z, W;
    public Vector4(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
}

public struct Matrix4x4
{
    public double M11, M12, M13, M14;
    public double M21, M22, M23, M24;
    public double M31, M32, M33, M34;
    public double M41, M42, M43, M44;
}

public struct CameraData
{
    public Vector3 Location;
    public double Pitch;
    public double Yaw;    // Must be double to match 8-byte memory
    public double Roll;   // Must be double
    public float FOV;

    public bool IsValid => !double.IsNaN(Yaw) && FOV > 0;
}

public class CharacterData
{
    public IntPtr Address;
    public Vector3 Location;
    public bool IsPlayer;
    public double Distance;

    // This will now initialize correctly when you create the object
    public Dictionary<int, Vector3> Bones { get; set; } = new();
}
