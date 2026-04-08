using SmartInvoice.Core.Domain;

namespace SmartInvoice.Infrastructure.Services.Pdf;

public interface IInvoiceXmlLocator
{
    Task<(string? XmlContent, string? XmlPath)> TryReadInvoiceXmlAsync(
        string companyRoot,
        Invoice invoice,
        CancellationToken cancellationToken);
}

/// <summary>
/// Locator chịu trách nhiệm tìm XML trong storage theo chuẩn mới, đồng thời fallback legacy để tương thích dữ liệu cũ.
/// </summary>
public sealed class InvoiceXmlLocator : IInvoiceXmlLocator
{
    private readonly IInvoiceXmlFileNamingStrategy _xmlNamingStrategy;

    public InvoiceXmlLocator(IInvoiceXmlFileNamingStrategy xmlNamingStrategy)
    {
        _xmlNamingStrategy = xmlNamingStrategy ?? throw new ArgumentNullException(nameof(xmlNamingStrategy));
    }

    public async Task<(string? XmlContent, string? XmlPath)> TryReadInvoiceXmlAsync(
        string companyRoot,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        var candidateBaseNames = GetCandidateBaseNames(invoice);
        var monthFolder = InvoiceFileStoragePathHelper.GetMonthYearPath(companyRoot, invoice.NgayLap);
        string? xmlPath = null;

        foreach (var baseName in candidateBaseNames)
        {
            var destDir = Path.Combine(monthFolder, baseName);
            if (Directory.Exists(destDir))
                xmlPath = Directory.EnumerateFiles(destDir, "*.xml", SearchOption.AllDirectories).FirstOrDefault();

            if (xmlPath == null)
            {
                var rawXmlPath = Path.Combine(monthFolder, baseName + ".xml");
                if (File.Exists(rawXmlPath))
                    xmlPath = rawXmlPath;
            }

            if (xmlPath != null)
                break;
        }

        if (xmlPath == null)
        {
            try
            {
                if (Directory.Exists(companyRoot))
                {
                    foreach (var baseName in candidateBaseNames)
                    {
                        var pattern = baseName + "*.xml";
                        xmlPath = Directory.GetFiles(companyRoot, pattern, SearchOption.AllDirectories).FirstOrDefault();
                        if (xmlPath != null)
                            break;
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        if (xmlPath == null || !File.Exists(xmlPath))
            return (null, null);

        var xml = await File.ReadAllTextAsync(xmlPath, cancellationToken).ConfigureAwait(false);
        return (xml, xmlPath);
    }

    private List<string> GetCandidateBaseNames(Invoice invoice)
    {
        var candidateBaseNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(invoice.XmlBaseName))
            candidateBaseNames.Add(invoice.XmlBaseName.Trim());

        var standardizedName = _xmlNamingStrategy.BuildBaseName(invoice);
        if (!candidateBaseNames.Contains(standardizedName, StringComparer.OrdinalIgnoreCase))
            candidateBaseNames.Add(standardizedName);

        var legacyBaseName = GetLegacyBaseName(invoice);
        if (!candidateBaseNames.Contains(legacyBaseName, StringComparer.OrdinalIgnoreCase))
            candidateBaseNames.Add(legacyBaseName);

        return candidateBaseNames;
    }

    private static string GetLegacyBaseName(Invoice inv)
    {
        var kh = inv.KyHieu ?? "";
        kh = InvoiceFileStoragePathHelper.SanitizeFileName(kh);
        return $"{kh}-{inv.SoHoaDon}";
    }
}
