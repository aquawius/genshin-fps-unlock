﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace unlockfps_nc.Utility
{
    internal class ProcessUtils
    {
        public static string GetProcessPathFromPid(uint pid, out IntPtr processHandle)
        {
            var hProcess = Native.OpenProcess(
                ProcessAccess.QUERY_LIMITED_INFORMATION |
                ProcessAccess.TERMINATE |
                StandardAccess.SYNCHRONIZE, false, pid);

            processHandle = hProcess;

            if (hProcess == IntPtr.Zero)
                return string.Empty;

            StringBuilder sb = new StringBuilder(1024);
            uint bufferSize = (uint)sb.Capacity;
            if (!Native.QueryFullProcessImageName(hProcess, 0, sb, ref bufferSize))
                return string.Empty;

            return sb.ToString();
        }

        public static bool InjectDlls(IntPtr processHandle, List<string> dllPaths)
        {
            if (dllPaths.Count == 0)
                return true;

            Native.RtlAdjustPrivilege(20, true, false, out var _);

            var kernel32 = Native.LoadLibrary("kernel32.dll");
            var loadLibrary = Native.GetProcAddress(kernel32, "LoadLibraryW");

            var remoteVa = Native.VirtualAllocEx(processHandle, IntPtr.Zero, 0x1000,
                AllocationType.COMMIT | AllocationType.RESERVE, MemoryProtection.READWRITE);
            if (remoteVa == IntPtr.Zero)
                return false;

            foreach (var dllPath in dllPaths)
            {
                var nativeString = Marshal.StringToHGlobalUni(dllPath);
                var bytes = Encoding.Unicode.GetBytes(dllPath);
                Marshal.FreeHGlobal(nativeString);

                if (!Native.WriteProcessMemory(processHandle, remoteVa, bytes, bytes.Length, out var bytesWritten))
                    return false;

                var thread = Native.CreateRemoteThread(processHandle, IntPtr.Zero, 0, loadLibrary, remoteVa, 0, out var threadId);
                if (thread == IntPtr.Zero)
                    return false;

                Native.WaitForSingleObject(thread, uint.MaxValue);
                Native.CloseHandle(thread);
                Native.WriteProcessMemory(processHandle, remoteVa, new byte[bytes.Length], bytes.Length, out _);
            }

            Native.VirtualFreeEx(processHandle, remoteVa, 0, FreeType.RELEASE);

            return true;
        }

        public static unsafe IntPtr PatternScan(IntPtr module, string signature)
        {
            var tokens = signature.Split(' ');
            var patternBytes = tokens
                .Select(x => x == "?" ? (byte)0xFF : Convert.ToByte(x, 16))
                .ToArray();
            var maskBytes = tokens
                .Select(x => x == "?")
                .ToArray();

            var dosHeader = Marshal.PtrToStructure<IMAGE_DOS_HEADER>(module);
            var ntHeader = Marshal.PtrToStructure<IMAGE_NT_HEADERS>((IntPtr)(module.ToInt64() + dosHeader.e_lfanew));

            var sizeOfImage = ntHeader.OptionalHeader.SizeOfImage;
            var scanBytes = (byte*)module;

            var s = patternBytes.Length;
            var d = patternBytes;

            if (Native.IsWine())
            {
                /*
                 *  Fixes a problem with LoadLibraryEx not working properly on Wine.
                 *  When the flag 'LOAD_LIBRARY_AS_IMAGE_RESOURCE' is used, it is supposed to map the entire file as READONLY.
                 *  But Wine maps each section with the respective protection, and if there is a section with no read permission, it will trigger Access Violation.
                */
                Native.VirtualProtect(module, sizeOfImage, MemoryProtection.EXECUTE_READWRITE, out _);
            }

            for (var i = 0U; i < sizeOfImage - s; i++)
            {
                var found = true;
                for (var j = 0; j < s; j++)
                {
                    if (d[j] != scanBytes[i + j] && !maskBytes[j])
                    {
                        found = false;
                        break;
                    }
                }

                if (found)
                    return (IntPtr)(module.ToInt64() + i);
            }

            return IntPtr.Zero;
        }

        public static IntPtr GetModuleBase(IntPtr hProcess, string moduleName)
        {
            var moduleNameLower = moduleName.ToLowerInvariant();
            var modules = new IntPtr[1024];

            if (!Native.EnumProcessModulesEx(hProcess, modules, (uint)(modules.Length * IntPtr.Size), out var bytesNeeded, 2))
            {
                var errorCode = Marshal.GetLastWin32Error();
                if (errorCode != 299)
                {
                    MessageBox.Show($@"EnumProcessModulesEx failed ({errorCode}){Environment.NewLine}{Marshal.GetLastPInvokeErrorMessage()}"
                        , @"Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return IntPtr.Zero;
                }
            }

            foreach (var module in modules.Where(x => x != IntPtr.Zero))
            {
                StringBuilder sb = new StringBuilder(1024);
                if (Native.GetModuleBaseName(hProcess, module, sb, (uint)sb.Capacity) == 0)
                    continue;

                if (sb.ToString().ToLowerInvariant() != moduleNameLower)
                    continue;

                if (!Native.GetModuleInformation(hProcess, module, out var moduleInfo, (uint)Marshal.SizeOf<MODULEINFO>()))
                    continue;

                return moduleInfo.lpBaseOfDll;
            }

            return IntPtr.Zero;
        }

    }
}
