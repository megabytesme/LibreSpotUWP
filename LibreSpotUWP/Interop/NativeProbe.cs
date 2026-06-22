using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreSpotUWP.Services;

namespace LibreSpotUWP.Interop
{
    public static class NativeProbe
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadPackagedLibrary(string fileName, uint reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        public static IntPtr TryLoadLibreSpot()
        {
            const string dllName = "librespot.dll";
            IntPtr handle = LoadPackagedLibrary(dllName, 0);
            int error = Marshal.GetLastWin32Error();
            LogService.Info($"LoadPackagedLibrary('{dllName}') success={handle != IntPtr.Zero}, getLastError={error}.");
            return handle;
        }

        public static void TryFree(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;

            bool ok = FreeLibrary(handle);
            int error = Marshal.GetLastWin32Error();
            LogService.Info($"FreeLibrary success={ok}, getLastError={error}.");
        }
    }
}
