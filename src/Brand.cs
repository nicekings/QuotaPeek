using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace NhipPC
{
internal static class Brand
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
    static extern uint Extract(string path, int index, int width, int height, out IntPtr icon, out uint id,
                               uint count, uint flags);

    public static Icon Icon(int pixels)
    {
        IntPtr icon;
        uint id;
        if (Extract(Tools.Self, 0, pixels, pixels, out icon, out id, 1, 0) != 1 || icon == IntPtr.Zero)
            return (Icon)SystemIcons.Application.Clone();
        try
        {
            using (var wrapper = System.Drawing.Icon.FromHandle(icon)) return (Icon)wrapper.Clone();
        }
        finally
        {
            Theme.DestroyIcon(icon);
        }
    }
}
}
