﻿#region LICENSE

// Copyright 2014 LeagueSharp.Loader
// Injection.cs is part of LeagueSharp.Loader.
// 
// LeagueSharp.Loader is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// LeagueSharp.Loader is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with LeagueSharp.Loader. If not, see <http://www.gnu.org/licenses/>.

#endregion

using System.IO.MemoryMappedFiles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LeagueSharp.Loader.Data;

namespace LeagueSharp.Loader.Class
{
    public static class Injection
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        struct SharedMemoryLayout
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            readonly String SandboxPath;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            readonly String BootstrapPath;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            readonly String User;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            readonly String Password;

            public SharedMemoryLayout(String sandboxPath, String bootstrapPath, String user, String password)
            {
                SandboxPath = sandboxPath;
                BootstrapPath = bootstrapPath;
                User = user;
                Password = password;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate bool InjectDLLDelegate(int processId, string path);

        private static InjectDLLDelegate injectDLL;

        public delegate void OnInjectDelegate(IntPtr hwnd);

        public static event OnInjectDelegate OnInject;

        public static bool InjectedAssembliesChanged { get; set; }

        private static bool IsProcessInjected(Process leagueProcess)
        {
            if (leagueProcess != null)
            {
                try
                {
                    return
                        leagueProcess.Modules.Cast<ProcessModule>()
                            .Any(processModule => processModule.ModuleName == Path.GetFileName(Directories.CoreFilePath));
                }
                catch (Exception e)
                {
                    Utility.Log(LogStatus.Error, "Injector", string.Format("Error - {0}", e), Logs.MainLog);
                }
            }
            return false;
        }

        public static bool IsInjected
        {
            get { return LeagueProcess.Any(IsProcessInjected); }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr LoadLibrary(string dllToLoad);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(IntPtr ZeroOnly, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static string GetWindowText(IntPtr hWnd)
        {
            var size = GetWindowTextLength(hWnd);
            if (size++ > 0)
            {
                var builder = new StringBuilder(size);
                GetWindowText(hWnd, builder, builder.Capacity);
                return builder.ToString();
            }

            return String.Empty;
        }

        private static List<IntPtr> FindWindows(string title)
        {
            var windows = new List<IntPtr>();

            EnumWindows(delegate(IntPtr wnd, IntPtr param)
            {
                if (GetWindowText(wnd).Contains(title))
                {
                    windows.Add(wnd);
                }
                return true;
            }, IntPtr.Zero);

            return windows;
        }

        internal static bool IsLeagueOfLegendsFocused
        {
            get
            {
                return GetWindowText(GetForegroundWindow()).Contains("League of Legends (TM) Client");
            }
        }

        private static void ResolveInjectDLL()
        {
            try
            {
                var mmf = MemoryMappedFile.CreateOrOpen("Local\\LeagueSharpBootstrap", 260 * 2,
                    MemoryMappedFileAccess.ReadWrite);

                var sharedMem = new SharedMemoryLayout(Directories.SandboxFilePath, Directories.BootstrapFilePath, 
                    Config.Instance.Username, Config.Instance.Password);

                using (var writer = mmf.CreateViewAccessor())
                {
                    var len = Marshal.SizeOf(typeof(SharedMemoryLayout));
                    var arr = new byte[len];
                    var ptr = Marshal.AllocHGlobal(len);
                    Marshal.StructureToPtr(sharedMem, ptr, true);
                    Marshal.Copy(ptr, arr, 0, len);
                    Marshal.FreeHGlobal(ptr);
                    writer.WriteArray(0, arr, 0, arr.Length);
                }

                var hModule = LoadLibrary(Directories.BootstrapFilePath);
                if (!(hModule != IntPtr.Zero))
                {
                    return;
                }

                var procAddress = GetProcAddress(hModule, "InjectModule");
                if (!(procAddress != IntPtr.Zero))
                {
                    return;
                }

                injectDLL = Marshal.GetDelegateForFunctionPointer(procAddress, typeof(InjectDLLDelegate)) as InjectDLLDelegate;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        public static List<IntPtr> LeagueInstances
        {
            get
            {
                return FindWindows("League of Legends (TM) Client");
            }
        }

        private static List<Process> LeagueProcess
        {
            get
            {
                return Process.GetProcessesByName("League of Legends").ToList();
            }
        }

        public static void Pulse()
        {
            if (LeagueProcess == null)
            {
                return;
            }

            //Don't inject untill we checked that there are not updates for the loader.
            if (Updater.Updating || !Updater.CheckedForUpdates)
            {
                return;
            }

            foreach (var instance in LeagueProcess)
            {
                try
                {
                    Config.Instance.LeagueOfLegendsExePath = instance.Modules[0].FileName;

                    if (!IsProcessInjected(instance) && Updater.UpdateCore(instance.Modules[0].FileName, true).Item1)
                    {
                        if (injectDLL == null)
                        {
                            ResolveInjectDLL();
                        }

                        if (injectDLL != null && GetWindowText(instance.MainWindowHandle).Contains("League of Legends (TM) Client"))
                        {
                            injectDLL(instance.Id, Directories.CoreFilePath);

                            if (OnInject != null)
                            {
                                OnInject(instance.MainWindowHandle);
                            }
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }

        public static void SendLoginCredentials(IntPtr wnd, string user, string passwordHash)
        {
            var str = string.Format("LOGIN|{0}|{1}", user, passwordHash);
            var lParam = new COPYDATASTRUCT { cbData = 2, dwData = str.Length * 2 + 2, lpData = str };
            SendMessage(wnd, 74U, IntPtr.Zero, ref lParam);
        }

        public static void SendConfig(IntPtr wnd)
        {
            var str = string.Format(
                "{0}{1}{2}{3}", (Config.Instance.Settings.GameSettings[0].SelectedValue == "True") ? "1" : "0",
                (Config.Instance.Settings.GameSettings[3].SelectedValue == "True") ? "1" : "0",
                (Config.Instance.Settings.GameSettings[1].SelectedValue == "True") ? "1" : "0",
                (Config.Instance.Settings.GameSettings[2].SelectedValue == "True") ? "2" : "0");

            var lParam = new COPYDATASTRUCT { cbData = 2, dwData = str.Length * 2 + 2, lpData = str };
            SendMessage(wnd, 74U, IntPtr.Zero, ref lParam);
        }

        public struct COPYDATASTRUCT
        {
            public int cbData;
            public int dwData;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpData;
        }
    }
}