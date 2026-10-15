using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Orbit.Services;

/// <summary>
/// Estatísticas da máquina (Windows) via P/Invoke — sem pacotes extras.
/// </summary>
public class SystemStats
{
    private ulong _prevIdle, _prevKernel, _prevUser;

    // Só lê o que está habilitado — cada flag desligada pula seu syscall (ReadDisk é o mais caro).
    public SystemStatsResult Read(bool cpu = true, bool ram = true, bool disk = true)
    {
        return new SystemStatsResult(
            cpu ? ReadCpu() : 0,
            ram ? ReadRam() : new RamInfo(0, 0),
            disk ? ReadDisk() : new DiskInfo(0, 0));
    }

    // ---- CPU: delta de idle vs total entre leituras ----
    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        ulong i = ToU(idle), k = ToU(kernel), u = ToU(user);

        double cpu = 0;
        if (_prevKernel != 0 || _prevUser != 0)
        {
            ulong totalDelta = (k - _prevKernel) + (u - _prevUser);
            ulong idleDelta = i - _prevIdle;
            if (totalDelta > 0)
                cpu = (1.0 - (double)idleDelta / totalDelta) * 100.0;
        }

        _prevIdle = i;
        _prevKernel = k;
        _prevUser = u;
        return Math.Clamp(cpu, 0, 100);
    }

    // ---- RAM física total/usada ----
    private static RamInfo ReadRam()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem))
            return new RamInfo(0, 0);

        double totalGb = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
        double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;
        return new RamInfo(usedGb, totalGb);
    }

    // ---- Disco do sistema (drive onde o app está) ----
    private static DiskInfo ReadDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new DriveInfo(root);
            double totalGb = d.TotalSize / 1024.0 / 1024 / 1024;
            double usedGb = (d.TotalSize - d.TotalFreeSpace) / 1024.0 / 1024 / 1024;
            return new DiskInfo(usedGb, totalGb);
        }
        catch
        {
            return new DiskInfo(0, 0);
        }
    }

    private static ulong ToU(FILETIME ft) => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

public record SystemStatsResult(double CpuPercent, RamInfo Ram, DiskInfo Disk);
public record RamInfo(double UsedGb, double TotalGb)
{
    public double Percent => TotalGb > 0 ? UsedGb / TotalGb * 100 : 0;
}
public record DiskInfo(double UsedGb, double TotalGb)
{
    public double Percent => TotalGb > 0 ? UsedGb / TotalGb * 100 : 0;
}
