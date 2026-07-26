using System.Runtime.InteropServices;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Management;

public sealed class WindowsShellProgressService : IShellProgressService
{
    private nint _windowHandle;
    private ITaskbarList3? _taskbar;

    public void Attach(nint windowHandle)
    {
        _windowHandle = windowHandle;
        _taskbar = (ITaskbarList3)new CTaskbarList();
        _taskbar.HrInit();
    }

    public void SetProgress(ShellProgressState state, int? percent = null)
    {
        if (_taskbar is null || _windowHandle == 0) return;
        var flag = state switch
        {
            ShellProgressState.Indeterminate => TaskbarProgressState.Indeterminate,
            ShellProgressState.Normal => TaskbarProgressState.Normal,
            ShellProgressState.Paused => TaskbarProgressState.Paused,
            ShellProgressState.Error => TaskbarProgressState.Error,
            _ => TaskbarProgressState.NoProgress
        };
        _taskbar.SetProgressState(_windowHandle, flag);
        if (percent is not null) _taskbar.SetProgressValue(_windowHandle, (ulong)Math.Clamp(percent.Value, 0, 100), 100);
    }

    public void RequestAttention()
    {
        if (_windowHandle == 0 || IsForeground()) return;
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = _windowHandle,
            Flags = 3 | 12,
            Count = 3,
            Timeout = 0
        };
        FlashWindowEx(ref info);
    }

    public bool IsForeground() => _windowHandle != 0 && GetForegroundWindow() == _windowHandle;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private enum TaskbarProgressState { NoProgress = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class CTaskbarList;

    // Current Windows 11 shells can expose the ITaskbarList4 IID without returning
    // ITaskbarList3 from QueryInterface. The methods used here occupy the same
    // inherited vtable slots on both interfaces.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C43DC798-95D1-4BEA-9030-BB99E2983A1A")]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, TaskbarProgressState state);
    }
}
