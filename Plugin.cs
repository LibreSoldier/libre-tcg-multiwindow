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
    // The game's own code never minimizes; Unity does it when it hears that the app lost
    // the focus. This plugin sits in front of the window's message handler and:
    //  1. doesn't pass that "app deactivated" notice on to Unity (input still gets its
    //     own focus messages), and
    //  2. if the window gets minimized anyway, puts it back without taking the focus.
    // A minimize the player asks for (the button, the taskbar, a shortcut) is left alone.
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "libre.multiwindow";
        public const string Name = "Libre's Multi Window";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        private ConfigEntry<bool> hideFocusLoss, restore, runInBackground;

        private void Awake()
        {
            Log = Logger;
            hideFocusLoss = Config.Bind("General", "HideFocusLoss", true,
                "Don't tell Unity that the game lost the focus, which is what makes it minimize in full screen.");
            restore = Config.Bind("General", "RestoreWhenMinimized", true,
                "If the window gets minimized without you asking, put it back (without taking the focus).");
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
                if (!Window.Hook(hideFocusLoss.Value, restore.Value))
                    Log.LogWarning("Game window not found; the window will still minimize");
                else
                    Log.LogInfo($"{Name} {Version} loaded (hide focus loss: {hideFocusLoss.Value}, " +
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
        private const uint WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006, WM_ACTIVATEAPP = 0x001C,
                           WM_SYSCOMMAND = 0x0112, WM_APP_RESTORE = 0x8000 + 0x5356;
        private const int SIZE_MINIMIZED = 1, SC_MINIMIZE = 0xF020, SW_SHOWNOACTIVATE = 4;

        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private static IntPtr hwnd, original;
        private static WndProc hook;   // kept referenced: native code calls it
        private static bool hideFocusLoss, restore;
        private static bool userMinimized;
        private static int restores;
        private static float restoreWindowStart;

        internal static bool Hook(bool hideFocusLoss, bool restore)
        {
            Window.hideFocusLoss = hideFocusLoss;
            Window.restore = restore;
            hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero)
                return false;
            hook = Handle;
            original = SetWndProc(hwnd, Marshal.GetFunctionPointerForDelegate(hook));
            if (original == IntPtr.Zero)
                throw new InvalidOperationException($"SetWindowLong failed ({Marshal.GetLastWin32Error()})");
            return true;
        }

        private static IntPtr Handle(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_ACTIVATEAPP:
                        if (wParam == IntPtr.Zero && hideFocusLoss)
                            return IntPtr.Zero;   // "deactivated": Unity never hears it
                        break;
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
            return CallWindowProcW(original, h, msg, wParam, lParam);
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
}
