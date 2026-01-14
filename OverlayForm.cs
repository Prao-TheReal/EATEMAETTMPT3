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
    private const float AIM_FOV_RADIUS = 351.0f;

    // --- [CUSTOM OVERRIDES] ---
    private readonly Dictionary<string, string> _customBoneOverrides = new()
    {
        { "Root_Flyer", "weakpoint_01_L" },
        { "Char_RootZombie_C", "Bone_SZ_Hand_L" },
        { "Char_Nemesis", "Bone_RN_Coll"}
    };

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
    private bool _espEnabled = true;
    private bool _drawLines = false;
    private bool _drawWeakspot = false;
    private bool _drawBox = false;
    private bool _drawDist = false;
    private bool _drawSkeleton = false;
    private bool _drawItems = true;
    private int _boneDisplayMode = 0;
    private Dictionary<int, bool> _keyToggleState = new Dictionary<int, bool>();
    private bool _isAttached = false;
    private string _debugInfo = "";

    // [Render Caches]
    private List<RenderEntity> _renderList = new();
    private List<RenderItem> _renderItems = new();
    private int _frameCount = 0;
    private DateTime _lastFpsTime = DateTime.Now;
    private int _currentFps = 0;

    // [Resources]
    private readonly Pen _enemyPen; private readonly Pen _enemyPenFar; private readonly Pen _bonePen; private readonly Pen _linePen; private readonly Pen _fovPen;
    private readonly Brush _menuBgBrush; private readonly Brush _headerBrush; private readonly Pen _borderPen; private readonly Brush _textBrush; private readonly Brush _onBrush; private readonly Brush _offBrush; private readonly Brush _boneBrush;
    private PrivateFontCollection _pfc = new PrivateFontCollection(); private readonly Font _titleFont; private readonly Font _itemFont;

    // [Render Entities - NOW STORING WORLD POSITIONS]
    public class RenderEntity
    {
        // We store WORLD coordinates here, not Screen coordinates.
        public Vector3 WorldRoot;
        public Vector3? WorldWeakspot;
        public Vector3? WorldHead;

        public float Distance; // Cached distance for sorting
        public string LabelText = "";
        public bool IsVisible = false;
        public bool HasWeakspot = false;

        // Store Bones as WORLD positions
        public Dictionary<int, Vector3> BoneWorldPos = new();
        public Dictionary<int, string> BoneNames = new();
        public string LockReason = "";
        public Brush LockColor = Brushes.White;
    }

    public class RenderItem
    {
        public Vector3 WorldPos; // Store World Pos
        public string Text;
        public Brush Color;
        public float Distance;
    }

    public OverlayForm()
    {
        _memory = new MemoryReader();
        _gameReader = new GameDataReader(_memory);
        _w2s = new WorldToScreen(SCREEN_WIDTH, SCREEN_HEIGHT);

        _gameReader.StartItemScanner();

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
                    // We grab camera here for Distance calc, but we will grab it AGAIN in OnPaint for drawing
                    var camera = _gameReader.GetCameraData();

                    if (camera.HasValue && camera.Value.IsValid)
                    {
                        var rawCharacters = _gameReader.GetAllCharacters(camera.Value);
                        var newRenderList = new List<RenderEntity>();

                        foreach (var character in rawCharacters)
                        {
                            if (character.IsPlayer) continue;
                            if (character.Distance > 150) continue; // Optimization: Don't process far entities

                            var renderEnt = new RenderEntity();
                            renderEnt.Distance = (float)character.Distance;
                            renderEnt.LabelText = $"[{character.Name}] {character.Distance:F0}m";

                            // [CHANGE] Store WORLD Position, do not Project yet!
                            renderEnt.WorldRoot = character.Location;

                            if (character.Bones != null && character.Bones.Count > 0)
                            {
                                IntPtr enemyMesh = _memory.ReadPointer(character.Address + Offsets.Mesh);
                                bool isVis = _gameReader.IsVisible(enemyMesh);
                                renderEnt.IsVisible = isVis;
                                renderEnt.LockColor = isVis ? Brushes.LimeGreen : Brushes.Red;
                                if (isVis) renderEnt.LockReason = "[VIS]";

                                double minZ = 999999;
                                double maxZ = -999999;
                                foreach (var bonePos in character.Bones.Values)
                                {
                                    if (bonePos.Z < minZ) minZ = bonePos.Z;
                                    if (bonePos.Z > maxZ) maxZ = bonePos.Z;
                                }
                                double waistHeight = minZ + ((maxZ - minZ) * 0.5);
                                Vector3? weakspot3D = null;

                                // Custom Overrides
                                foreach (var overrideEntry in _customBoneOverrides)
                                {
                                    if (character.Name.Contains(overrideEntry.Key, StringComparison.OrdinalIgnoreCase))
                                    {
                                        int targetIndex = _gameReader.GetBoneIndexByName(enemyMesh, overrideEntry.Value);
                                        if (targetIndex != -1 && character.Bones.ContainsKey(targetIndex))
                                        {
                                            weakspot3D = character.Bones[targetIndex];
                                            renderEnt.LockReason = "OVERRIDE";
                                            renderEnt.LockColor = Brushes.Cyan;
                                            break;
                                        }
                                    }
                                }

                                // Priority A
                                if (weakspot3D == null && character.WeakspotIndex != -1 && character.Bones.ContainsKey(character.WeakspotIndex))
                                {
                                    weakspot3D = character.Bones[character.WeakspotIndex];
                                    renderEnt.LockReason = isVis ? "CRITICAL" : "CRITICAL (HID)";
                                }

                                // Priority B
                                if (weakspot3D == null)
                                {
                                    foreach (var kvp in character.Bones)
                                    {
                                        string bName = _gameReader.GetBoneName(enemyMesh, kvp.Key).ToLower();
                                        if (bName.Contains("weakpoint"))
                                        {
                                            if (kvp.Value.Z > waistHeight)
                                            {
                                                weakspot3D = kvp.Value;
                                                renderEnt.LockReason = "WEAKPOINT";
                                                renderEnt.LockColor = Brushes.Magenta;
                                                break;
                                            }
                                        }
                                    }
                                }

                                // Priority C
                                if (weakspot3D == null)
                                {
                                    Vector3? bestNameMatch = null;
                                    double bestNameZ = -99999;

                                    foreach (var kvp in character.Bones)
                                    {
                                        int bIndex = kvp.Key;
                                        var bPos = kvp.Value;

                                        // [CHANGE] Store Bone World Pos for later projection
                                        if (_drawSkeleton)
                                        {
                                            renderEnt.BoneWorldPos[bIndex] = bPos;
                                            if (_boneDisplayMode == 2) renderEnt.BoneNames[bIndex] = _gameReader.GetBoneName(enemyMesh, bIndex);
                                        }

                                        string bName = _gameReader.GetBoneName(enemyMesh, bIndex).ToLower();
                                        bool isHead = bName.Contains("head") || bName.Contains("eye") || bName.Contains("mouth") || bName.Contains("face");
                                        bool isNeck = bName.Contains("neck");

                                        if (isHead || isNeck)
                                        {
                                            if (bPos.Z > waistHeight)
                                            {
                                                if (bPos.Z > bestNameZ)
                                                {
                                                    bestNameZ = bPos.Z;
                                                    bestNameMatch = bPos;
                                                }
                                            }
                                        }
                                    }

                                    if (bestNameMatch.HasValue)
                                    {
                                        weakspot3D = bestNameMatch;
                                        renderEnt.LockReason = isVis ? "HEAD" : "HEAD (HID)";
                                    }
                                }

                                // Priority D
                                if (weakspot3D == null)
                                {
                                    foreach (var bPos in character.Bones.Values)
                                    {
                                        if (Math.Abs(bPos.Z - maxZ) < 5.0f)
                                        {
                                            weakspot3D = bPos;
                                            renderEnt.LockReason = isVis ? "HEIGHT" : "HEIGHT (HID)";
                                            break;
                                        }
                                    }
                                }

                                if (weakspot3D.HasValue && (weakspot3D.Value.X == 0 && weakspot3D.Value.Y == 0))
                                    weakspot3D = null;

                                renderEnt.WorldWeakspot = weakspot3D;
                                if (weakspot3D.HasValue)
                                {
                                    renderEnt.HasWeakspot = true;
                                    // We only store the WORLD position now. Projection happens in OnPaint.
                                    renderEnt.WorldHead = weakspot3D.Value;
                                }
                            }
                            newRenderList.Add(renderEnt);
                        }

                        var newRenderItems = new List<RenderItem>();
                        if (_drawItems)
                        {
                            var rawItems = _gameReader.GetItems(camera.Value);
                            foreach (var item in rawItems)
                            {
                                newRenderItems.Add(new RenderItem
                                {
                                    WorldPos = item.Location, // Store World Pos
                                    Text = $"[{item.DisplayName}] {item.Distance:F0}m",
                                    Color = new SolidBrush(item.RarityColor),
                                    Distance = (float)item.Distance
                                });
                            }
                        }

                        lock (_dataLock) { _renderList = newRenderList; _renderItems = newRenderItems; _debugInfo = info; }
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
        CheckToggle((int)Keys.F12, () => { _gameReader.DebugMode = !_gameReader.DebugMode; Console.Beep(); });
        CheckToggle((int)Keys.Insert, () => _drawItems = !_drawItems);
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

            // [KEY CHANGE] Get FRESH Camera Data right now for drawing
            // This decouples the camera rotation from the entity search lag
            CameraData? cam = _gameReader.GetCameraData();
            if (!cam.HasValue) return; // Can't draw without camera

            List<RenderEntity> entities; List<RenderItem> items; bool attached; bool enabled;
            lock (_dataLock) { entities = new List<RenderEntity>(_renderList); items = new List<RenderItem>(_renderItems); attached = _isAttached; enabled = _espEnabled; }

            DrawInfoPanel(g, cam.Value, _debugInfo, attached, enabled);
            if (!attached || !enabled) return;

            float centerX = SCREEN_WIDTH / 2.0f; float centerY = SCREEN_HEIGHT / 2.0f;
            g.DrawEllipse(_fovPen, centerX - AIM_FOV_RADIUS, centerY - AIM_FOV_RADIUS, AIM_FOV_RADIUS * 2, AIM_FOV_RADIUS * 2);

            Vector3? bestTargetPos = null;
            float closestWorldDist = 99999f;

            foreach (var ent in entities)
            {
                // [PROJECT ENTITY ROOT]
                var screenRoot = _w2s.Project(ent.WorldRoot, cam.Value);
                if (!screenRoot.HasValue) continue;
                Vector3 rootScreen = new Vector3(screenRoot.Value.X, screenRoot.Value.Y, 0);

                // [PROJECT HEAD/WEAKSPOT]
                Vector3? headScreen = null;
                if (ent.WorldHead.HasValue)
                {
                    var w2sHead = _w2s.Project(ent.WorldHead.Value, cam.Value);
                    if (w2sHead.HasValue) headScreen = new Vector3(w2sHead.Value.X, w2sHead.Value.Y, 0);
                }

                // [DRAW SKELETON]
                if (_drawSkeleton && ent.BoneWorldPos.Count > 0)
                {
                    foreach (var kvp in ent.BoneWorldPos)
                    {
                        var boneWorld = kvp.Value;
                        var boneScreen = _w2s.Project(boneWorld, cam.Value);

                        if (boneScreen.HasValue)
                        {
                            var pos = boneScreen.Value;
                            if (pos.X > -100 && pos.X < SCREEN_WIDTH + 100 && pos.Y > -100 && pos.Y < SCREEN_HEIGHT + 100)
                            {
                                g.FillRectangle(_boneBrush, (float)pos.X - 1, (float)pos.Y - 1, 3, 3);
                                if (_boneDisplayMode == 1) g.DrawString(kvp.Key.ToString(), _itemFont, Brushes.White, (float)pos.X, (float)pos.Y - 10);
                                else if (_boneDisplayMode == 2 && ent.BoneNames.ContainsKey(kvp.Key)) g.DrawString(ent.BoneNames[kvp.Key], _itemFont, Brushes.LightGreen, (float)pos.X, (float)pos.Y - 10);
                            }
                        }
                    }
                }

                // [DRAW WEAKSPOT]
                if (_drawWeakspot && headScreen.HasValue)
                {
                    var hPos = headScreen.Value;
                    g.DrawEllipse(_enemyPen, (float)hPos.X - 4, (float)hPos.Y - 4, 8, 8);
                    g.FillRectangle(Brushes.Red, (float)hPos.X - 2, (float)hPos.Y - 2, 4, 4);
                    g.DrawString(ent.LockReason, _itemFont, ent.LockColor, (float)hPos.X + 6, (float)hPos.Y - 6);
                }

                // [DRAW BOX]
                float height = Math.Clamp(1800.0f / ent.Distance, 10.0f, 400.0f); float width = height * 0.6f; float boxX = 0, boxY = 0; bool validBox = false;
                if (headScreen.HasValue) { boxX = (float)headScreen.Value.X - (width / 2); boxY = (float)headScreen.Value.Y - (height * 0.15f); validBox = true; }
                else { boxX = (float)rootScreen.X - (width / 2); boxY = (float)rootScreen.Y - height; validBox = true; }

                if (!validBox) continue; if (boxX < -width || boxX > SCREEN_WIDTH || boxY < -height || boxY > SCREEN_HEIGHT) continue;
                var distColor = ent.Distance < 30 ? _enemyPen : _enemyPenFar;
                if (_drawBox) g.DrawRectangle(distColor, boxX, boxY, width, height);

                if (_drawLines) { float targetX = headScreen.HasValue ? (float)headScreen.Value.X : (boxX + width / 2); float targetY = headScreen.HasValue ? (float)headScreen.Value.Y : (boxY + height / 2); g.DrawLine(_linePen, centerX, SCREEN_HEIGHT, targetX, targetY); }
                if (_drawDist) { float estimatedWidth = ent.LabelText.Length * 6.0f; float textX = boxX + (width - estimatedWidth) / 2; float textY = boxY - 15; g.DrawString(ent.LabelText, _itemFont, Brushes.Salmon, textX, textY); }

                // [AIMBOT LOGIC]
                if (ent.HasWeakspot && ent.WorldWeakspot.HasValue)
                {
                    // Use FRESH screen position for aimbot
                    var aimScreen = _w2s.Project(ent.WorldWeakspot.Value, cam.Value);

                    if (aimScreen.HasValue)
                    {
                        float dx = (float)aimScreen.Value.X - centerX;
                        float dy = (float)aimScreen.Value.Y - centerY;
                        float screenDist = (float)Math.Sqrt(dx * dx + dy * dy);

                        if (screenDist < AIM_FOV_RADIUS)
                        {
                            if (ent.Distance < closestWorldDist)
                            {
                                closestWorldDist = ent.Distance;
                                bestTargetPos = ent.WorldWeakspot; // Keep as World Pos
                            }
                        }
                    }
                }
            }

            if (_drawItems)
            {
                foreach (var item in items)
                {
                    var iScreen = _w2s.Project(item.WorldPos, cam.Value);
                    if (iScreen.HasValue)
                    {
                        if (iScreen.Value.X > 0 && iScreen.Value.X < SCREEN_WIDTH && iScreen.Value.Y > 0 && iScreen.Value.Y < SCREEN_HEIGHT)
                        {
                            g.DrawString(item.Text, _itemFont, item.Color, (float)iScreen.Value.X, (float)iScreen.Value.Y);
                        }
                    }
                }
            }

            if (bestTargetPos.HasValue && (GetAsyncKeyState(0x02) & 0x8000) != 0)
            {
                var screenTarget = _w2s.Project(bestTargetPos.Value, cam.Value);
                if (screenTarget.HasValue)
                {
                    float deltaX = (float)screenTarget.Value.X - centerX;
                    float deltaY = (float)screenTarget.Value.Y - centerY;

                    if (Math.Abs(deltaX) >= 2 || Math.Abs(deltaY) >= 2)
                    {
                        float smooth = 4.0f;
                        int moveX = (int)(deltaX / smooth);
                        int moveY = (int)(deltaY / smooth);

                        if (moveX == 0 && Math.Abs(deltaX) > 2) moveX = deltaX > 0 ? 1 : -1;
                        if (moveY == 0 && Math.Abs(deltaY) > 2) moveY = deltaY > 0 ? 1 : -1;

                        mouse_event(MOUSEEVENTF_MOVE, moveX, moveY, 0, 0);
                    }
                }
            }
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
        int menuX = 20; int menuY = 20; int width = 220; int height = 260; int headerHeight = 25;
        g.FillRectangle(_menuBgBrush, menuX, menuY, width, height); g.DrawRectangle(_borderPen, menuX, menuY, width, height);
        g.FillRectangle(_headerBrush, menuX, menuY, width, headerHeight); g.DrawRectangle(_borderPen, menuX, menuY, width, headerHeight);
        g.DrawString("REMNANT 2 EXTERNAL", _titleFont, _textBrush, menuX + 5, menuY + 4);
        string status = attached ? "ATTACHED" : "WAITING"; Brush statusColor = attached ? _onBrush : _offBrush; g.DrawString(status, _itemFont, statusColor, menuX + width - 70, menuY + 4);
        int itemY = menuY + headerHeight + 10; int gap = 20;
        void DrawToggle(string label, string key, bool isActive) { g.DrawString(key, _itemFont, _offBrush, menuX + 10, itemY); g.DrawString(label, _itemFont, _textBrush, menuX + 45, itemY); string state = isActive ? "ON" : "OFF"; Brush stateBrush = isActive ? _onBrush : _offBrush; g.DrawString(state, _itemFont, stateBrush, menuX + width - 40, itemY); itemY += gap; }
        DrawToggle("ESP Master", "[F5]", enabled); DrawToggle("Snaplines", "[F6]", _drawLines); DrawToggle("Weakspots", "[F7]", _drawWeakspot); DrawToggle("2D Box", "[F8]", _drawBox); DrawToggle("Distance", "[F9]", _drawDist); DrawToggle("Skeleton", "[F10]", _drawSkeleton);
        DrawToggle("Item ESP", "[INS]", _drawItems);
        string debugModeStr = _boneDisplayMode == 0 ? "OFF" : (_boneDisplayMode == 1 ? "NUM" : "NAME"); g.DrawString("[F11]", _itemFont, _offBrush, menuX + 10, itemY); g.DrawString("Debug Bones", _itemFont, _textBrush, menuX + 45, itemY); g.DrawString(debugModeStr, _itemFont, _boneDisplayMode > 0 ? _onBrush : _offBrush, menuX + width - 40, itemY); itemY += gap;
        g.DrawString("[F12] Dump Entities (Output)", _itemFont, _offBrush, menuX + 10, itemY);
        g.DrawLine(_borderPen, menuX, itemY + 25, menuX + width, itemY + 25); itemY += 30;
        g.DrawString($"FPS: {_currentFps}", _itemFont, _offBrush, menuX + 10, itemY);
    }
}