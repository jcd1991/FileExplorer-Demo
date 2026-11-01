using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TestProject.Services;

// Native metadata stays here: callers never infer a regular file from “not a directory.”
internal static class FileSystemMetadata
{
    internal readonly record struct Identity(ulong Volume, ulong Low, ulong High = 0);
    internal readonly record struct Entry(bool IsDirectory, bool IsRegularFile, Identity Identity);
    private const int TypeMask = 0xF000;
    private const int RegularFile = 0x8000;
    private const int DirectoryType = 0x4000;
    private const uint ShareReadWriteDelete = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileIdInfo = 18;

    internal static Entry Read(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (LStat(path, out var status) != 0) throw NativeFailure();
            return new((status.Mode & TypeMask) == DirectoryType, (status.Mode & TypeMask) == RegularFile,
                new(unchecked((ulong)status.Device), unchecked((ulong)status.Inode)));
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return default;
        using var handle = CreateFile(path, 0, ShareReadWriteDelete, IntPtr.Zero, OpenExisting,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileId(handle, FileIdInfo, out var id, Marshal.SizeOf<WindowsFileId>())) throw NativeFailure();
        return new((attributes & FileAttributes.Directory) != 0, (attributes & FileAttributes.Directory) == 0,
            new(id.Volume, id.Low, id.High));
    }

    internal static FileStream OpenRegularRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RequireRegular(Read(path));
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        }

        // O_NONBLOCK prevents FIFO open from waiting for a writer even if the path changed
        // after preflight. O_NOFOLLOW rejects a replaced final symlink; O_CLOEXEC owns the fd.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new FileBrowserException(501, "Unsupported platform", "Checked file reads require Windows, macOS, or Linux.");
        const int macNonBlocking = 0x4, macNoFollow = 0x100, macCloseOnExec = 0x1000000;
        const int linuxNonBlocking = 0x800, linuxNoFollow = 0x20000, linuxCloseOnExec = 0x80000;
        var flags = OperatingSystem.IsMacOS()
            ? macNonBlocking | macNoFollow | macCloseOnExec
            : linuxNonBlocking | linuxNoFollow | linuxCloseOnExec;
        var descriptor = Open(path, flags);
        if (descriptor < 0) throw NativeFailure();
        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            if (FStat(handle, out var status) != 0) throw NativeFailure();
            RequireRegular(new(false, (status.Mode & TypeMask) == RegularFile, default));
            // A native descriptor is not marked as a .NET async handle. ReadAsync still
            // supports cancellation between bounded reads using FileStream's fallback.
            return new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void RequireRegular(Entry entry)
    {
        if (!entry.IsRegularFile) throw new FileBrowserException(403, "Unsupported entry", "Only regular files can be read or copied.");
    }

    private static Exception NativeFailure()
    {
        var error = Marshal.GetLastPInvokeError();
        var denied = error == (OperatingSystem.IsWindows() ? 5 : 13);
        return denied
            ? new UnauthorizedAccessException("The entry cannot be inspected or opened safely.")
            : new IOException("The entry cannot be inspected or opened safely.");
    }

    // Fixed-width .NET 8 System.Native FileStatus ABI, not an OS-specific struct stat.
    // Layout reference: https://github.com/dotnet/runtime/blob/v8.0.0/src/native/libs/System.Native/pal_io.h
    [StructLayout(LayoutKind.Sequential)]
    private struct UnixStatus
    {
        public int Flags, Mode;
        public uint User, Group;
        public long Size, AccessTime, AccessNanos, ModifiedTime, ModifiedNanos;
        public long ChangedTime, ChangedNanos, BirthTime, BirthNanos, Device, RawDevice, Inode;
        public uint UserFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileId { public ulong Volume, Low, High; }

    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true,
        CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixStatus status);

    [DllImport("System.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle handle, out UnixStatus status);

    [DllImport("libc", EntryPoint = "open", SetLastError = true,
        CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileId(SafeFileHandle handle, int infoClass, out WindowsFileId id, int size);
}
