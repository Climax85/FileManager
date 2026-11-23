using FSH.Modules.Files.Domain;

namespace FSH.Modules.Files.Services;

/// <summary>
/// Provider-neutral antivirus/content scanning boundary. FinalizeUpload calls this after HEAD-ing
/// the uploaded object. Implementations must return Clean only for an explicit successful scan;
/// errors and timeouts return ScanFailed so callers can quarantine or leave content pending.
/// </summary>
public interface IFileScanner
{
    ValueTask<ScanStatus> ScanAsync(string storageKey, CancellationToken cancellationToken = default);
}
