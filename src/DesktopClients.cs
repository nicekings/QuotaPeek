using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NhipPC
{
// AppsFolder includes both Start menu shortcuts and Microsoft Store applications.
internal static class DesktopClients
{
    public static Dictionary<string, string> Find()
    {
        var result = new Dictionary<string, string>();
        object shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
            folder = Member(shell, "NameSpace", "shell:AppsFolder");
            items = Member(folder, "Items");
            int count = Convert.ToInt32(Member(items, "Count"));
            for (int i = 0; i < count; i++)
            {
                object item = Member(items, "Item", i);
                if (item == null)
                    continue;
                try
                {
                    string name = Convert.ToString(Member(item, "Name")).ToLowerInvariant();
                    string id = name == "gemini" || name == "google gemini" ? "gemini" : null;
                    if (id == null)
                        continue;
                    string path = Convert.ToString(Member(item, "Path"));
                    if (String.IsNullOrWhiteSpace(path) ||
                        path.IndexOf("_crx_", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    result[id] = Path.IsPathRooted(path) ? path : @"shell:AppsFolder\" + path;
                }
                catch (COMException)
                {
                }
                catch (TargetInvocationException)
                {
                }
                finally
                {
                    Release(item);
                }
            }
        }
        catch (COMException)
        {
        }
        catch (TargetInvocationException)
        {
        }
        catch (TypeLoadException)
        {
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }
        return result;
    }
    static object Member(object obj, string name, params object[] args)
    {
        if (obj == null)
            return null;
        return obj.GetType().InvokeMember(name, BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, obj,
                                          args);
    }
    static void Release(object obj)
    {
        if (obj != null && Marshal.IsComObject(obj))
            Marshal.ReleaseComObject(obj);
    }
}
}
