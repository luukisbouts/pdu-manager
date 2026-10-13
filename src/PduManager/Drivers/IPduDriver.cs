using PduManager.Models;

namespace PduManager.Drivers;

public interface IPduDriver
{
    Task<PduSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task ControlAsync(IEnumerable<int> outlets, OutletOp op, CancellationToken ct = default);
    Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default);
    Task<List<FormField>> GetFormAsync(string page, CancellationToken ct = default);
    Task PostFormAsync(string page, IEnumerable<FormField> fields, CancellationToken ct = default);
}
