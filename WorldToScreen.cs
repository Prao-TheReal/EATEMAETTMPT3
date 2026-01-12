using System;

namespace Remnant2ESP
{
    public class WorldToScreen
    {
        private readonly int _screenWidth;
        private readonly int _screenHeight;

        public WorldToScreen(int screenWidth, int screenHeight)
        {
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
        }

        public (int X, int Y)? Project(Vector3 worldPos, CameraData camera)
        {
            if (double.IsNaN(camera.Yaw) || camera.FOV <= 0) return null;

            // 1. Convert to Radians
            double radPitch = camera.Pitch * (Math.PI / 180.0);
            double radYaw = camera.Yaw * (Math.PI / 180.0);
            double radRoll = camera.Roll * (Math.PI / 180.0);

            // 2. Trig calculations
            double SP = Math.Sin(radPitch);
            double CP = Math.Cos(radPitch);
            double SY = Math.Sin(radYaw);
            double CY = Math.Cos(radYaw);
            double SR = Math.Sin(radRoll);
            double CR = Math.Cos(radRoll);

            // 3. Matrix Transformation (Standard UE5)
            Vector3 vForward = new Vector3(CP * CY, CP * SY, SP);
            Vector3 vRight = new Vector3(SR * SP * CY - CR * SY, SR * SP * SY + CR * CY, -SR * CP);
            Vector3 vUp = new Vector3(-(CR * SP * CY + SR * SY), CY * SR - CR * SP * SY, CR * CP);

            // 4. Delta Calculation
            Vector3 delta = new Vector3(
                worldPos.X - camera.Location.X,
                worldPos.Y - camera.Location.Y,
                worldPos.Z - camera.Location.Z
            );

            // 5. Camera Space Projection
            double vTransX = (delta.X * vRight.X) + (delta.Y * vRight.Y) + (delta.Z * vRight.Z);
            double vTransY = (delta.X * vUp.X) + (delta.Y * vUp.Y) + (delta.Z * vUp.Z);
            double vTransZ = (delta.X * vForward.X) + (delta.Y * vForward.Y) + (delta.Z * vForward.Z);

            // 6. Behind Camera Check
            if (vTransZ < 1.0) return null;

            // 7. Perspective Projection for Ultrawide
            float centerWeight = _screenWidth / 2.0f;
            double focalLength = centerWeight / Math.Tan(camera.FOV * Math.PI / 360.0);

            int screenX = (int)(centerWeight + vTransX * focalLength / vTransZ);
            int screenY = (int)((_screenHeight / 2.0f) - vTransY * focalLength / vTransZ);

            return (screenX, screenY);
        }
    }
}