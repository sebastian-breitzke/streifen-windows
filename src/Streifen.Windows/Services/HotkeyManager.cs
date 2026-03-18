using System.Diagnostics;
using System.Windows.Interop;
using Streifen.Windows.Core;
using Streifen.Windows.Win32;

namespace Streifen.Windows.Services;

/// <summary>
/// Global hotkey manager using RegisterHotKey.
/// Hyper = Ctrl+Alt+Win. All hotkeys registered on a hidden WPF window.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const uint HYPER = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_WIN;
    private const uint HYPER_SHIFT = HYPER | NativeMethods.MOD_SHIFT;

    private readonly Dictionary<int, Action> _bindings = new();
    private IntPtr _hwnd;
    private HwndSource? _source;
    private int _nextId = 1;
    private bool _disposed;

    // Virtual key codes
    private const uint VK_LEFT = 0x25;
    private const uint VK_UP = 0x26;
    private const uint VK_RIGHT = 0x27;
    private const uint VK_DOWN = 0x28;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_F1 = 0x70;
    private const uint VK_F2 = 0x71;
    private const uint VK_F3 = 0x72;
    private const uint VK_F4 = 0x73;
    private const uint VK_F5 = 0x74;
    private const uint VK_F6 = 0x75;
    private const uint VK_F7 = 0x76;
    private const uint VK_F8 = 0x77;
    private const uint VK_F12 = 0x7B;
    private const uint VK_H = 0x48;
    private const uint VK_L = 0x4C;
    private const uint VK_OEM_MINUS = 0xBD;  // ß on German keyboard
    private const uint VK_OEM_PLUS = 0xBB;   // ´ on German keyboard

    public event Action<int>? SwitchWorkspace;
    public event Action<int>? MoveToWorkspace;
    public event Action? FocusLeft;
    public event Action? FocusRight;
    public event Action? ReorderLeft;
    public event Action? ReorderRight;
    public event Action? NextWorkspace;
    public event Action? PreviousWorkspace;
    public event Action? MoveToNextWorkspace;
    public event Action? MoveToPreviousWorkspace;
    public event Action<int>? SetSliceCount;
    public event Action<int>? StepSliceCount;
    public event Action<AppSize>? SetAppDefaultSize;
    public event Action? ResetSizes;
    public event Action? DebugDump;

    /// <summary>
    /// Register all hotkeys. Must be called after the hidden window is created.
    /// </summary>
    public void RegisterAll(IntPtr hwnd)
    {
        _hwnd = hwnd;

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        // Workspace switching: Hyper+1-9
        for (uint i = 0; i < 9; i++)
        {
            uint key = 0x31 + i; // VK_1 through VK_9
            int wsId = (int)i + 1;
            Register(HYPER | NativeMethods.MOD_NOREPEAT, key, () => SwitchWorkspace?.Invoke(wsId));
        }

        // Move to workspace: Hyper+Shift+1-9
        for (uint i = 0; i < 9; i++)
        {
            uint key = 0x31 + i;
            int wsId = (int)i + 1;
            Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, key, () => MoveToWorkspace?.Invoke(wsId));
        }

        // Focus navigation
        Register(HYPER | NativeMethods.MOD_NOREPEAT, VK_H, () => FocusLeft?.Invoke());
        Register(HYPER | NativeMethods.MOD_NOREPEAT, VK_LEFT, () => FocusLeft?.Invoke());
        Register(HYPER | NativeMethods.MOD_NOREPEAT, VK_L, () => FocusRight?.Invoke());
        Register(HYPER | NativeMethods.MOD_NOREPEAT, VK_RIGHT, () => FocusRight?.Invoke());

        // Reorder
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_LEFT, () => ReorderLeft?.Invoke());
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_RIGHT, () => ReorderRight?.Invoke());

        // Workspace up/down
        Register(HYPER | NativeMethods.MOD_NOREPEAT, VK_UP, () => NextWorkspace?.Invoke());
        Register(HYPER | NativeMethods.MOD_NOREPEAT, VK_DOWN, () => PreviousWorkspace?.Invoke());
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_UP, () => MoveToNextWorkspace?.Invoke());
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_DOWN, () => MoveToPreviousWorkspace?.Invoke());

        // Slice counts: Hyper+F1-F8 = set 1-8 slices directly
        for (int s = 1; s <= 8; s++)
        {
            uint key = VK_F1 + (uint)(s - 1);
            int slices = s;
            Register(HYPER | NativeMethods.MOD_NOREPEAT, key, () => SetSliceCount?.Invoke(slices));
        }

        // Slice step: Hyper+OEM_MINUS = -1, Hyper+OEM_PLUS = +1
        Register(HYPER | NativeMethods.MOD_NOREPEAT, VK_OEM_MINUS, () => StepSliceCount?.Invoke(-1));
        Register(HYPER | NativeMethods.MOD_NOREPEAT, VK_OEM_PLUS, () => StepSliceCount?.Invoke(+1));

        // App default sizes: Hyper+Shift+F1-F5
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_F1, () => SetAppDefaultSize?.Invoke(AppSize.XS));
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_F2, () => SetAppDefaultSize?.Invoke(AppSize.S));
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_F3, () => SetAppDefaultSize?.Invoke(AppSize.M));
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_F4, () => SetAppDefaultSize?.Invoke(AppSize.L));
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_F5, () => SetAppDefaultSize?.Invoke(AppSize.XL));

        // Reset all sizes
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_ESCAPE, () => ResetSizes?.Invoke());

        // Debug dump
        Register(HYPER_SHIFT | NativeMethods.MOD_NOREPEAT, VK_F12, () => DebugDump?.Invoke());

        Log($"Registered {_bindings.Count} hotkeys");
    }

    private void Register(uint modifiers, uint vk, Action action)
    {
        int id = _nextId++;
        if (NativeMethods.RegisterHotKey(_hwnd, id, modifiers, vk))
        {
            _bindings[id] = action;
        }
        else
        {
            Log($"Failed to register hotkey id={id} mod=0x{modifiers:X} vk=0x{vk:X}");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_bindings.TryGetValue(id, out var action))
            {
                action();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _bindings.Keys)
            NativeMethods.UnregisterHotKey(_hwnd, id);

        _source?.RemoveHook(WndProc);
        _bindings.Clear();
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[HotkeyManager] {message}");
        StreifenLog.Write($"[HotkeyManager] {message}");
    }
}
