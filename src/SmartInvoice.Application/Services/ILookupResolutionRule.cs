namespace SmartInvoice.Application.Services;

/// <summary>
/// Một rule tra cứu theo NCC (hoặc nhóm key): không HTTP, chỉ dựng <see cref="InvoiceLookupSuggestion"/> từ context.
/// </summary>
public interface ILookupResolutionRule
{
    /// <summary>Ưu tiên nhỏ hơn được thử trước (EasyInvoice thường là 0).</summary>
    int Priority { get; }

    bool CanHandle(InvoiceLookupResolutionHint hint);

    InvoiceLookupSuggestion? Build(InvoiceContentContext context);
}
