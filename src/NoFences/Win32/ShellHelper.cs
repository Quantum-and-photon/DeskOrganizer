using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskOrganizer.NoFences.Win32
{
    /// <summary>
    /// 通过 ShellExecute 的 verb 机制执行常用 Shell 操作。
    /// 不依赖 IContextMenu COM 接口，避免 COM 消息循环问题。
    /// </summary>
    internal static class ShellHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ShellExecuteExW(ref SHELLEXECUTEINFOW lpExecInfo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFOW
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
        private const int SW_SHOWNORMAL = 1;

        public static bool OpenFile(string filePath)
        {
            return ExecuteVerb("open", filePath);
        }

        public static bool RunAsAdmin(string filePath)
        {
            return ExecuteVerb("runas", filePath);
        }

        public static bool ShowProperties(string filePath)
        {
            return ExecuteVerb("properties", filePath);
        }

        public static bool OpenWith(string filePath)
        {
            return ExecuteVerb("openas", filePath);
        }

        public static bool DeleteToRecycleBin(string filePath, IntPtr hwnd)
        {
            var shFileOp = new SHFILEOPSTRUCTW
            {
                hwnd = hwnd,
                wFunc = FO_DELETE,
                pFrom = filePath + '\0' + '\0',
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null
            };

            int result = SHFileOperationW(ref shFileOp);
            return result == 0;
        }

        private const uint FO_DELETE = 0x0003;
        private const uint FOF_ALLOWUNDO = 0x0040;
        private const uint FOF_NOCONFIRMATION = 0x0010;
        private const uint FOF_SILENT = 0x0004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCTW
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public uint fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

        private static bool ExecuteVerb(string verb, string filePath)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                hwnd = IntPtr.Zero,
                lpVerb = verb,
                lpFile = filePath,
                lpParameters = null,
                lpDirectory = null,
                nShow = SW_SHOWNORMAL,
                hInstApp = IntPtr.Zero,
                lpIDList = IntPtr.Zero,
                lpClass = null,
                hkeyClass = IntPtr.Zero,
                dwHotKey = 0,
                hIcon = IntPtr.Zero,
                hProcess = IntPtr.Zero
            };

            int hr = ShellExecuteExW(ref info);
            return hr > 32; // ShellExecute 返回 > 32 表示成功
        }
    }
}