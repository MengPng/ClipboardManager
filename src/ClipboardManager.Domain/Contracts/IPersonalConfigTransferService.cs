using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IPersonalConfigTransferService
{
    Task<PersonalConfigExportResult> ExportAsync(
        Guid personalWorkspaceId,
        string packagePath,
        CancellationToken cancellationToken = default);

    Task<PersonalConfigImportPreview> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default);

    Task<PersonalConfigImportResult> ImportAsync(
        Guid personalWorkspaceId,
        string packagePath,
        CancellationToken cancellationToken = default);
}
