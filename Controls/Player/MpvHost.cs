using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows;

namespace StremioVideoCenterControls.Player
{
    // Native child window mpv embeds into via --wid. Unchanged from the
    // previous known-good implementation - only moved out of MpvPlayer.xaml.cs
    // into its own file so PlayerSession can hold a reference to it without
    // MpvPlayer.xaml.cs owning every concern.
    public sealed class MpvHost : HwndHost
    {
        private IntPtr _handle;

        public IntPtr WindowHandle
        {
            get { return _handle; }
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _handle = NativeMethods.CreateWindowEx(
                0, "STATIC", string.Empty,
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE |
                NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                0, 0, 1, 1, hwndParent.Handle,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Unable to create the MPV host window.");

            return new HandleRef(this, _handle);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            SynchronizeNativeWindow();
        }

        public async Task<bool> WaitForAndAttachMpvWindowAsync(int mpvProcessId, int timeoutMilliseconds)
        {
            if (_handle == IntPtr.Zero || mpvProcessId <= 0)
                return false;

            int elapsed = 0;
            while (elapsed < timeoutMilliseconds)
            {
                IntPtr mpvWindow = FindMpvChildWindow(mpvProcessId);
                if (mpvWindow != IntPtr.Zero)
                {
                    AttachMpvChildWindow(mpvWindow);
                    SynchronizeNativeWindow();
                    return true;
                }

                await Task.Delay(50).ConfigureAwait(true);
                elapsed += 50;
            }

            return false;
        }

        private IntPtr FindMpvChildWindow(int mpvProcessId)
        {
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumChildWindows(_handle,
                delegate (IntPtr child, IntPtr parameter)
                {
                    int processId;
                    NativeMethods.GetWindowThreadProcessId(child, out processId);
                    if (processId == mpvProcessId)
                    {
                        found = child;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            return found;
        }

        private void AttachMpvChildWindow(IntPtr child)
        {
            if (child == IntPtr.Zero || _handle == IntPtr.Zero)
                return;

            IntPtr style = NativeMethods.GetWindowLongPtr(child, NativeMethods.GWL_STYLE);
            long styleValue = style.ToInt64();
            styleValue &= ~((long)NativeMethods.WS_POPUP);
            styleValue |= NativeMethods.WS_CHILD | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN;
            NativeMethods.SetWindowLongPtr(child, NativeMethods.GWL_STYLE, new IntPtr(styleValue));

            IntPtr parent = NativeMethods.GetParent(child);
            if (parent != _handle)
                NativeMethods.SetParent(child, _handle);

            NativeMethods.ShowWindow(child, NativeMethods.SW_SHOW);
            NativeMethods.BringWindowToTop(child);
        }

        public void SynchronizeNativeWindow()
        {
            if (_handle == IntPtr.Zero)
                return;

            int width = Math.Max(1, (int)Math.Round(ActualWidth));
            int height = Math.Max(1, (int)Math.Round(ActualHeight));

            NativeMethods.SetWindowPos(_handle, IntPtr.Zero, 0, 0, width, height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);

            NativeMethods.EnumChildWindows(_handle,
                delegate (IntPtr child, IntPtr parameter)
                {
                    NativeMethods.SetWindowPos(child, IntPtr.Zero, 0, 0, width, height,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
                    return true;
                }, IntPtr.Zero);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (hwnd.Handle != IntPtr.Zero)
                NativeMethods.DestroyWindow(hwnd.Handle);
            _handle = IntPtr.Zero;
        }

        private static class NativeMethods
        {
            public const int WS_CHILD = 0x40000000;
            public const int WS_VISIBLE = 0x10000000;
            public const int WS_CLIPSIBLINGS = 0x04000000;
            public const int WS_CLIPCHILDREN = 0x02000000;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_SHOWWINDOW = 0x0040;
            public const int GWL_STYLE = -16;
            public const long WS_POPUP = unchecked((long)0x80000000);
            public const int SW_SHOW = 5;

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
                int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
                IntPtr instance, IntPtr param);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DestroyWindow(IntPtr handle);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                int x, int y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetParent(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
            private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
            private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
            private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
            private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

            public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
            {
                if (IntPtr.Size == 8)
                    return GetWindowLongPtr64(hWnd, nIndex);

                return new IntPtr(GetWindowLong32(hWnd, nIndex));
            }

            public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
            {
                if (IntPtr.Size == 8)
                    return SetWindowLongPtr64(hWnd, nIndex, value);

                return new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));
            }

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool BringWindowToTop(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

            public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
        }
    }
}
