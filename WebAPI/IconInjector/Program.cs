using System;
using System.IO;
using System.Runtime.InteropServices;

public class IconInjector
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr BeginUpdateResource(string pFileName, [MarshalAs(UnmanagedType.Bool)]bool bDeleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateResource(IntPtr hUpdate, uint lpType, ushort lpName, ushort wLanguage, byte[] lpData, uint cbData);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EndUpdateResource(IntPtr hUpdate, [MarshalAs(UnmanagedType.Bool)]bool fDiscard);

    [DllImport("kernel32.dll")]
    static extern uint GetLastError();

    const uint RT_ICON = 3;
    const uint RT_GROUP_ICON = 14;

    public static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: IconInjector <exePath> <iconPath>");
            return;
        }

        string exePath = args[0];
        string iconPath = args[1];

        try
        {
            UpdateIcon(exePath, iconPath);
            Console.WriteLine("Icon updated successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
        }
    }

    public static void UpdateIcon(string exePath, string iconPath)
    {
        byte[] iconData = File.ReadAllBytes(iconPath);
        
        IntPtr hUpdate = BeginUpdateResource(exePath, false);
        if (hUpdate == IntPtr.Zero)
        {
            uint error = GetLastError();
            throw new Exception("Cannot open exe file, error code: " + error);
        }

        try
        {
            bool result = UpdateResource(hUpdate, RT_GROUP_ICON, 1, 0, iconData, (uint)iconData.Length);
            if (!result)
            {
                uint error = GetLastError();
                throw new Exception("Update icon group failed, error code: " + error);
            }

            result = UpdateResource(hUpdate, RT_ICON, 1, 0, iconData, (uint)iconData.Length);
            if (!result)
            {
                uint error = GetLastError();
                throw new Exception("Update icon failed, error code: " + error);
            }
        }
        finally
        {
            if (!EndUpdateResource(hUpdate, false))
            {
                uint error = GetLastError();
                Console.WriteLine("Warning: Error ending update, code: " + error);
            }
        }
    }
}
