using System;
// We do not need System.Numerics here. 
// We will use your project's local Vector3 and standard System.Math (doubles).

namespace Remnant2ESP
{
    public class WorldToScreen
    {
        // Screen stats
        private float _screenWidth;
        private float _screenHeight;
        private float _screenCenterX;
        private float _screenCenterY;

        // Matrix Components 
        // CHANGED TO DOUBLE: This prevents the "cannot convert double to float" errors
        private double _m11, _m12, _m13; // Forward
        private double _m21, _m22, _m23; // Right
        private double _m31, _m32, _m33; // Up

        private Vector3 _camLocation;
        private double _focalLength;
        private bool _isMatrixReady;

        public WorldToScreen(int screenWidth, int screenHeight)
        {
            Resize(screenWidth, screenHeight);
        }

        public void Resize(int width, int height)
        {
            _screenWidth = (float)width;
            _screenHeight = (float)height;
            _screenCenterX = width / 2.0f;
            _screenCenterY = height / 2.0f;
        }

        public void UpdateCamera(CameraData camera)
        {
            if (camera.FOV <= 0) return;

            _camLocation = camera.Location;

            // 1. Trig Setup (All Doubles - No Casting Needed yet)
            double radPitch = camera.Pitch * (Math.PI / 180.0);
            double radYaw = camera.Yaw * (Math.PI / 180.0);
            double radRoll = camera.Roll * (Math.PI / 180.0);

            double sp = Math.Sin(radPitch);
            double cp = Math.Cos(radPitch);
            double sy = Math.Sin(radYaw);
            double cy = Math.Cos(radYaw);
            double sr = Math.Sin(radRoll);
            double cr = Math.Cos(radRoll);

            // 2. Matrix Transformation
            // Forward (X Axis)
            _m11 = cp * cy;
            _m12 = cp * sy;
            _m13 = sp;

            // Right (Y Axis)
            _m21 = sr * sp * cy - cr * sy;
            _m22 = sr * sp * sy + cr * cy;
            _m23 = -sr * cp;

            // Up (Z Axis)
            _m31 = -(cr * sp * cy + sr * sy);
            _m32 = cy * sr - cr * sp * sy;
            _m33 = cr * cp;

            // 3. Focal Length
            _focalLength = _screenCenterX / Math.Tan(camera.FOV * Math.PI / 360.0);

            _isMatrixReady = true;
        }

        public (float X, float Y)? Project(Vector3 worldPos)
        {
            if (!_isMatrixReady) return null;

            // 4. Delta Calculation 
            double deltaX = (double)worldPos.X - (double)_camLocation.X;
            double deltaY = (double)worldPos.Y - (double)_camLocation.Y;
            double deltaZ = (double)worldPos.Z - (double)_camLocation.Z;

            // 5. Dot Product (All Double Math)
            double vTransX = (deltaX * _m21) + (deltaY * _m22) + (deltaZ * _m23);
            double vTransY = (deltaX * _m31) + (deltaY * _m32) + (deltaZ * _m33);
            double vTransZ = (deltaX * _m11) + (deltaY * _m12) + (deltaZ * _m13);

            // 6. Behind Camera Check
            if (vTransZ < 1.0) return null;

            // 7. Perspective Projection (Double -> Float)
            // This is the ONLY place we cast to float, right at the end.
            float screenX = (float)(_screenCenterX + vTransX * (_focalLength / vTransZ));
            float screenY = (float)(_screenCenterY - vTransY * (_focalLength / vTransZ));

            return (screenX, screenY);
        }
    }
}