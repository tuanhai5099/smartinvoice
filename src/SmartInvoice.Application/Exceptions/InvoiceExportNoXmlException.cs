namespace SmartInvoice.Application.Exceptions;

/// <summary>API export-xml trả 500 với message "Không tồn tại hồ sơ gốc của hóa đơn" — hóa đơn không có XML trên server.</summary>
public sealed class InvoiceExportNoXmlException : Exception
{
    public InvoiceExportNoXmlException(string? message = null, Exception? inner = null)
        : base(message ?? "Không tồn tại hồ sơ gốc của hóa đơn.", inner) { }
}
