using FreeAll.Core.Volumes;

namespace FreeAll.Application.Abstractions;

/// <summary>Discovers local volumes and mapped network drives (PR-001, F-007).</summary>
public interface IVolumeDiscovery
{
    Task<VolumeInventory> DiscoverAsync(CancellationToken cancellationToken);
}
