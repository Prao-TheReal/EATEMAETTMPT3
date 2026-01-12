using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Drawing.Drawing2D;

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
    private const int SCREEN_WIDTH = 5120; // Check your resolution
    private const int SCREEN_HEIGHT = 1440;
    private const int TARGET_FPS = 144;

    // AIMBOT SETTINGS
    private const float AIM_FOV_RADIUS = 100.0f; // Tighter circle for precision

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

    private bool _isAttached = false;
    private CameraData _lastCamera;
    private List<CharacterData> _lastCharacters = new();
    private string _debugInfo = "";

    private int _frameCount = 0;
    private DateTime _lastFpsTime = DateTime.Now;
    private int _currentFps = 0;

    // [Resources]
    private readonly Pen _enemyPen;
    private readonly Pen _enemyPenFar;
    private readonly Pen _bonePen;
    private readonly Pen _linePen;
    private readonly Pen _fovPen;
    private readonly Brush _textBrush;
    private readonly Brush _bgBrush;
    private readonly Brush _whiteBrush;
    private readonly Font _font;
    private readonly Font _smallFont;

    public OverlayForm()
    {
        _memory = new MemoryReader();
        _gameReader = new GameDataReader(_memory);
        _w2s = new WorldToScreen(SCREEN_WIDTH, SCREEN_HEIGHT);

        _enemyPen = new Pen(Color.Red, 2);
        _enemyPenFar = new Pen(Color.Orange, 2);
        _bonePen = new Pen(Color.Cyan, 1.5f);
        _linePen = new Pen(Color.FromArgb(150, Color.Yellow), 1);
        _fovPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1); // Faint white circle

        _textBrush = new SolidBrush(Color.Yellow);
        _whiteBrush = new SolidBrush(Color.White);
        _bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        _font = new Font("Consolas", 11, FontStyle.Bold);
        _smallFont = new Font("Consolas", 9);

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

        // --- DEFINE CENTER ONCE ---
        float centerX = SCREEN_WIDTH / 2.0f;
        float centerY = SCREEN_HEIGHT / 2.0f;

        // Draw FOV Circle (Visual Guide)
        g.DrawEllipse(_fovPen, centerX - AIM_FOV_RADIUS, centerY - AIM_FOV_RADIUS, AIM_FOV_RADIUS * 2, AIM_FOV_RADIUS * 2);

        // --- BEST TARGET VARIABLES ---
        Vector3? bestTargetPos = null;
        float closestDistToCrosshair = 99999f; // Start huge

        foreach (var character in chars)
        {
            if (character.IsPlayer) continue;

            // --- 1. FIND WEAKSPOT (Anti-Hand Logic) ---
            Vector3? weakspotPos3D = null;
            if (character.Bones != null && character.Bones.Count > 0)
            {
                double maxScore = -99999;
                foreach (var kvp in character.Bones)
                {
                    var b = kvp.Value;

                    // Height (Higher is better)
                    double h = b.Z;

                    // Horizontal Distance from Character Center (Lower is better)
                    double dist = Math.Sqrt(Math.Pow(b.X - character.Location.X, 2) + Math.Pow(b.Y - character.Location.Y, 2));

                    // [POLISH] "Anti-Hand" Scoring
                    // We multiply distance by 5.0 (was 2.0). 
                    // This heavily penalizes bones that stick out to the side (hands/arms), 
                    // ensuring we pick the Head or Spine which are aligned with Center.
                    double score = h - (dist * 5.0);

                    if (score > maxScore)
                    {
                        maxScore = score;
                        weakspotPos3D = b;
                    }
                }
            }

            // --- 2. CALCULATE BOX ---
            var screenRoot = _w2s.Project(character.Location, cam);
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

            // --- 3. DRAW ELEMENTS ---
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
                g.DrawString(distText, _smallFont, _textBrush, boxX, boxY + height + 2);
            }

            if (_drawWeakspot && screenWeak.HasValue)
            {
                float dotR = Math.Clamp(width * 0.15f, 3.0f, 10.0f);
                g.DrawEllipse(_enemyPen, screenWeak.Value.X - dotR, screenWeak.Value.Y - dotR, dotR * 2, dotR * 2);
                g.FillRectangle(Brushes.Red, screenWeak.Value.X - 2, screenWeak.Value.Y - 2, 4, 4);
            }

            // --- 4. AIMBOT CANDIDATE SELECTION (FOV Logic) ---
            if (weakspotPos3D.HasValue && screenWeak.HasValue)
            {
                // Calculate distance from CROSSHAIR (Not Player Body)
                float distX = Math.Abs(screenWeak.Value.X - centerX);
                float distY = Math.Abs(screenWeak.Value.Y - centerY);
                float distToCrosshair = (float)Math.Sqrt(distX * distX + distY * distY);

                // Logic:
                // 1. Must be inside the White FOV Circle
                // 2. Must be closer to the crosshair than the previous best target
                if (distToCrosshair < AIM_FOV_RADIUS && distToCrosshair < closestDistToCrosshair)
                {
                    closestDistToCrosshair = distToCrosshair;
                    bestTargetPos = weakspotPos3D;
                }
            }
        }

        // --- 5. EXECUTE AIMBOT ---
        // If we found a target inside our FOV...
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
        g.FillRectangle(_bgBrush, 10, 10, 320, 160);
        string status = attached ? (enabled ? "ESP: ON" : "ESP: OFF") : "Waiting...";
        var statusColor = attached ? (enabled ? Color.Lime : Color.Orange) : Color.Red;
        using var statusBrush = new SolidBrush(statusColor);

        int y = 15;
        g.DrawString($"Remnant 2 ESP | {status}", _font, statusBrush, 15, y); y += 22;
        g.DrawString($"FPS: {_currentFps}", _smallFont, _whiteBrush, 15, y); y += 16;
        g.DrawString($"[F5] Master Toggle", _smallFont, _whiteBrush, 15, y); y += 16;
        g.DrawString($"[F6] Lines: {(_drawLines ? "ON" : "OFF")}", _smallFont, _whiteBrush, 15, y); y += 16;
        g.DrawString($"[F7] Weakspot: {(_drawWeakspot ? "ON" : "OFF")}", _smallFont, _whiteBrush, 15, y); y += 16;
        g.DrawString($"[F8] Box: {(_drawBox ? "ON" : "OFF")}", _smallFont, _whiteBrush, 15, y); y += 16;
        g.DrawString($"[F9] Dist: {(_drawDist ? "ON" : "OFF")}", _smallFont, _whiteBrush, 15, y);
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
        _linePen.Dispose(); _textBrush.Dispose(); _whiteBrush.Dispose();
        _bgBrush.Dispose(); _font.Dispose(); _smallFont.Dispose();
        base.OnFormClosing(e);
    }
}