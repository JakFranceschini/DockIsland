using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DockIsland;

// Ejeta um pendrive (volume removível): descarrega o cache, trava, desmonta e pede a ejeção da mídia.
// Se algum programa estiver usando o pendrive, a trava falha e o resultado é "Busy".
static class UsbEject
{
    public enum Result { Ok, Busy, Failed }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020;
    const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804, IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
    const int ERROR_SHARING_VIOLATION = 32;
    static readonly IntPtr Invalid = new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint ret, IntPtr ov);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControlBuf(IntPtr h, uint code, byte[] inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint ret, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FlushFileBuffers(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    // root: "E:\"
    public static Result Eject(string root)
    {
        if (string.IsNullOrEmpty(root)) return Result.Failed;
        string path = @"\\.\" + root.Substring(0, 1) + ":";

        IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == Invalid || h == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == ERROR_SHARING_VIOLATION ? Result.Busy : Result.Failed;

        try
        {
            uint ret;
            FlushFileBuffers(h);

            bool locked = false;
            for (int i = 0; i < 5 && !locked; i++)
            {
                locked = DeviceIoControl(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                if (!locked) Thread.Sleep(300);
            }
            if (!locked) return Result.Busy;   // arquivo aberto em algum programa

            if (!DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero))
                return Result.Failed;

            // libera a trava de remoção e pede a ejeção; muitos pendrives recusam o último passo,
            // mas o volume já está sincronizado e desmontado (seguro para remover)
            DeviceIoControlBuf(h, IOCTL_STORAGE_MEDIA_REMOVAL, new byte[] { 0 }, 1, IntPtr.Zero, 0, out ret, IntPtr.Zero);
            DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
            return Result.Ok;
        }
        finally { CloseHandle(h); }
    }
}
