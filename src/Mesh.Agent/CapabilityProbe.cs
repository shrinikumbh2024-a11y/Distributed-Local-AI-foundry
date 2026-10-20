namespace Mesh.Agent;

using Mesh.Abstractions;

/// <summary>
/// Detects the node's hardware capability using only the base class library.
/// No WMI, no P/Invoke, no external packages — keeps the agent portable and
/// trivially auditable.
/// </summary>
public static class CapabilityProbe
{
    public static NodeCapability Probe(string fabricGroup, long reservedSystemBytes, int coresOverride = 0, long ramBytesOverride = 0)
    {
        var total = ramBytesOverride > 0 ? ramBytesOverride : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var usable = Math.Max(total - reservedSystemBytes, total / 2);
        var cores = coresOverride > 0 ? coresOverride : Environment.ProcessorCount;
        var os = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "Unknown";

        return new NodeCapability(
            CpuCores: cores,
            TotalMemoryBytes: total,
            UsableMemoryBytes: usable,
            Accelerator: AcceleratorKind.None,
            AcceleratorMemoryBytes: 0,
            FabricGroup: fabricGroup,
            Os: os);
    }
}
