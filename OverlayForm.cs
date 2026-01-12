using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Numerics; // Ensure this is here

namespace Remnant2ESP;

public class OverlayForm : Form
{
    // [Mouse Import]
    [DllImport("user32.dll")]
    static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
    private const int MOUSEEVENTF_MOVE = 0x0001;

    // [Standard Imports]
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOPMOST = 0x8;

    // [Screen Settings]
    private const int SCREEN_WIDTH = 5120; // Your resolution
    private const int SCREEN_HEIGHT = 1440;
    private const int TARGET_FPS = 144;

    // AIMBOT SETTINGS
    private const float AIM_FOV_RADIUS = 100.0f;

    private readonly MemoryReader _memory;
    private readonly GameDataReader _gameReader;
    private readonly WorldToScreen _w2s;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly System.Windows.Forms.Timer _attachTimer;

    private Thread? _logicThread;
    private bool _isRunning = true;
    private readonly object _dataLock = new object();

    // [Toggles]
    private bool _espEnabled = true;   // F5
    private bool _drawLines = true;    // F6
    private bool _drawWeakspot = true; // F7
    private bool _drawBox = true;      // F8
    private bool _drawDist = true;     // F9
    private bool _drawSkeleton = true; // F10 [NEW]

    private bool _isAttached = false;
    private CameraData _lastCamera;
    private List<CharacterData> _lastCharacters = new();
    private string _debugInfo = "";

    private int _frameCount = 0;
    private DateTime _lastFpsTime = DateTime.Now;
    private int _currentFps = 0;

    // [Game Resources]
    private readonly Pen _enemyPen;
    private readonly Pen _enemyPenFar;
    private readonly Pen _bonePen;
    private readonly Pen _linePen;
    private readonly Pen _fovPen;

    // [UI Resources - ImGui Style]
    private readonly Brush _menuBgBrush;
    private readonly Brush _headerBrush;
    private readonly Pen _borderPen;
    private readonly Brush _textBrush;
    private readonly Brush _onBrush;
    private readonly Brush _offBrush;
    private readonly Brush _boneBrush;

    private readonly Font _titleFont;
    private readonly Font _itemFont;

    public OverlayForm()
    {
        _memory = new MemoryReader();
        _gameReader = new GameDataReader(_memory);
        _w2s = new WorldToScreen(SCREEN_WIDTH, SCREEN_HEIGHT);

        // --- GAME COLORS ---
        _enemyPen = new Pen(Color.Red, 2);
        _enemyPenFar = new Pen(Color.Orange, 2);
        _bonePen = new Pen(Color.Cyan, 1.5f);
        _linePen = new Pen(Color.FromArgb(150, Color.Yellow), 1);
        _fovPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1);

        // --- IMGUI STYLE THEME ---
        _menuBgBrush = new SolidBrush(Color.FromArgb(220, 35, 35, 35));
        _headerBrush = new SolidBrush(Color.FromArgb(255, 58, 0, 90));
        _borderPen = new Pen(Color.Black, 1);

        _textBrush = new SolidBrush(Color.FromArgb(230, 230, 230));
        _onBrush = new SolidBrush(Color.FromArgb(0, 255, 100));
        _offBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
        _boneBrush = new SolidBrush(Color.Cyan);

        _titleFont = new Font("Segoe UI", 10, FontStyle.Bold);
        _itemFont = new Font("Segoe UI", 9, FontStyle.Regular);

        InitializeOverlay();

        _renderTimer = new System.Windows.Forms.Timer { Interval = 1000 / TARGET_FPS };
        _renderTimer.Tick += (s, e) => this.Refresh();

        _attachTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _attachTimer.Tick += AttachTick;
        _attachTimer.Start();

        _logicThread = new Thread(LogicLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _logicThread.Start();

        this.KeyPreview = true;
        this.KeyDown += OnKeyDown;
        TryAttach();
    }

    private void LogicLoop()
    {
        while (_isRunning)
        {
            try
            {
                if (_isAttached && _espEnabled)
                {
                    var info = _gameReader.GetDebugInfo();
                    var camera = _gameReader.GetCameraData();

                    if (camera.HasValue && camera.Value.IsValid)
                    {
                        var characters = _gameReader.GetAllCharacters(camera.Value);
                        lock (_dataLock)
                        {
                            _lastCamera = camera.Value;
                            _lastCharacters = characters;
                            _debugInfo = info;
                        }
                    }
                }
            }
            catch { }

            Thread.Sleep(1);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.InterpolationMode = InterpolationMode.Low;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;

        g.Clear(Color.Black);

        CameraData cam;
        List<CharacterData> chars;
        bool attached;
        bool enabled;

        lock (_dataLock)
        {
            cam = _lastCamera;
            chars = new List<CharacterData>(_lastCharacters);
            attached = _isAttached;
            enabled = _espEnabled;
        }

        DrawInfoPanel(g, cam, _debugInfo, attached, enabled);
        if (!attached || !enabled) return;

        float centerX = SCREEN_WIDTH / 2.0f;
        float centerY = SCREEN_HEIGHT / 2.0f;

        g.DrawEllipse(_fovPen, centerX - AIM_FOV_RADIUS, centerY - AIM_FOV_RADIUS, AIM_FOV_RADIUS * 2, AIM_FOV_RADIUS * 2);

        Vector3? bestTargetPos = null;
        float closestDistToCrosshair = 99999f;

        foreach (var character in chars)
        {
            if (character.IsPlayer) continue;

            // --- 1. FIND WEAKSPOT & DRAW SKELETON ---
            Vector3? weakspotPos3D = null;
            var screenRoot = _w2s.Project(character.Location, cam);

            if (character.Bones != null && character.Bones.Count > 0)
            {
                // [PRIORITY 1] TRUE WEAKSPOT (The "VFX_" Socket Logic)
                // This is the Dynamic Fix. It asks the game "What is the weakspot?" and aims there.
                if (character.WeakspotIndex != -1 && character.Bones.ContainsKey(character.WeakspotIndex))
                {
                    weakspotPos3D = character.Bones[character.WeakspotIndex];
                }

                // [PRIORITY 2] BACKUP CALCULATION (Height Logic)
                // Only runs if the game didn't tell us a weakspot (e.g. Minions or non-weakspot enemies)
                double maxScore = -99999;

                foreach (var kvp in character.Bones)
                {
                    int boneIndex = kvp.Key;
                    var bonePos = kvp.Value;

                    // Draw Skeleton Dots
                    if (_drawSkeleton)
                    {
                        var screenBone = _w2s.Project(bonePos, cam);
                        if (screenBone.HasValue &&
                            screenBone.Value.X > -5000 && screenBone.Value.X < 5000 &&
                            screenBone.Value.Y > -5000 && screenBone.Value.Y < 5000)
                        {
                            // Highlight the True Weakspot in Gold if found
                            Brush dotBrush = (boneIndex == character.WeakspotIndex) ? Brushes.Gold : _boneBrush;
                            g.FillRectangle(dotBrush, screenBone.Value.X - 1, screenBone.Value.Y - 1, 3, 3);

                            // [DEBUG] Uncomment this to see the BONE NAMES on screen!
                            // This helps you verify "b_Head" is actually Bone 6.
                            // g.DrawString(boneIndex.ToString(), _itemFont, Brushes.White, screenBone.Value.X, screenBone.Value.Y);
                        }
                    }

                    // Backup Math (Only runs if Priority 1 failed)
                    if (weakspotPos3D == null)
                    {
                        double h = bonePos.Z;
                        double dist = Math.Sqrt(Math.Pow(bonePos.X - character.Location.X, 2) + Math.Pow(bonePos.Y - character.Location.Y, 2));

                        // Prefer Height (0.2 penalty) so we don't aim at the feet
                        double score = h - (dist * 0.2);

                        if (score > maxScore)
                        {
                            maxScore = score;
                            weakspotPos3D = bonePos;
                        }
                    }
                }
            }

            // --- 2. CALCULATE BOX ---
            var screenWeak = weakspotPos3D.HasValue ? _w2s.Project(weakspotPos3D.Value, cam) : null;

            float height = Math.Clamp(1800.0f / (float)character.Distance, 10.0f, 400.0f);
            float width = height * 0.6f;

            float boxX = 0, boxY = 0;
            bool validBox = false;

            if (screenWeak.HasValue)
            {
                boxX = screenWeak.Value.X - (width / 2);
                boxY = screenWeak.Value.Y - (height * 0.15f);
                validBox = true;
            }
            else if (screenRoot.HasValue)
            {
                boxX = screenRoot.Value.X - (width / 2);
                boxY = screenRoot.Value.Y - height;
                validBox = true;
            }

            if (!validBox) continue;
            if (boxX < -width || boxX > SCREEN_WIDTH || boxY < -height || boxY > SCREEN_HEIGHT) continue;

            var distColor = character.Distance < 30 ? _enemyPen : _enemyPenFar;

            if (_drawBox) g.DrawRectangle(distColor, boxX, boxY, width, height);

            if (_drawLines)
            {
                float lineTargetX = screenWeak.HasValue ? screenWeak.Value.X : (boxX + width / 2);
                float lineTargetY = screenWeak.HasValue ? screenWeak.Value.Y : (boxY + height / 2);
                g.DrawLine(_linePen, SCREEN_WIDTH / 2, SCREEN_HEIGHT, lineTargetX, lineTargetY);
            }

            if (_drawDist)
            {
                string distText = $"{character.Distance:F0}m";
                g.DrawString(distText, _itemFont, _textBrush, boxX, boxY + height + 2);
            }

            if (_drawWeakspot && screenWeak.HasValue)
            {
                float dotR = Math.Clamp(width * 0.15f, 3.0f, 10.0f);
                g.DrawEllipse(_enemyPen, screenWeak.Value.X - dotR, screenWeak.Value.Y - dotR, dotR * 2, dotR * 2);
                g.FillRectangle(Brushes.Red, screenWeak.Value.X - 2, screenWeak.Value.Y - 2, 4, 4);
            }

            // --- 4. AIMBOT SELECTION ---
            if (weakspotPos3D.HasValue && screenWeak.HasValue)
            {
                float distX = Math.Abs(screenWeak.Value.X - centerX);
                float distY = Math.Abs(screenWeak.Value.Y - centerY);
                float distToCrosshair = (float)Math.Sqrt(distX * distX + distY * distY);

                if (distToCrosshair < AIM_FOV_RADIUS && distToCrosshair < closestDistToCrosshair)
                {
                    closestDistToCrosshair = distToCrosshair;
                    bestTargetPos = weakspotPos3D;
                }
            }
        }

        // --- 5. EXECUTE AIMBOT ---
        if (bestTargetPos.HasValue && (GetAsyncKeyState(0x02) & 0x8000) != 0)
        {
            var screenTarget = _w2s.Project(bestTargetPos.Value, cam);
            if (screenTarget.HasValue)
            {
                float deltaX = screenTarget.Value.X - centerX;
                float deltaY = screenTarget.Value.Y - centerY;
                mouse_event(MOUSEEVENTF_MOVE, (int)deltaX, (int)deltaY, 0, 0);
            }
        }

        UpdateFPS();
    }

    private void UpdateFPS()
    {
        _frameCount++;
        if ((DateTime.Now - _lastFpsTime).TotalSeconds >= 1)
        {
            _currentFps = _frameCount;
            _frameCount = 0;
            _lastFpsTime = DateTime.Now;
        }
    }

    private void InitializeOverlay()
    {
        this.Text = "Remnant 2 ESP";
        this.FormBorderStyle = FormBorderStyle.None;
        this.BackColor = Color.Black;
        this.TransparencyKey = Color.Black;
        this.TopMost = true;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.Location = new Point(0, 0);
        this.Size = new Size(SCREEN_WIDTH, SCREEN_HEIGHT);

        this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        this.Load += (s, e) => MakeClickThrough();
    }

    private void MakeClickThrough()
    {
        IntPtr exStylePtr = GetWindowLongPtr(this.Handle, GWL_EXSTYLE);
        long exStyle = exStylePtr.ToInt64();
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
        SetWindowLongPtr(this.Handle, GWL_EXSTYLE, (IntPtr)exStyle);
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(this.Handle, ref margins);
    }

    private void TryAttach()
    {
        if (_memory.Attach("Remnant2-Win64-Shipping"))
        {
            _isAttached = true;
            _gameReader.UpdateBaseAddress();
            _renderTimer.Start();
        }
        else
        {
            _isAttached = false;
            _renderTimer.Stop();
        }
    }

    private void AttachTick(object? sender, EventArgs e)
    {
        if (!_memory.IsAttached) TryAttach();
    }

    private void DrawInfoPanel(Graphics g, CameraData cam, string debug, bool attached, bool enabled)
    {
        int menuX = 20;
        int menuY = 20;
        int width = 220;
        int height = 210;
        int headerHeight = 25;

        g.FillRectangle(_menuBgBrush, menuX, menuY, width, height);
        g.DrawRectangle(_borderPen, menuX, menuY, width, height);

        g.FillRectangle(_headerBrush, menuX, menuY, width, headerHeight);
        g.DrawRectangle(_borderPen, menuX, menuY, width, headerHeight);

        g.DrawString("REMNANT 2 EXTERNAL", _titleFont, _textBrush, menuX + 5, menuY + 4);

        string status = attached ? "ATTACHED" : "WAITING";
        Brush statusColor = attached ? _onBrush : _offBrush;
        g.DrawString(status, _itemFont, statusColor, menuX + width - 70, menuY + 4);

        int itemY = menuY + headerHeight + 10;
        int gap = 20;

        void DrawToggle(string label, string key, bool isActive)
        {
            g.DrawString(key, _itemFont, _offBrush, menuX + 10, itemY);
            g.DrawString(label, _itemFont, _textBrush, menuX + 45, itemY);

            string state = isActive ? "ON" : "OFF";
            Brush stateBrush = isActive ? _onBrush : _offBrush;
            g.DrawString(state, _itemFont, stateBrush, menuX + width - 40, itemY);

            itemY += gap;
        }

        DrawToggle("ESP Master", "[F5]", enabled);
        DrawToggle("Snaplines", "[F6]", _drawLines);
        DrawToggle("Weakspots", "[F7]", _drawWeakspot);
        DrawToggle("2D Box", "[F8]", _drawBox);
        DrawToggle("Distance", "[F9]", _drawDist);
        DrawToggle("Skeleton", "[F10]", _drawSkeleton);

        g.DrawLine(_borderPen, menuX, itemY + 5, menuX + width, itemY + 5);
        itemY += 10;
        g.DrawString($"FPS: {_currentFps}", _itemFont, _offBrush, menuX + 10, itemY);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F5: _espEnabled = !_espEnabled; break;
            case Keys.F6: _drawLines = !_drawLines; break;
            case Keys.F7: _drawWeakspot = !_drawWeakspot; break;
            case Keys.F8: _drawBox = !_drawBox; break;
            case Keys.F9: _drawDist = !_drawDist; break;
            case Keys.F10: _drawSkeleton = !_drawSkeleton; break;
            case Keys.Escape: this.Close(); break;
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _isRunning = false;
        _renderTimer.Stop();
        _attachTimer.Stop();
        try { _logicThread?.Join(200); } catch { }
        _memory.Dispose();
        _enemyPen.Dispose(); _enemyPenFar.Dispose(); _bonePen.Dispose();
        _linePen.Dispose(); _fovPen.Dispose(); _borderPen.Dispose();

        _menuBgBrush.Dispose(); _headerBrush.Dispose(); _textBrush.Dispose();
        _onBrush.Dispose(); _offBrush.Dispose(); _boneBrush.Dispose();
        _titleFont.Dispose(); _itemFont.Dispose();

        base.OnFormClosing(e);
    }
}