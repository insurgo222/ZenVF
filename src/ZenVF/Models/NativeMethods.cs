using System.Runtime.InteropServices;

namespace ZenVF.Models;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr affinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity groupAffinity, out GroupAffinity previousGroupAffinity);

    [DllImport("kernel32.dll")]
    public static extern int GetCurrentProcessorNumber();

    [StructLayout(LayoutKind.Sequential)]
    public struct GroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
    }
}
