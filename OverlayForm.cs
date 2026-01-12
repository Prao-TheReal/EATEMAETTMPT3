using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Numerics;

namespace Remnant2ESP;

public class OverlayForm : Form
{
    // [Imports]
    [DllImport("user32.dll")] static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
    private const int MOUSEEVENTF_MOVE = 0x0001;
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);
    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int Left, Right, Top, Bottom; }
    private const int GWL_EXSTYLE = -20; private const int WS_EX_LAYERED = 0x80000; private const int WS_EX_TRANSPARENT = 0x20; private const int WS_EX_TOPMOST = 0x8;

    // [Settings]
    private const int SCREEN_WIDTH = 5120;
    private const int SCREEN_HEIGHT = 1440;
    private const int TARGET_FPS = 144;
    private const float AIM_FOV_RADIUS = 100.0f;

    // [Core Components]
    private readonly MemoryReader _memory;
    private readonly GameDataReader _gameReader;
    private readonly WorldToScreen _w2s;
    private readonly System.Windows.Forms.Timer _attachTimer;

    private Thread? _logicThread;
    private Thread? _renderThread;
    private bool _isRunning = true;
    private readonly object _dataLock = new object();

    // [State]
    private bool _espEnabled = false;
    private bool _drawLines = false;
    private bool _drawWeakspot = false;
    private bool _drawBox = false;
    private bool _drawDist = false;
    private bool _drawSkeleton = false;
    private bool _drawItems = true;     // New: Item Toggle
    private int _boneDisplayMode = 0;
    private Dictionary<int, bool> _keyToggleState = new Dictionary<int, bool>();
    private bool _isAttached = false;
    private string _debugInfo = "";

    // [Render Caches]
    private List<RenderEntity> _renderList = new();
    private List<RenderItem> _renderItems = new(); // New
    private CameraData _lastCamera;
    private int _frameCount = 0;
    private DateTime _lastFpsTime = DateTime.Now;
    private int _currentFps = 0;

    // [Resources]
    private readonly Pen _enemyPen; private readonly Pen _enemyPenFar; private readonly Pen _bonePen; private readonly Pen _linePen; private readonly Pen _fovPen;
    private readonly Brush _menuBgBrush; private readonly Brush _headerBrush; private readonly Pen _borderPen; private readonly Brush _textBrush; private readonly Brush _onBrush; private readonly Brush _offBrush; private readonly Brush _boneBrush;
    private PrivateFontCollection _pfc = new PrivateFontCollection(); private readonly Font _titleFont; private readonly Font _itemFont;

    // [Render Entities]
    public class RenderEntity
    {
        public Vector3? RootPos; public Vector3? WeakspotPos; public Vector3? HeadPos;
        public float Distance;
        public string LabelText = ""; // Changed from DistText
        public bool IsVisible = false; public bool HasWeakspot = false;
        public Dictionary<int, Vector3> BoneScreenPos = new(); public Dictionary<int, string> BoneNames = new();
        public string LockReason = ""; public Brush LockColor = Brushes.White;
    }

    public class RenderItem
    {
        public Vector3 ScreenPos;
        public string Text;
        public Brush Color;
        public float Distance;
    }

    public OverlayForm()
    {
        _memory = new MemoryReader(); _gameReader = new GameDataReader(_memory); _w2s = new WorldToScreen(SCREEN_WIDTH, SCREEN_HEIGHT);
        this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        _enemyPen = new Pen(Color.Red, 2); _enemyPenFar = new Pen(Color.Orange, 2); _bonePen = new Pen(Color.Cyan, 1.5f); _linePen = new Pen(Color.FromArgb(150, Color.Yellow), 1); _fovPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1);
        _menuBgBrush = new SolidBrush(Color.FromArgb(220, 35, 35, 35)); _headerBrush = new SolidBrush(Color.FromArgb(255, 58, 0, 90)); _borderPen = new Pen(Color.Black, 1); _textBrush = new SolidBrush(Color.FromArgb(230, 230, 230)); _onBrush = new SolidBrush(Color.FromArgb(0, 255, 100)); _offBrush = new SolidBrush(Color.FromArgb(150, 150, 150)); _boneBrush = new SolidBrush(Color.Cyan);
        try { if (File.Exists("font.ttf")) { _pfc.AddFontFile("font.ttf"); _titleFont = new Font(_pfc.Families[0], 12, FontStyle.Bold); _itemFont = new Font(_pfc.Families[0], 10, FontStyle.Regular); } else { _titleFont = new Font("Segoe UI", 10, FontStyle.Bold); _itemFont = new Font("Segoe UI", 9, FontStyle.Regular); } } catch { _titleFont = new Font("Segoe UI", 10, FontStyle.Bold); _itemFont = new Font("Segoe UI", 9, FontStyle.Regular); }
        InitializeOverlay();
        _logicThread = new Thread(LogicLoop) { IsBackground = true, Priority = ThreadPriority.Normal }; _logicThread.Start();
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal }; _renderThread.Start();
        _attachTimer = new System.Windows.Forms.Timer { Interval = 1000 }; _attachTimer.Tick += AttachTick; _attachTimer.Start();
        TryAttach();
    }

    private void RenderLoop()
    {
        Stopwatch frameTimer = new Stopwatch(); frameTimer.Start(); long targetTicks = 10000000 / TARGET_FPS;
        while (_isRunning)
        {
            long start = frameTimer.ElapsedTicks;
            try { if (this.IsHandleCreated && !this.IsDisposed) { this.BeginInvoke((MethodInvoker)delegate { if (this.IsHandleCreated && !this.IsDisposed) this.Refresh(); }); } } catch { }
            while (frameTimer.ElapsedTicks - start < targetTicks) { Thread.Sleep(1); }
        }
    }

    private void LogicLoop()
    {
        while (_isRunning)
        {
            CheckInputs();
            if (_isAttached && _espEnabled)
            {
                try
                {
                    var info = _gameReader.GetDebugInfo();
                    var camera = _gameReader.GetCameraData();

                    if (camera.HasValue && camera.Value.IsValid)
                    {
                        // 1. Process Enemies
                        var rawCharacters = _gameReader.GetAllCharacters(camera.Value);
                        var newRenderList = new List<RenderEntity>();
                        foreach (var character in rawCharacters)
                        {
                            if (character.IsPlayer) continue; if (character.Distance > 70) continue;

                            var renderEnt = new RenderEntity();
                            renderEnt.Distance = (float)character.Distance;
                            // New Name Format: (69m) Root Flyer
                            renderEnt.LabelText = $"({character.Distance:F0}m) {character.DisplayName}";

                            var screenRoot = _w2s.Project(character.Location, camera.Value);
                            if (screenRoot.HasValue) renderEnt.RootPos = new Vector3(screenRoot.Value.X, screenRoot.Value.Y, 0);

                            if (character.Bones != null && character.Bones.Count > 0)
                            {
                                // ... (Bone Processing kept same as before)
                                Vector3? weakspot3D = null;
                                if (character.WeakspotIndex != -1 && character.Bones.ContainsKey(character.WeakspotIndex)) { weakspot3D = character.Bones[character.WeakspotIndex]; renderEnt.LockReason = "CRITICAL"; renderEnt.LockColor = Brushes.Red; }
                                double maxZ_T1 = -99999; Vector3? pos_T1 = null; double maxZ_T2 = -99999; Vector3? pos_T2 = null; double maxZ_T3 = -99999; Vector3? pos_T3 = null; double maxZ_Fallback = -99999; Vector3? pos_Fallback = null;
                                IntPtr mesh = _memory.ReadPointer(character.Address + Offsets.Mesh);
                                foreach (var kvp in character.Bones)
                                {
                                    int bIndex = kvp.Key; var bPos = kvp.Value;
                                    if (_drawSkeleton) { var sPos = _w2s.Project(bPos, camera.Value); if (sPos.HasValue) { renderEnt.BoneScreenPos[bIndex] = new Vector3(sPos.Value.X, sPos.Value.Y, 0); if (_boneDisplayMode == 2) renderEnt.BoneNames[bIndex] = _gameReader.GetBoneName(mesh, bIndex); } }
                                    string bName = _gameReader.GetBoneName(mesh, bIndex); string lowerName = bName.ToLower();
                                    if (lowerName.Contains("weakpoint")) { if (bPos.Z > maxZ_T1) { maxZ_T1 = bPos.Z; pos_T1 = bPos; } } else if (lowerName.Contains("coll") || lowerName.Contains("eye") || lowerName.Contains("neck") || lowerName.Contains("pinky2") || lowerName.Contains("mouth")) { if (bPos.Z > maxZ_T2) { maxZ_T2 = bPos.Z; pos_T2 = bPos; } } else if (lowerName.Contains("chest") || lowerName.Contains("spine") || lowerName.Contains("upper") || lowerName.Contains("ear")) { if (bPos.Z > maxZ_T3) { maxZ_T3 = bPos.Z; pos_T3 = bPos; } }
                                    if (bPos.Z > maxZ_Fallback) { maxZ_Fallback = bPos.Z; pos_Fallback = bPos; }
                                }
                                if (weakspot3D == null) { if (pos_T1.HasValue) { weakspot3D = pos_T1; renderEnt.LockReason = "WEAKPOINT"; renderEnt.LockColor = Brushes.LimeGreen; } else if (pos_T2.HasValue) { weakspot3D = pos_T2; renderEnt.LockReason = "HEAD"; renderEnt.LockColor = Brushes.Cyan; } else if (pos_T3.HasValue) { weakspot3D = pos_T3; renderEnt.LockReason = "BODY"; renderEnt.LockColor = Brushes.Orange; } else if (pos_Fallback.HasValue) { weakspot3D = pos_Fallback; renderEnt.LockReason = "HEIGHT"; renderEnt.LockColor = Brushes.Gray; } }
                                renderEnt.WeakspotPos = weakspot3D; if (weakspot3D.HasValue) { renderEnt.HasWeakspot = true; var w2sWeak = _w2s.Project(weakspot3D.Value, camera.Value); if (w2sWeak.HasValue) renderEnt.HeadPos = new Vector3(w2sWeak.Value.X, w2sWeak.Value.Y, 0); }
                            }
                            newRenderList.Add(renderEnt);
                        }

                        // 2. Process Items (New)
                        var newRenderItems = new List<RenderItem>();
                        if (_drawItems)
                        {
                            var rawItems = _gameReader.GetItems(camera.Value);
                            foreach (var item in rawItems)
                            {
                                var screenPos = _w2s.Project(item.Location, camera.Value);
                                if (screenPos.HasValue)
                                {
                                    newRenderItems.Add(new RenderItem
                                    {
                                        ScreenPos = new Vector3(screenPos.Value.X, screenPos.Value.Y, 0),
                                        Text = $"[{item.DisplayName}] {item.Distance:F0}m",
                                        Color = new SolidBrush(item.RarityColor),
                                        Distance = (float)item.Distance
                                    });
                                }
                            }
                        }

                        lock (_dataLock) { _lastCamera = camera.Value; _renderList = newRenderList; _renderItems = newRenderItems; _debugInfo = info; }
                    }
                }
                catch { }
            }
            Thread.Sleep(5);
        }
    }

    private void CheckInputs()
    {
        void CheckToggle(int key, Action action) { bool isDown = (GetAsyncKeyState(key) & 0x8000) != 0; if (!_keyToggleState.ContainsKey(key)) _keyToggleState[key] = false; if (isDown && !_keyToggleState[key]) { _keyToggleState[key] = true; action.Invoke(); } else if (!isDown) _keyToggleState[key] = false; }
        CheckToggle((int)Keys.F5, () => _espEnabled = !_espEnabled);
        CheckToggle((int)Keys.F6, () => _drawLines = !_drawLines);
        CheckToggle((int)Keys.F7, () => _drawWeakspot = !_drawWeakspot);
        CheckToggle((int)Keys.F8, () => _drawBox = !_drawBox);
        CheckToggle((int)Keys.F9, () => _drawDist = !_drawDist);
        CheckToggle((int)Keys.F10, () => _drawSkeleton = !_drawSkeleton);
        CheckToggle((int)Keys.F11, () => { _boneDisplayMode++; if (_boneDisplayMode > 2) _boneDisplayMode = 0; });
        CheckToggle((int)Keys.Insert, () => _drawItems = !_drawItems); // New: Toggle Items with INSERT
        CheckToggle((int)Keys.End, () => { _isRunning = false; Application.Exit(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_isRunning) return;
        try
        {
            var g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceOver; g.CompositingQuality = CompositingQuality.HighSpeed; g.InterpolationMode = InterpolationMode.Low; g.SmoothingMode = SmoothingMode.None; g.PixelOffsetMode = PixelOffsetMode.HighSpeed; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Black);
            List<RenderEntity> entities; List<RenderItem> items; CameraData cam; bool attached; bool enabled;
            lock (_dataLock) { entities = new List<RenderEntity>(_renderList); items = new List<RenderItem>(_renderItems); cam = _lastCamera; attached = _isAttached; enabled = _espEnabled; }
            DrawInfoPanel(g, cam, _debugInfo, attached, enabled);
            if (!attached || !enabled) return;
            float centerX = SCREEN_WIDTH / 2.0f; float centerY = SCREEN_HEIGHT / 2.0f;
            g.DrawEllipse(_fovPen, centerX - AIM_FOV_RADIUS, centerY - AIM_FOV_RADIUS, AIM_FOV_RADIUS * 2, AIM_FOV_RADIUS * 2);
            Vector3? bestTargetPos = null; float closestDistToCrosshair = 99999f;

            // Draw Enemies
            foreach (var ent in entities)
            {
                if (_drawSkeleton && ent.BoneScreenPos.Count > 0) { foreach (var kvp in ent.BoneScreenPos) { var pos = kvp.Value; if (pos.X > -100 && pos.X < SCREEN_WIDTH + 100 && pos.Y > -100 && pos.Y < SCREEN_HEIGHT + 100) { g.FillRectangle(_boneBrush, (float)pos.X - 1, (float)pos.Y - 1, 3, 3); if (_boneDisplayMode == 1) g.DrawString(kvp.Key.ToString(), _itemFont, Brushes.White, (float)pos.X, (float)pos.Y - 10); else if (_boneDisplayMode == 2 && ent.BoneNames.ContainsKey(kvp.Key)) g.DrawString(ent.BoneNames[kvp.Key], _itemFont, Brushes.LightGreen, (float)pos.X, (float)pos.Y - 10); } } }
                if (_drawWeakspot && ent.HeadPos.HasValue) { var hPos = ent.HeadPos.Value; g.DrawEllipse(_enemyPen, (float)hPos.X - 4, (float)hPos.Y - 4, 8, 8); g.FillRectangle(Brushes.Red, (float)hPos.X - 2, (float)hPos.Y - 2, 4, 4); g.DrawString(ent.LockReason, _itemFont, ent.LockColor, (float)hPos.X + 6, (float)hPos.Y - 6); }
                float height = Math.Clamp(1800.0f / ent.Distance, 10.0f, 400.0f); float width = height * 0.6f; float boxX = 0, boxY = 0; bool validBox = false;
                if (ent.HeadPos.HasValue) { boxX = (float)ent.HeadPos.Value.X - (width / 2); boxY = (float)ent.HeadPos.Value.Y - (height * 0.15f); validBox = true; } else if (ent.RootPos.HasValue) { boxX = (float)ent.RootPos.Value.X - (width / 2); boxY = (float)ent.RootPos.Value.Y - height; validBox = true; }
                if (!validBox) continue; if (boxX < -width || boxX > SCREEN_WIDTH || boxY < -height || boxY > SCREEN_HEIGHT) continue;
                var distColor = ent.Distance < 30 ? _enemyPen : _enemyPenFar;
                if (_drawBox) g.DrawRectangle(distColor, boxX, boxY, width, height);
                if (_drawLines) { float targetX = ent.HeadPos.HasValue ? (float)ent.HeadPos.Value.X : (boxX + width / 2); float targetY = ent.HeadPos.HasValue ? (float)ent.HeadPos.Value.Y : (boxY + height / 2); g.DrawLine(_linePen, centerX, SCREEN_HEIGHT, targetX, targetY); }
                if (_drawDist) { float estimatedWidth = ent.LabelText.Length * 6.0f; float textX = boxX + (width - estimatedWidth) / 2; float textY = boxY - 15; g.DrawString(ent.LabelText, _itemFont, Brushes.Salmon, textX, textY); } // Updated to use LabelText
                if (ent.HasWeakspot && ent.HeadPos.HasValue && ent.WeakspotPos.HasValue) { float dx = (float)ent.HeadPos.Value.X - centerX; float dy = (float)ent.HeadPos.Value.Y - centerY; float dist = (float)Math.Sqrt(dx * dx + dy * dy); if (dist < AIM_FOV_RADIUS && dist < closestDistToCrosshair) { closestDistToCrosshair = dist; bestTargetPos = ent.WeakspotPos; } }
            }

            // Draw Items
            if (_drawItems)
            {
                foreach (var item in items)
                {
                    if (item.ScreenPos.X > 0 && item.ScreenPos.X < SCREEN_WIDTH && item.ScreenPos.Y > 0 && item.ScreenPos.Y < SCREEN_HEIGHT)
                    {
                        g.DrawString(item.Text, _itemFont, item.Color, (float)item.ScreenPos.X, (float)item.ScreenPos.Y);
                    }
                }
            }

            if (bestTargetPos.HasValue && (GetAsyncKeyState(0x02) & 0x8000) != 0) { var screenTarget = _w2s.Project(bestTargetPos.Value, cam); if (screenTarget.HasValue) { float deltaX = (float)screenTarget.Value.X - centerX; float deltaY = (float)screenTarget.Value.Y - centerY; mouse_event(MOUSEEVENTF_MOVE, (int)deltaX, (int)deltaY, 0, 0); } }
            UpdateFPS();
        }
        catch { }
    }

    private void UpdateFPS() { _frameCount++; if ((DateTime.Now - _lastFpsTime).TotalSeconds >= 1) { _currentFps = _frameCount; _frameCount = 0; _lastFpsTime = DateTime.Now; } }
    private void InitializeOverlay() { this.Text = "Remnant 2 ESP"; this.FormBorderStyle = FormBorderStyle.None; this.BackColor = Color.Black; this.TransparencyKey = Color.Black; this.TopMost = true; this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.Manual; this.Location = new Point(0, 0); this.Size = new Size(SCREEN_WIDTH, SCREEN_HEIGHT); this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); this.Load += (s, e) => MakeClickThrough(); }
    private void MakeClickThrough() { IntPtr exStylePtr = GetWindowLongPtr(this.Handle, GWL_EXSTYLE); long exStyle = exStylePtr.ToInt64(); exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT; SetWindowLongPtr(this.Handle, GWL_EXSTYLE, (IntPtr)exStyle); var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 }; DwmExtendFrameIntoClientArea(this.Handle, ref margins); }
    private void TryAttach() { if (_memory.Attach("Remnant2-Win64-Shipping")) { _isAttached = true; _gameReader.UpdateBaseAddress(); } else { _isAttached = false; } }
    private void AttachTick(object? sender, EventArgs e) { if (!_memory.IsAttached) TryAttach(); }
    private void DrawInfoPanel(Graphics g, CameraData cam, string debug, bool attached, bool enabled)
    {
        int menuX = 20; int menuY = 20; int width = 220; int height = 260; int headerHeight = 25; // Increased height
        g.FillRectangle(_menuBgBrush, menuX, menuY, width, height); g.DrawRectangle(_borderPen, menuX, menuY, width, height);
        g.FillRectangle(_headerBrush, menuX, menuY, width, headerHeight); g.DrawRectangle(_borderPen, menuX, menuY, width, headerHeight);
        g.DrawString("REMNANT 2 EXTERNAL", _titleFont, _textBrush, menuX + 5, menuY + 4);
        string status = attached ? "ATTACHED" : "WAITING"; Brush statusColor = attached ? _onBrush : _offBrush; g.DrawString(status, _itemFont, statusColor, menuX + width - 70, menuY + 4);
        int itemY = menuY + headerHeight + 10; int gap = 20;
        void DrawToggle(string label, string key, bool isActive) { g.DrawString(key, _itemFont, _offBrush, menuX + 10, itemY); g.DrawString(label, _itemFont, _textBrush, menuX + 45, itemY); string state = isActive ? "ON" : "OFF"; Brush stateBrush = isActive ? _onBrush : _offBrush; g.DrawString(state, _itemFont, stateBrush, menuX + width - 40, itemY); itemY += gap; }
        DrawToggle("ESP Master", "[F5]", enabled); DrawToggle("Snaplines", "[F6]", _drawLines); DrawToggle("Weakspots", "[F7]", _drawWeakspot); DrawToggle("2D Box", "[F8]", _drawBox); DrawToggle("Distance", "[F9]", _drawDist); DrawToggle("Skeleton", "[F10]", _drawSkeleton);
        DrawToggle("Item ESP", "[INS]", _drawItems); // New
        string debugModeStr = _boneDisplayMode == 0 ? "OFF" : (_boneDisplayMode == 1 ? "NUM" : "NAME"); g.DrawString("[F11]", _itemFont, _offBrush, menuX + 10, itemY); g.DrawString("Debug Bones", _itemFont, _textBrush, menuX + 45, itemY); g.DrawString(debugModeStr, _itemFont, _boneDisplayMode > 0 ? _onBrush : _offBrush, menuX + width - 40, itemY); itemY += gap;
        g.DrawString("[F12] Dump Entities (Output)", _itemFont, _offBrush, menuX + 10, itemY);
        g.DrawLine(_borderPen, menuX, itemY + 25, menuX + width, itemY + 25); itemY += 30;
        g.DrawString($"FPS: {_currentFps}", _itemFont, _offBrush, menuX + 10, itemY);
    }
}