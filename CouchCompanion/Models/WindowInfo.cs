using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CouchCompanion.Models
{
    public class WindowInfo
    {
        public IntPtr Hwnd { get; }
        public string Title { get; }
        public Process Process { get; }

        public WindowInfo(IntPtr hwnd, string title, Process process)
        {
            Hwnd = hwnd;
            Title = title;
            Process = process;
        }

        public override string ToString()
        {
            return Title;
        }
    }
}
