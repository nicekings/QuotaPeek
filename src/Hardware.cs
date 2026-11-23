using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NhipPC
{
internal class Volume
{
    public string Name;
    public long Total, Free;
    public double Used
    {
        get {
            return 100d * (Total - Free) / Total;
        }
    }
}
internal class HardwareFrame
{
    public double? Cpu, Ram, Receive, Send, Read, Write;
    public ulong TotalMemory, UsedMemory;
    public string Adapter;
    public List<Volume> Volumes = new List<Volume>();
}
internal sealed class HardwareSampler : IDisposable
{
    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    struct Memory
    {
        public uint Length, Load;
        public ulong Total, Free, Page, PageFree, Virtual, VirtualFree, Ex;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern bool GlobalMemoryStatusEx(ref Memory memory);
    [DllImport("iphlpapi.dll")]
    static extern int GetBestInterface(uint destination, out int index);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQuery(string source, UIntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type,
                                                   out CounterValue value);
    [StructLayout(LayoutKind.Explicit)]
    struct CounterValue
    {
        [FieldOffset(0)]
        public uint Status;
        [FieldOffset(8)]
        public double Number;
    }
    long previousCpu, previousIdle, netTick, rx, tx;
    bool cpuReady, netReady;
    NetworkInterface adapter;
    long adapterCheck;
    string selection;
    IntPtr query, reads, writes;

    public HardwareFrame Sample(Preferences p)
    {
        var f = new HardwareFrame();
        if (p.On("cpu"))
        {
            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                long total = kernel + user;
                if (cpuReady && total > previousCpu)
                    f.Cpu = Math.Max(
                        0, Math.Min(100, 100d * (1 - (double)(idle - previousIdle) / (total - previousCpu))));
                previousCpu = total;
                previousIdle = idle;
                cpuReady = true;
            }
        }
        else
            cpuReady = false;
        if (p.On("ram"))
        {
            var m = new Memory { Length = (uint)Marshal.SizeOf(typeof(Memory)) };
            if (GlobalMemoryStatusEx(ref m) && m.Total > 0)
            {
                f.TotalMemory = m.Total;
                f.UsedMemory = m.Total - m.Free;
                f.Ram = 100d * f.UsedMemory / m.Total;
            }
        }
        if (p.On("disk"))
            f.Volumes = GetVolumes();
        if (p.On("net"))
            ReadNetwork(f, p.NetworkId);
        else
        {
            netReady = false;
            adapterCheck = 0;
        }
        if (p.On("io"))
            ReadDiskRates(f);
        else
            CloseQuery();
        return f;
    }
    static List<Volume> GetVolumes()
    {
        var list = new List<Volume>();
        foreach (var d in DriveInfo.GetDrives())
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
                    list.Add(new Volume { Name = d.Name.TrimEnd('\\'), Total = d.TotalSize,
                                          Free = d.TotalFreeSpace });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        return list;
    }
    public static List<NetworkInterface> Adapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .ToList();
    }
    static NetworkInterface Choose(string id)
    {
        var options = Adapters();
        if (!String.IsNullOrEmpty(id))
            return options.Find(n => n.Id == id);
        int index;
        if (GetBestInterface(0x01010101, out index) == 0)
            foreach (var n in options)
                try
                {
                    var v = n.GetIPProperties().GetIPv4Properties();
                    if (v != null && v.Index == index)
                        return n;
                }
                catch (NetworkInformationException)
                {
                }
        return options.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Count > 0) ??
               options.FirstOrDefault();
    }
    void ReadNetwork(HardwareFrame f, string id)
    {
        try
        {
            if (Fmt.Now >= adapterCheck || selection != id)
            {
                var next = Choose(id);
                if (next == null || adapter == null || next.Id != adapter.Id)
                    netReady = false;
                adapter = next;
                selection = id;
                adapterCheck = Fmt.Now + 5;
            }
            if (adapter == null)
                return;
            var stats = adapter.GetIPStatistics();
            long tick = Stopwatch.GetTimestamp();
            double seconds = (tick - netTick) / (double)Stopwatch.Frequency;
            if (netReady && seconds > 0 && seconds < 10 && stats.BytesReceived >= rx && stats.BytesSent >= tx)
            {
                f.Receive = (stats.BytesReceived - rx) / seconds;
                f.Send = (stats.BytesSent - tx) / seconds;
            }
            f.Adapter = adapter.Name;
            rx = stats.BytesReceived;
            tx = stats.BytesSent;
            netTick = tick;
            netReady = true;
        }
        catch (NetworkInformationException)
        {
            netReady = false;
            adapterCheck = 0;
        }
    }
    static double? Counter(IntPtr handle)
    {
        CounterValue v;
        return handle != IntPtr.Zero && PdhGetFormattedCounterValue(handle, 0x200, IntPtr.Zero, out v) == 0 &&
                       v.Status <= 1 && v.Number >= 0
                   ? (double?)v.Number
                   : null;
    }
    void ReadDiskRates(HardwareFrame f)
    {
        if (query == IntPtr.Zero)
        {
            if (PdhOpenQuery(null, UIntPtr.Zero, out query) != 0)
                return;
            PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", UIntPtr.Zero, out reads);
            PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", UIntPtr.Zero,
                                 out writes);
            PdhCollectQueryData(query);
            return;
        }
        if (PdhCollectQueryData(query) == 0)
        {
            f.Read = Counter(reads);
            f.Write = Counter(writes);
        }
    }
    void CloseQuery()
    {
        if (query != IntPtr.Zero)
            PdhCloseQuery(query);
        query = reads = writes = IntPtr.Zero;
    }
    public void Dispose()
    {
        CloseQuery();
    }
}
}
