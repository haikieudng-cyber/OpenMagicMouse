using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenMagicMouse.Models;

namespace OpenMagicMouse.Gestures;

/// <summary>
/// Executes macOS-style Windows 11 multitasking shortcuts using Win32 SendInput.
/// Strictly x64-safe (sizeof(INPUT) == 40 bytes verified in --selftest).
/// </summary>
public static class ActionExecutor
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyUp = 0x0002;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint MouseeventfMiddleDown = 0x0020;
    private const uint MouseeventfMiddleUp = 0x0040;
    private const uint MouseeventfWheel = 0x0800;
    private const uint MouseeventfHWheel = 0x1000;
    public const nint MagicScrollExtraInfo = 0x4F4D4D32; // 'OMM2'

    private const ushort VkLControl = 0xA2;
    private const ushort VkLWin = 0x5B;
    private const ushort VkLMenu = 0xA4;
    private const ushort VkLShift = 0xA0;
    private const ushort VkTab = 0x09;
    private const ushort VkLeft = 0x25;
    private const ushort VkUp = 0x26;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;
    private const ushort VkD = 0x44;
    private const ushort VkBrowserBack = 0xA6;
    private const ushort VkBrowserForward = 0xA7;

    public static int InputStructSize => Marshal.SizeOf<INPUT>();

    public static int GetVirtualDesktopCount()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("VirtualDesktopIDs") is byte[] ids && ids.Length >= 16)
            {
                return Math.Max(1, ids.Length / 16);
            }
        }
        catch
        {
            // Fallback
        }
        return 1;
    }

    public static void CreateNewVirtualDesktop()
    {
        SendKeyCombo(VkLControl, VkLWin, VkD);
    }

    public static string DescribeAction(GestureAction action) => action switch
    {
        GestureAction.VirtualDesktopRight => "Chuyển Desktop Phải (Ctrl+Win+→)",
        GestureAction.VirtualDesktopLeft => "Chuyển Desktop Trái (Ctrl+Win+←)",
        GestureAction.TaskView => "Mission Control / Task View (Win+Tab)",
        GestureAction.ShowDesktop => "Hiện Desktop (Win+D)",
        GestureAction.BrowserBack => "Quay lại trang trước (Back)",
        GestureAction.BrowserForward => "Tiến tới trang sau (Forward)",
        GestureAction.MiddleClick => "Nhấn chuột giữa (Middle Click)",
        GestureAction.AltTabNext => "Chuyển cửa sổ tiếp theo (Alt+Tab)",
        GestureAction.AltTabPrev => "Chuyển cửa sổ trước (Alt+Shift+Tab)",
        GestureAction.MaximizeWindow => "Phóng to cửa sổ (Win+↑)",
        GestureAction.MinimizeWindow => "Thu nhỏ cửa sổ (Win+↓)",
        _ => ""
    };

    public static void Execute(GestureAction action)
    {
        switch (action)
        {
            case GestureAction.VirtualDesktopLeft:
                SendKeyCombo(VkLControl, VkLWin, VkLeft);
                break;
            case GestureAction.VirtualDesktopRight:
                SendKeyCombo(VkLControl, VkLWin, VkRight);
                break;
            case GestureAction.TaskView:
                SendKeyCombo(VkLWin, VkTab);
                break;
            case GestureAction.ShowDesktop:
                SendKeyCombo(VkLWin, VkD);
                break;
            case GestureAction.BrowserBack:
                SendSingleKey(VkBrowserBack, extended: false);
                break;
            case GestureAction.BrowserForward:
                SendSingleKey(VkBrowserForward, extended: false);
                break;
            case GestureAction.MiddleClick:
                SendMiddleClick();
                break;
            case GestureAction.AltTabNext:
                SendKeyCombo(VkLMenu, VkTab);
                break;
            case GestureAction.AltTabPrev:
                SendKeyCombo(VkLMenu, VkLShift, VkTab);
                break;
            case GestureAction.MaximizeWindow:
                SendKeyCombo(VkLWin, VkUp);
                break;
            case GestureAction.MinimizeWindow:
                SendKeyCombo(VkLWin, VkDown);
                break;
        }
    }

    /// <summary>
    /// Emits sub-detent high-resolution WM_MOUSEWHEEL / WM_MOUSEHWHEEL ticks stamped with
    /// MagicScrollExtraInfo so Chromium/Edge/Electron treat them as pixel-precise scroll deltas.
    /// </summary>
    public static void SendPreciseScroll(int verticalDelta, int horizontalDelta)
    {
        // Never emit exact multiples of WHEEL_DELTA (120) so browsers set has_precise_scrolling_deltas=true
        if (verticalDelta != 0 && verticalDelta % 120 == 0)
        {
            verticalDelta += verticalDelta > 0 ? -1 : 1;
        }
        if (horizontalDelta != 0 && horizontalDelta % 120 == 0)
        {
            horizontalDelta += horizontalDelta > 0 ? -1 : 1;
        }

        if (verticalDelta != 0 && horizontalDelta != 0)
        {
            var inputs = new INPUT[]
            {
                MakeWheelInput(MouseeventfWheel, verticalDelta),
                MakeWheelInput(MouseeventfHWheel, horizontalDelta)
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
        else if (verticalDelta != 0)
        {
            var inputs = new INPUT[] { MakeWheelInput(MouseeventfWheel, verticalDelta) };
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }
        else if (horizontalDelta != 0)
        {
            var inputs = new INPUT[] { MakeWheelInput(MouseeventfHWheel, horizontalDelta) };
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT MakeWheelInput(uint flags, int delta) => new()
    {
        type = InputMouse,
        u = new InputUnion
        {
            mi = new MOUSEINPUT
            {
                mouseData = unchecked((uint)delta),
                dwFlags = flags,
                dwExtraInfo = MagicScrollExtraInfo
            }
        }
    };

    private static bool IsExtendedKey(ushort vk) => vk is VkLeft or VkUp or VkRight or VkDown or VkLWin;

    private static void SendSingleKey(ushort vk, bool extended)
    {
        uint ext = extended ? KeyeventfExtendedKey : 0;
        var inputs = new INPUT[]
        {
            MakeKeyInput(vk, ext),
            MakeKeyInput(vk, ext | KeyeventfKeyUp)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyCombo(params ushort[] vks)
    {
        var inputs = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++)
        {
            uint ext = IsExtendedKey(vks[i]) ? KeyeventfExtendedKey : 0;
            inputs[i] = MakeKeyInput(vks[i], ext);
        }
        for (int i = 0; i < vks.Length; i++)
        {
            int idx = vks.Length - 1 - i;
            uint ext = IsExtendedKey(vks[idx]) ? KeyeventfExtendedKey : 0;
            inputs[vks.Length + i] = MakeKeyInput(vks[idx], ext | KeyeventfKeyUp);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMiddleClick()
    {
        var inputs = new INPUT[]
        {
            new()
            {
                type = InputMouse,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MouseeventfMiddleDown } }
            },
            new()
            {
                type = InputMouse,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MouseeventfMiddleUp } }
            }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeKeyInput(ushort vk, uint flags) => new()
    {
        type = InputKeyboard,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = nint.Zero
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);
}
