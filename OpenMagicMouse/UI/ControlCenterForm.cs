using System.Diagnostics;
using System.Drawing.Drawing2D;
using OpenMagicMouse.Backend;
using OpenMagicMouse.Gestures;
using OpenMagicMouse.Models;
using OpenMagicMouse.Scrolling;

namespace OpenMagicMouse.UI;

/// <summary>
/// Dark-mode macOS-inspired Control Center &amp; System Tray host for OpenMagicMouse 2.0.
/// </summary>
public sealed class ControlCenterForm : Form
{
    private readonly GestureConfig _config;
    private readonly MultiTouchGestureEngine _engine;
    private readonly LowLevelWheelFilter _wheelFilter;
    private readonly MacScrollPhysicsEngine _scrollEngine;
    private readonly PipeTouchClient _client;
    private readonly EventWaitHandle? _showUiEvent;

    private readonly MouseTouchCanvas _canvas;
    private readonly Label _lblServiceStatus;
    private readonly Label _lblBattery;
    private readonly Label _lblDesktopCount;
    private readonly Button _btnAddDesktop;
    private readonly Button _btnBounceExt;

    private readonly CheckBox _chkSmoothScroll;
    private readonly CheckBox _chkNaturalScroll;
    private readonly CheckBox _chkEnabled;
    private readonly CheckBox _chkNatural;
    private readonly NoWheelComboBox _cmb2H;
    private readonly NoWheelComboBox _cmb2DblTap;
    private readonly NoWheelComboBox _cmb2Up;
    private readonly NoWheelComboBox _cmb2Down;
    private readonly NoWheelComboBox _cmb1Flick;
    private readonly NoWheelComboBox _cmb3Tap;
    private readonly NoWheelComboBox _cmb3H;
    private readonly NoWheelTrackBar _trkScrollSpeed;
    private readonly Label _lblScrollSpeedVal;
    private readonly NoWheelTrackBar _trkSens;
    private readonly Label _lblSensVal;

    private readonly NotifyIcon _trayIcon;
    private readonly Icon _appIcon;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _coastUiTimer;

    private bool _allowExit;
    private bool _backendConnected;
    private int _latestBattery = -1;

    private sealed class NoWheelComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            // Prevent accidental mouse-wheel scrolling over unselected settings dropdowns
            if ((m.Msg == 0x020A || m.Msg == 0x020E) && !DroppedDown)
            {
                return;
            }
            base.WndProc(ref m);
        }
    }

    private sealed class NoWheelTrackBar : TrackBar
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A || m.Msg == 0x020E)
            {
                return;
            }
            base.WndProc(ref m);
        }
    }

    private sealed record OptionItem(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    public ControlCenterForm(bool startMinimized, EventWaitHandle? showUiEvent)
    {
        _showUiEvent = showUiEvent;
        _config = GestureConfig.LoadOrDefault();
        _engine = new MultiTouchGestureEngine(_config, OnGestureRecognized);
        _wheelFilter = new LowLevelWheelFilter();
        _scrollEngine = new MacScrollPhysicsEngine(_config, wheelFilter: _wheelFilter);
        _client = new PipeTouchClient();

        _appIcon = CreateVectorMouseIcon();
        Icon = _appIcon;
        Text = "OpenMagicMouse 2.2 — Đa điểm & Cuộn Quán Tính Lực Chuẩn Mac";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(950, 680);
        BackColor = Color.FromArgb(18, 20, 26);
        ForeColor = Color.FromArgb(240, 244, 250);
        Font = new Font("Segoe UI", 9.5f);

        // Top Header
        var pnlHeader = new Panel
        {
            Location = new Point(20, 14),
            Size = new Size(910, 52),
            BackColor = Color.Transparent
        };
        var lblTitle = new Label
        {
            Text = "OpenMagicMouse 2.2",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var lblSub = new Label
        {
            Text = "Cuộn 1 ngón 125Hz quán tính macOS + Đa điểm 2–3 ngón + Hiệu ứng nảy đàn hồi Safari (Mã nguồn mở 100%)",
            Font = new Font("Segoe UI", 9.2f),
            ForeColor = Color.FromArgb(146, 158, 180),
            AutoSize = true,
            Location = new Point(2, 30)
        };
        pnlHeader.Controls.Add(lblTitle);
        pnlHeader.Controls.Add(lblSub);
        Controls.Add(pnlHeader);

        // Left Card: Visualizer & Live Hardware Telemetry
        var cardLeft = CreateCardPanel(20, 74, 340, 590);
        var lblCanvasTitle = new Label
        {
            Text = "CẢM BIẾN ĐA ĐIỂM TRỰC TIẾP",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(130, 145, 170),
            Location = new Point(16, 12),
            AutoSize = true
        };
        _canvas = new MouseTouchCanvas
        {
            Location = new Point(12, 36),
            Size = new Size(316, 356)
        };

        _lblServiceStatus = new Label
        {
            Text = "● Đang kết nối dịch vụ đa điểm...",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 159, 10),
            Location = new Point(16, 402),
            Size = new Size(308, 22)
        };
        _lblBattery = new Label
        {
            Text = "🔋 Pin Magic Mouse 2: Tự đồng bộ định kỳ",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(200, 210, 225),
            Location = new Point(16, 428),
            Size = new Size(308, 22)
        };
        _lblDesktopCount = new Label
        {
            Text = "🖥️ Màn hình ảo hiện tại: ...",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(200, 210, 225),
            Location = new Point(16, 452),
            Size = new Size(308, 22)
        };
        _btnAddDesktop = new Button
        {
            Text = "➕ Tạo Màn hình ảo thứ 2 (Ctrl+Win+D)",
            Location = new Point(16, 480),
            Size = new Size(308, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(10, 132, 255),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnAddDesktop.FlatAppearance.BorderSize = 0;
        _btnAddDesktop.Click += (_, _) =>
        {
            ActionExecutor.CreateNewVirtualDesktop();
            RefreshDesktopCount();
            _canvas.ShowGestureBanner("Đã tạo thêm Màn hình ảo mới!");
        };

        _btnBounceExt = new Button
        {
            Text = "🌐 Bật hiệu ứng cuộn \"Nảy Bật Lại\" (Chrome/Edge)",
            Location = new Point(16, 540),
            Size = new Size(308, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(44, 50, 66),
            ForeColor = Color.FromArgb(230, 238, 252),
            Font = new Font("Segoe UI", 8.8f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnBounceExt.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 195);
        _btnBounceExt.Click += (_, _) => OpenSafariBounceGuide();

        cardLeft.Controls.AddRange([lblCanvasTitle, _canvas, _lblServiceStatus, _lblBattery, _lblDesktopCount, _btnAddDesktop, _btnBounceExt]);
        Controls.Add(cardLeft);

        // Right Card: macOS Smooth Scroll + Gesture Settings
        var cardRight = CreateCardPanel(376, 74, 554, 590);
        var lblSettingsTitle = new Label
        {
            Text = "CUỘN MƯỢT & CỬ CHỈ CHUẨN MACOS",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(130, 145, 170),
            Location = new Point(20, 12),
            AutoSize = true
        };
        cardRight.Controls.Add(lblSettingsTitle);

        _chkSmoothScroll = CreateDarkCheckBox("Cuộn 1 ngón quán tính Mac (125Hz)", 20, 38);
        _chkSmoothScroll.Checked = _config.SmoothScrollEnabled;

        _chkEnabled = CreateDarkCheckBox("Bật cử chỉ đa điểm (2–3 ngón)", 305, 38);
        _chkEnabled.Checked = _config.Enabled;

        _chkNaturalScroll = CreateDarkCheckBox("Cuộn tự nhiên (Đẩy lên = đọc xuống)", 20, 66);
        _chkNaturalScroll.Checked = _config.NaturalScroll;

        _chkNatural = CreateDarkCheckBox("Vuốt 2–3 ngón tự nhiên chuẩn Mac", 305, 66);
        _chkNatural.Checked = _config.NaturalTwoFingerSwipe;

        cardRight.Controls.AddRange([_chkSmoothScroll, _chkEnabled, _chkNaturalScroll, _chkNatural]);

        int rowY = 104;
        int rowStep = 41;

        _cmb2H = AddGestureRow(cardRight, "Vuốt 2 ngón Trái / Phải", rowY, [
            new("VirtualDesktop", "Chuyển Màn hình ảo (Ctrl+Win+←/→)"),
            new("BrowserBackForward", "Trình duyệt Back / Forward"),
            new("AltTab", "Chuyển ứng dụng (Alt+Tab)"),
            new("None", "Tắt cử chỉ này")
        ], _config.TwoFingerHorizontalAction);

        _cmb2DblTap = AddGestureRow(cardRight, "Chạm đúp 2 ngón (Chuẩn Mac)", rowY += rowStep, [
            new("TaskView", "Mission Control / Task View (Win+Tab)"),
            new("ShowDesktop", "Hiện Desktop (Win+D)"),
            new("MiddleClick", "Nhấn chuột giữa (Middle Click)"),
            new("None", "Tắt cử chỉ này")
        ], _config.TwoFingerDoubleTapAction);

        _cmb2Up = AddGestureRow(cardRight, "Vuốt 2 ngón Lên", rowY += rowStep, [
            new("TaskView", "Mission Control / Task View (Win+Tab)"),
            new("MaximizeWindow", "Phóng to cửa sổ (Win+↑)"),
            new("None", "Tắt cử chỉ này")
        ], _config.TwoFingerSwipeUpAction);

        _cmb2Down = AddGestureRow(cardRight, "Vuốt 2 ngón Xuống", rowY += rowStep, [
            new("ShowDesktop", "Hiện Desktop (Win+D)"),
            new("MinimizeWindow", "Thu nhỏ cửa sổ (Win+↓)"),
            new("None", "Tắt cử chỉ này")
        ], _config.TwoFingerSwipeDownAction);

        _cmb1Flick = AddGestureRow(cardRight, "Hất nhanh 1 ngón Ngang", rowY += rowStep, [
            new("BrowserBackForward", "Trình duyệt Back / Forward (chuẩn Safari)"),
            new("None", "Tắt cử chỉ này")
        ], _config.OneFingerFastFlickAction);

        _cmb3Tap = AddGestureRow(cardRight, "Chạm / Bấm 3 ngón", rowY += rowStep, [
            new("MiddleClick", "Nhấn chuột giữa (Mở tab mới / Middle Click)"),
            new("TaskView", "Mission Control / Task View (Win+Tab)"),
            new("ShowDesktop", "Hiện Desktop (Win+D)"),
            new("None", "Tắt cử chỉ này")
        ], _config.ThreeFingerTapAction);

        _cmb3H = AddGestureRow(cardRight, "Vuốt 3 ngón Trái / Phải", rowY += rowStep, [
            new("AltTab", "Chuyển cửa sổ (Alt+Tab)"),
            new("VirtualDesktop", "Chuyển Màn hình ảo (Ctrl+Win+←/→)"),
            new("BrowserBackForward", "Trình duyệt Back / Forward"),
            new("None", "Tắt cử chỉ này")
        ], _config.ThreeFingerHorizontalAction);

        // Scroll speed slider
        int scrollSliderY = rowY + 46;
        var lblScrollTitle = new Label
        {
            Text = "Tốc độ & đà cuộn trang:",
            Location = new Point(20, scrollSliderY + 4),
            AutoSize = true,
            ForeColor = Color.FromArgb(220, 228, 240)
        };
        _trkScrollSpeed = new NoWheelTrackBar
        {
            Minimum = 5,
            Maximum = 18,
            Value = Math.Clamp((int)Math.Round(_config.ScrollSpeed * 10), 5, 18),
            TickFrequency = 1,
            Location = new Point(215, scrollSliderY - 2),
            Size = new Size(255, 34),
            BackColor = Color.FromArgb(28, 32, 41)
        };
        _lblScrollSpeedVal = new Label
        {
            Text = $"{_trkScrollSpeed.Value * 10}%",
            Location = new Point(480, scrollSliderY + 4),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(48, 209, 88)
        };
        cardRight.Controls.AddRange([lblScrollTitle, _trkScrollSpeed, _lblScrollSpeedVal]);

        // Gesture sensitivity slider
        int sensY = scrollSliderY + 42;
        var lblSensTitle = new Label
        {
            Text = "Độ nhạy nhận diện cử chỉ:",
            Location = new Point(20, sensY + 4),
            AutoSize = true,
            ForeColor = Color.FromArgb(220, 228, 240)
        };
        _trkSens = new NoWheelTrackBar
        {
            Minimum = 6,
            Maximum = 15,
            Value = Math.Clamp((int)Math.Round(_config.Sensitivity * 10), 6, 15),
            TickFrequency = 1,
            Location = new Point(215, sensY - 2),
            Size = new Size(255, 34),
            BackColor = Color.FromArgb(28, 32, 41)
        };
        _lblSensVal = new Label
        {
            Text = $"{_trkSens.Value * 10}%",
            Location = new Point(480, sensY + 4),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(10, 132, 255)
        };
        cardRight.Controls.AddRange([lblSensTitle, _trkSens, _lblSensVal]);

        var btnReset = new Button
        {
            Text = "↺ Khôi phục mặc định chuẩn Mac",
            Location = new Point(20, 540),
            Size = new Size(248, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(44, 50, 64),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        btnReset.FlatAppearance.BorderColor = Color.FromArgb(72, 82, 104);
        btnReset.Click += (_, _) => ResetDefaults();

        var btnHideToTray = new Button
        {
            Text = "Thu gọn xuống khay hệ thống (Chạy nền)",
            Location = new Point(282, 540),
            Size = new Size(252, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 209, 88),
            ForeColor = Color.FromArgb(14, 20, 18),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnHideToTray.FlatAppearance.BorderSize = 0;
        btnHideToTray.Click += (_, _) => HideToTray();

        cardRight.Controls.Add(btnReset);
        cardRight.Controls.Add(btnHideToTray);
        Controls.Add(cardRight);

        // Wire live config change events
        _chkSmoothScroll.CheckedChanged += (_, _) => ApplyAndSaveConfig();
        _chkNaturalScroll.CheckedChanged += (_, _) => ApplyAndSaveConfig();
        _chkEnabled.CheckedChanged += (_, _) => ApplyAndSaveConfig();
        _chkNatural.CheckedChanged += (_, _) => ApplyAndSaveConfig();
        _cmb2H.SelectedIndexChanged += (_, _) => ApplyAndSaveConfig();
        _cmb2DblTap.SelectedIndexChanged += (_, _) => ApplyAndSaveConfig();
        _cmb2Up.SelectedIndexChanged += (_, _) => ApplyAndSaveConfig();
        _cmb2Down.SelectedIndexChanged += (_, _) => ApplyAndSaveConfig();
        _cmb1Flick.SelectedIndexChanged += (_, _) => ApplyAndSaveConfig();
        _cmb3Tap.SelectedIndexChanged += (_, _) => ApplyAndSaveConfig();
        _cmb3H.SelectedIndexChanged += (_, _) => ApplyAndSaveConfig();
        _trkScrollSpeed.ValueChanged += (_, _) =>
        {
            _lblScrollSpeedVal.Text = $"{_trkScrollSpeed.Value * 10}%";
            ApplyAndSaveConfig();
        };
        _trkSens.ValueChanged += (_, _) =>
        {
            _lblSensVal.Text = $"{_trkSens.Value * 10}%";
            ApplyAndSaveConfig();
        };

        // Tray Icon
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mở Bảng điều khiển & Cảm biến Đa điểm", null, (_, _) => RestoreFromTray());
        var toggleItem = new ToolStripMenuItem("Bật cuộn mượt & cử chỉ đa điểm") { Checked = _config.Enabled, CheckOnClick = true };
        toggleItem.CheckedChanged += (_, _) => _chkEnabled.Checked = toggleItem.Checked;
        trayMenu.Items.Add(toggleItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Thoát hoàn toàn OpenMagicMouse UI", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "OpenMagicMouse 2.2 — Cuộn quán tính & Đa điểm Mac",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        // Status timer
        _statusTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _statusTimer.Tick += (_, _) => RefreshUiBadges();
        _statusTimer.Start();

        // Coasting indicator timer (runs only when visible)
        _coastUiTimer = new System.Windows.Forms.Timer { Interval = 45 };
        _coastUiTimer.Tick += (_, _) =>
        {
            if (Visible && !IsDisposed)
            {
                _canvas.SetCoastingState(_scrollEngine.IsCoasting);
            }
        };
        _coastUiTimer.Start();

        // Connect live backend events
        _client.OnConnectionChanged += (connected, batt, desc) =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                _backendConnected = connected;
                if (batt > 0) _latestBattery = batt;
                UpdateStatusLabel(desc);
                RefreshUiBadges();
            });
        };

        _client.OnFrameReceived += frame =>
        {
            _scrollEngine.ProcessFrame(frame);
            _engine.ProcessFrame(frame);

            if (frame.BatteryPercent > 0)
            {
                _latestBattery = frame.BatteryPercent;
            }

            if (Visible && !IsDisposed)
            {
                BeginInvoke(() =>
                {
                    _canvas.SetCoastingState(_scrollEngine.IsCoasting);
                    _canvas.UpdateFrame(frame);
                });
            }
        };

        RefreshDesktopCount();
        _client.Start();
        StartShowUiWaiter();

        if (startMinimized)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Visible = false;
        }
    }

    private void OpenSafariBounceGuide()
    {
        string extDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "MacSafariBounceExtension"));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 4 && dir != null && !Directory.Exists(extDir); i++)
        {
            string probe = Path.Combine(dir.FullName, "MacSafariBounceExtension");
            if (Directory.Exists(probe))
            {
                extDir = probe;
                break;
            }
            dir = dir.Parent;
        }

        try { Clipboard.SetText(extDir); } catch { }
        try
        {
            if (Directory.Exists(extDir))
            {
                string demoPath = Path.Combine(extDir, "SafariBounceDemo.html");
                if (File.Exists(demoPath))
                {
                    Process.Start(new ProcessStartInfo(demoPath) { UseShellExecute = true });
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{extDir}\"") { UseShellExecute = true });
            }
        }
        catch { }

        MessageBox.Show(
            "Đã sao chép đường dẫn Extension nảy đàn hồi Safari vào Clipboard và mở thư mục!\n\n" +
            $"Thư mục: {extDir}\n\n" +
            "Cách bật trong 15 giây (làm 1 lần duy nhất cho Chrome hoặc Edge):\n" +
            "1. Mở chrome://extensions (hoặc edge://extensions) trên trình duyệt.\n" +
            "2. Bật công tắc \"Developer mode\" (Chế độ dành cho nhà phát triển) ở góc phải.\n" +
            "3. Bấm \"Load unpacked\" (Tải tiện ích đã giải nén) và Ctrl+V đường dẫn vừa copy.",
            "Hiệu ứng cuộn Nảy Bật Lại chuẩn Safari",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void StartShowUiWaiter()
    {
        if (_showUiEvent == null) return;
        var t = new Thread(() =>
        {
            while (!IsDisposed)
            {
                if (_showUiEvent.WaitOne(500))
                {
                    try
                    {
                        if (!IsDisposed)
                        {
                            BeginInvoke(RestoreFromTray);
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "OpenMagicMouse.ShowUiWaiter"
        };
        t.Start();
    }

    private void OnGestureRecognized(GestureAction action, string label)
    {
        ActionExecutor.Execute(action);
        string desc = ActionExecutor.DescribeAction(action);
        if (!IsDisposed)
        {
            BeginInvoke(() => _canvas.ShowGestureBanner(string.IsNullOrEmpty(desc) ? label : $"{label} → {desc}"));
        }
    }

    private void ApplyAndSaveConfig()
    {
        _config.ConfigVersion = 3;
        _config.SmoothScrollEnabled = _chkSmoothScroll.Checked;
        _config.NaturalScroll = _chkNaturalScroll.Checked;
        _config.Enabled = _chkEnabled.Checked;
        _config.NaturalTwoFingerSwipe = _chkNatural.Checked;
        _config.TwoFingerHorizontalAction = GetSelectedKey(_cmb2H, "VirtualDesktop");
        _config.TwoFingerDoubleTapAction = GetSelectedKey(_cmb2DblTap, "TaskView");
        _config.TwoFingerSwipeUpAction = GetSelectedKey(_cmb2Up, "TaskView");
        _config.TwoFingerSwipeDownAction = GetSelectedKey(_cmb2Down, "ShowDesktop");
        _config.OneFingerFastFlickAction = GetSelectedKey(_cmb1Flick, "BrowserBackForward");
        _config.ThreeFingerTapAction = GetSelectedKey(_cmb3Tap, "MiddleClick");
        _config.ThreeFingerHorizontalAction = GetSelectedKey(_cmb3H, "AltTab");
        _config.ScrollSpeed = _trkScrollSpeed.Value / 10.0;
        _config.Sensitivity = _trkSens.Value / 10.0;

        _engine.UpdateConfig(_config);
        _scrollEngine.UpdateConfig(_config);
        _config.Save();
    }

    private void ResetDefaults()
    {
        var def = new GestureConfig();
        _chkSmoothScroll.Checked = def.SmoothScrollEnabled;
        _chkNaturalScroll.Checked = def.NaturalScroll;
        _chkEnabled.Checked = def.Enabled;
        _chkNatural.Checked = def.NaturalTwoFingerSwipe;
        SelectOption(_cmb2H, def.TwoFingerHorizontalAction);
        SelectOption(_cmb2DblTap, def.TwoFingerDoubleTapAction);
        SelectOption(_cmb2Up, def.TwoFingerSwipeUpAction);
        SelectOption(_cmb2Down, def.TwoFingerSwipeDownAction);
        SelectOption(_cmb1Flick, def.OneFingerFastFlickAction);
        SelectOption(_cmb3Tap, def.ThreeFingerTapAction);
        SelectOption(_cmb3H, def.ThreeFingerHorizontalAction);
        _trkScrollSpeed.Value = 10;
        _trkSens.Value = 10;
        ApplyAndSaveConfig();
        _canvas.ShowGestureBanner("Đã khôi phục cấu hình chuẩn macOS!");
    }

    private void UpdateStatusLabel(string desc)
    {
        _lblServiceStatus.Text = _backendConnected
            ? "🟢 Dịch vụ Đa điểm & Cuộn 125Hz: ĐANG CHẠY"
            : $"🟠 {desc}";
        _lblServiceStatus.ForeColor = _backendConnected
            ? Color.FromArgb(48, 209, 88)
            : Color.FromArgb(255, 159, 10);
    }

    private void RefreshUiBadges()
    {
        _lblBattery.Text = _latestBattery > 0
            ? $"🔋 Pin Magic Mouse 2: {_latestBattery}%"
            : "🔋 Pin Magic Mouse 2: Đầy / Tự đồng bộ định kỳ";
        RefreshDesktopCount();
    }

    private void RefreshDesktopCount()
    {
        int count = ActionExecutor.GetVirtualDesktopCount();
        _lblDesktopCount.Text = count > 1
            ? $"🖥️ Số Màn hình ảo (Spaces): {count} (Sẵn sàng vuốt!)"
            : "🖥️ Số Màn hình ảo: 1 (Cần >= 2 để vuốt chuyển)";
        _lblDesktopCount.ForeColor = count > 1
            ? Color.FromArgb(48, 209, 88)
            : Color.FromArgb(255, 159, 10);
        _btnAddDesktop.Visible = count <= 1;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _trayIcon.Visible = false;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Stop();
            _statusTimer.Dispose();
            _coastUiTimer.Stop();
            _coastUiTimer.Dispose();
            _client.Dispose();
            _scrollEngine.Dispose();
            _wheelFilter.Dispose();
            _trayIcon.Dispose();
            _appIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Panel CreateCardPanel(int x, int y, int w, int h) => new()
    {
        Location = new Point(x, y),
        Size = new Size(w, h),
        BackColor = Color.FromArgb(28, 32, 41)
    };

    private static CheckBox CreateDarkCheckBox(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        ForeColor = Color.FromArgb(236, 240, 248),
        Font = new Font("Segoe UI", 9.2f, FontStyle.Bold)
    };

    private static NoWheelComboBox AddGestureRow(Panel parent, string title, int y, OptionItem[] items, string selectedKey)
    {
        var lbl = new Label
        {
            Text = title,
            Location = new Point(20, y + 6),
            Size = new Size(215, 22),
            ForeColor = Color.FromArgb(215, 222, 236)
        };
        var cmb = new NoWheelComboBox
        {
            Location = new Point(242, y + 2),
            Size = new Size(292, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 43, 56),
            ForeColor = Color.White
        };
        cmb.Items.AddRange(items);
        SelectOption(cmb, selectedKey);
        parent.Controls.Add(lbl);
        parent.Controls.Add(cmb);
        return cmb;
    }

    private static void SelectOption(ComboBox cmb, string key)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (cmb.Items[i] is OptionItem item && string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedIndex = i;
                return;
            }
        }
        if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
    }

    private static string GetSelectedKey(ComboBox cmb, string fallback)
        => cmb.SelectedItem is OptionItem item ? item.Key : fallback;

    private static Icon CreateVectorMouseIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var path = new GraphicsPath();
        path.AddArc(7, 2, 18, 14, 180, 180);
        path.AddArc(7, 16, 18, 14, 0, 180);
        path.CloseFigure();

        using var bg = new LinearGradientBrush(new Rectangle(7, 2, 18, 28),
            Color.FromArgb(10, 132, 255), Color.FromArgb(48, 209, 88), LinearGradientMode.Vertical);
        g.FillPath(bg, path);

        using var border = new Pen(Color.White, 1.5f);
        g.DrawPath(border, path);

        using var dotBrush = new SolidBrush(Color.White);
        g.FillEllipse(dotBrush, 11, 9, 4, 4);
        g.FillEllipse(dotBrush, 17, 9, 4, 4);

        return Icon.FromHandle(bmp.GetHicon());
    }
}
