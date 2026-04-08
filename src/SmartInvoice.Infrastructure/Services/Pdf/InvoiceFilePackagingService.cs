using System.IO.Compression;

namespace SmartInvoice.Infrastructure.Services.Pdf;

public interface IInvoiceFilePackagingService
{
    Task<string?> CreateZipFromDirectoryAsync(string sourceDirectory, string zipDirectory, string zipNameWithoutExtension, CancellationToken cancellationToken = default);
    Task<string?> CreateZipFromFilesAsync(IReadOnlyList<string> files, string zipDirectory, string zipNameWithoutExtension, CancellationToken cancellationToken = default);
}

public sealed class InvoiceFilePackagingService : IInvoiceFilePackagingService
{
    public Task<string?> CreateZipFromDirectoryAsync(string sourceDirectory, string zipDirectory, string zipNameWithoutExtension, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return Task.FromResult<string?>(null);

        var xmlFiles = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.TopDirectoryOnly);
        if (xmlFiles.Length == 0)
            return Task.FromResult<string?>(null);

        Directory.CreateDirectory(zipDirectory);
        var zipPath = Path.Combine(zipDirectory, zipNameWithoutExtension + ".zip");
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(sourceDirectory, zipPath);
        return Task.FromResult<string?>(zipPath);
    }

    public async Task<string?> CreateZipFromFilesAsync(IReadOnlyList<string> files, string zipDirectory, string zipNameWithoutExtension, CancellationToken cancellationToken = default)
    {
        if (files == null || files.Count == 0)
            return null;

        var existingFiles = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existingFiles.Count == 0)
            return null;

        Directory.CreateDirectory(zipDirectory);
        var zipPath = Path.Combine(zipDirectory, zipNameWithoutExtension + ".zip");
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        await using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var filePath in existingFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryName = Path.GetFileName(filePath);
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fileStream = File.OpenRead(filePath);
                await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }
        }

        return zipPath;
    }
}
