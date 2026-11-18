// Libre's Multi Window - BepInEx plugin for the PTCGO 2.95 client
// Copyright (C) 2026 LibreSoldier
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY; see the LICENSE file or
// <https://www.gnu.org/licenses/> for details.
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace LibreTcgMultiWindow
{
    // In full screen, the Unity player (2018.4) minimizes its window as soon as another
    // window gets the focus, so two game windows can't stay side by side, one per monitor.
    // The game's own code never minimizes: the player does, with user32's ShowWindow.
    //  1. The player's ShowWindow import is redirected here, and a minimize the player asks
    //     for on its own is skipped, so the window never leaves the screen (no animation,
    //     and the window stays on its monitor).
    //  2. If the window gets minimized anyway, it is put back without taking the focus.
    // A minimize the player asks for (the button, the taskbar, a shortcut) is left alone.
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "libre.multiwindow";
        public const string Name = "Libre's Multi Window";
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;
        private ConfigEntry<bool> blockMinimize, restore, runInBackground;

        private void Awake()
        {
            Log = Logger;
            blockMinimize = Config.Bind("General", "BlockMinimize", true,
                "Stop the game from minimizing itself when another window gets the focus.");
            restore = Config.Bind("General", "RestoreWhenMinimized", true,
                "If the window gets minimized anyway (without you asking), put it back without taking the focus.");
            runInBackground = Config.Bind("General", "RunInBackground", true,
                "Keep the game running and drawing while another window has the focus.");

            if (runInBackground.Value)
                Application.runInBackground = true;
            if (Application.platform != RuntimePlatform.WindowsPlayer)
            {
                Log.LogInfo($"{Name} {Version} loaded (not Windows: nothing to do)");
                return;
            }
            try
            {
                if (!Window.Hook(blockMinimize.Value, restore.Value))
                    Log.LogWarning("Game window not found; the window will still minimize");
                else
                    Log.LogInfo($"{Name} {Version} loaded (block minimize: {Window.Blocking}, " +
                                $"restore: {restore.Value}, run in background: {runInBackground.Value})");
            }
            catch (Exception e)
            {
                Log.LogError($"Could not hook the game window: {e}");
            }
        }

        // Unity can turn this back off when it changes settings; keep it on.
        private void Update()
        {
            if (runInBackground.Value && !Application.runInBackground)
                Application.runInBackground = true;
        }
    }

    internal static class Window
    {
        private const int GWL_WNDPROC = -4;
        private const uint WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006, WM_SYSCOMMAND = 0x0112,
                           WM_APP_RESTORE = 0x8000 + 0x5356;
        private const int SIZE_MINIMIZED = 1, SC_MINIMIZE = 0xF020, SW_SHOWNOACTIVATE = 4;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool ShowWindowProc(IntPtr hwnd, int cmd);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private static IntPtr hwnd, originalWndProc;
        private static WndProc wndProcHook;            // kept referenced: native code calls them
        private static ShowWindowProc showWindowHook, originalShowWindow;
        private static bool restore;
        private static volatile bool userMinimized;
        private static int restores, blocked;
        private static float restoreWindowStart;

        internal static bool Blocking => originalShowWindow != null;

        internal static bool Hook(bool blockMinimize, bool restore)
        {
            Window.restore = restore;
            hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero)
                return false;
            wndProcHook = Handle;
            originalWndProc = SetWndProc(hwnd, Marshal.GetFunctionPointerForDelegate(wndProcHook));
            if (originalWndProc == IntPtr.Zero)
                throw new InvalidOperationException($"SetWindowLong failed ({Marshal.GetLastWin32Error()})");
            if (blockMinimize)
            {
                showWindowHook = ShowWindowFilter;
                IntPtr old = Imports.Replace("UnityPlayer.dll", "user32.dll", "ShowWindow",
                                             Marshal.GetFunctionPointerForDelegate(showWindowHook));
                if (old == IntPtr.Zero)
                    Plugin.Log.LogWarning("UnityPlayer.dll's ShowWindow import not found; minimizes will only be undone");
                else
                    originalShowWindow = (ShowWindowProc)Marshal.GetDelegateForFunctionPointer(old, typeof(ShowWindowProc));
            }
            return true;
        }

        // Every ShowWindow call the Unity player makes comes through here.
        private static bool ShowWindowFilter(IntPtr h, int cmd)
        {
            // SW_SHOWMINIMIZED 2, SW_MINIMIZE 6, SW_SHOWMINNOACTIVE 7, SW_FORCEMINIMIZE 11
            bool minimize = cmd == 2 || cmd == 6 || cmd == 7 || cmd == 11;
            if (minimize && h == hwnd && !userMinimized)
            {
                if (blocked++ == 0)
                    Plugin.Log.LogInfo("The game tried to minimize itself after losing the focus; kept it on screen " +
                                       "(logged once)");
                return true;   // ShowWindow's "was visible"
            }
            return originalShowWindow(h, cmd);
        }

        private static IntPtr Handle(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_ACTIVATE:
                        if ((wParam.ToInt64() & 0xFFFF) != 0)
                            userMinimized = false;
                        break;
                    case WM_SYSCOMMAND:
                        if ((wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
                            userMinimized = true;   // the player's own minimize
                        break;
                    case WM_SIZE:
                        if (wParam.ToInt64() == SIZE_MINIMIZED)
                        {
                            if (restore && !userMinimized)
                                PostMessageW(h, WM_APP_RESTORE, IntPtr.Zero, IntPtr.Zero);
                        }
                        else
                            userMinimized = false;
                        break;
                    case WM_APP_RESTORE:
                        Restore(h);
                        return IntPtr.Zero;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Window hook: {e}");
            }
            return CallWindowProcW(originalWndProc, h, msg, wParam, lParam);
        }

        private static void Restore(IntPtr h)
        {
            if (!IsIconic(h) || userMinimized)
                return;
            // Give up instead of fighting something that keeps minimizing the window.
            float now = Time.realtimeSinceStartup;
            if (now - restoreWindowStart > 5f)
            {
                restoreWindowStart = now;
                restores = 0;
            }
            if (++restores > 5)
            {
                if (restores == 6)
                    Plugin.Log.LogWarning("The window keeps getting minimized; leaving it minimized for now");
                return;
            }
            Plugin.Log.LogInfo("The window was minimized after losing the focus; putting it back");
            ShowWindow(h, SW_SHOWNOACTIVATE);
        }

        // Unity's main window ("UnityWndClass") of this process. Two games can be open, so
        // the process id has to match, not just the class.
        private static IntPtr FindGameWindow()
        {
            uint pid = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            var name = new StringBuilder(64);
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint owner);
                if (owner != pid)
                    return true;
                name.Length = 0;
                GetClassNameW(h, name, name.Capacity);
                if (name.ToString() != "UnityWndClass")
                    return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static IntPtr SetWndProc(IntPtr h, IntPtr proc) =>
            IntPtr.Size == 8 ? SetWindowLongPtrW(h, GWL_WNDPROC, proc)
                             : new IntPtr(SetWindowLongW(h, GWL_WNDPROC, proc.ToInt32()));

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder name, int max);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLongW(IntPtr h, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProcW(IntPtr proc, IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    }

    // Redirects one function a loaded module imports from a DLL, by rewriting that module's
    // import address table (its list of resolved function addresses).
    internal static class Imports
    {
        // Returns the function the slot pointed to, or IntPtr.Zero when the import isn't there.
        internal static IntPtr Replace(string module, string fromDll, string function, IntPtr replacement)
        {
            IntPtr image = GetModuleHandleW(module);
            IntPtr target = GetProcAddress(GetModuleHandleW(fromDll), function);
            if (image == IntPtr.Zero || target == IntPtr.Zero)
                return IntPtr.Zero;
            int nt = Marshal.ReadInt32(image, 0x3C);
            if (Marshal.ReadInt32(image, nt) != 0x4550)                     // "PE\0\0"
                return IntPtr.Zero;
            int optional = nt + 24;
            bool pe32Plus = Marshal.ReadInt16(image, optional) == 0x20B;
            int importDir = optional + (pe32Plus ? 120 : 104);               // DataDirectory[1]
            int imports = Marshal.ReadInt32(image, importDir);
            if (imports == 0)
                return IntPtr.Zero;
            for (int desc = imports; ; desc += 20)                          // IMAGE_IMPORT_DESCRIPTOR
            {
                int nameRva = Marshal.ReadInt32(image, desc + 12);
                int thunks = Marshal.ReadInt32(image, desc + 16);            // FirstThunk (the IAT)
                if (nameRva == 0 || thunks == 0)
                    return IntPtr.Zero;
                string dll = Marshal.PtrToStringAnsi(Add(image, nameRva));
                if (!string.Equals(dll, fromDll, StringComparison.OrdinalIgnoreCase))
                    continue;
                for (int slot = thunks; ; slot += IntPtr.Size)
                {
                    IntPtr address = Marshal.ReadIntPtr(image, slot);
                    if (address == IntPtr.Zero)
                        break;
                    if (address != target)
                        continue;
                    IntPtr where = Add(image, slot);
                    if (!VirtualProtect(where, (UIntPtr)(uint)IntPtr.Size, 0x04, out uint old))   // PAGE_READWRITE
                        return IntPtr.Zero;
                    Marshal.WriteIntPtr(where, replacement);
                    VirtualProtect(where, (UIntPtr)(uint)IntPtr.Size, old, out _);
                    return address;
                }
            }
        }

        private static IntPtr Add(IntPtr p, int offset) => new IntPtr(p.ToInt64() + offset);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
    }
}
