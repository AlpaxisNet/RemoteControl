using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl.Protocol.Session;

/// <summary>
/// The PC-id list sent at the end of the RC login (RClib <c>generatePcidList</c>): <c>"win,&lt;product&gt;,&lt;network&gt;,&lt;disk&gt;"</c> on
/// Windows, <c>"linux,&lt;network&gt;,&lt;system&gt;,"</c> on Linux and <c>"mac,&lt;network&gt;,&lt;system&gt;,"</c> on macOS. The MD5 of
/// nothing is <c>""</c>, so a missing value leaves an empty field.
/// </summary>
public static class PcIdList
{
    /// <summary>Builds the list for this machine. Never throws; a value that cannot be read is left empty.</summary>
    /// <returns>The list.</returns>
    public static string Generate()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return "win," + Md5Reversed(WindowsProductId()) + "," + Md5Reversed(FirstMacWithAddress()) + "," + Md5Reversed(VolumeSerial());
            if (OperatingSystem.IsMacOS()) return "mac," + Md5Hex(FirstMacWithAddress()) + "," + Md5Hex(MachineIdBytes()) + ",";
            return "linux," + Md5Hex(InterfaceMac("eth0")) + "," + Md5Hex(MachineIdBytes()) + ",";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NetworkInformationException or PlatformNotSupportedException
                                              or DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException)
        {
            return "linux,,,";
        }
    }

    /// <summary>MD5 as lower-case hex; the MD5 of nothing is <c>""</c>.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The digest text.</returns>
    public static string Md5Hex(byte[] data) => data.Length == 0 ? string.Empty : Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    /// <summary>MD5 with each byte printed low nibble first (RClib <c>md5RevHex</c>); the MD5 of nothing is <c>""</c>.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The digest text.</returns>
    public static string Md5Reversed(byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        const string hex = "0123456789abcdef";
        var text = new StringBuilder(32);
        foreach (byte value in MD5.HashData(data)) text.Append(hex[value & 0x0F]).Append(hex[value >> 4]);
        return text.ToString();
    }

    private static byte[] InterfaceMac(string name)
    {
        foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(item.Name, name, StringComparison.Ordinal)) continue;
            byte[] mac = item.GetPhysicalAddress().GetAddressBytes();
            return mac.Length >= 6 ? mac[..6] : Array.Empty<byte>();
        }

        return Array.Empty<byte>();
    }

    private static byte[] FirstMacWithAddress()
    {
        foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
        {
            byte[] mac = item.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length < 6 || mac.All(value => value == 0)) continue;
            bool hasAddress = item.GetIPProperties().UnicastAddresses.Any(address =>
                address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address.Address)
                && !address.Address.Equals(System.Net.IPAddress.Any));
            if (hasAddress) return mac[..6];
        }

        return Array.Empty<byte>();
    }

    private static byte[] MachineIdBytes()
    {
        foreach (string path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (!File.Exists(path)) continue;
            string value = File.ReadAllText(path).Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
            if (value.Length > 0) return Encoding.ASCII.GetBytes(value);
        }

        return Array.Empty<byte>();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, IntPtr volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, IntPtr fileSystemNameBuffer, int fileSystemNameSize);

    /// <summary>The serial number of drive C:, four bytes little-endian (as RClib), or nothing.</summary>
    private static byte[] VolumeSerial()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<byte>();
        return GetVolumeInformation(@"C:\", IntPtr.Zero, 0, out uint serial, out _, out _, IntPtr.Zero, 0) ? BitConverter.GetBytes(serial) : Array.Empty<byte>();
    }

    private static byte[] WindowsProductId()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<byte>();
        foreach (string path in new[] { @"Software\Microsoft\Windows\CurrentVersion", @"Software\Microsoft\Windows NT\CurrentVersion" })
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue("DigitalProductId") is byte[] { Length: > 0 } value) return value;
        }

        return Array.Empty<byte>();
    }
}
