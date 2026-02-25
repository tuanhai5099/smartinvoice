namespace SmartInvoice.Application.Services;

/// <summary>
/// Cung cấp danh sách các IKeyedInvoicePdfFetcher đã đăng ký (dùng cho Registry khi DI container không hỗ trợ nhiều implementation cùng interface).
/// </summary>
public interface IKeyedInvoicePdfFetcherProvider
{
    IEnumerable<IKeyedInvoicePdfFetcher> GetFetchers();
}

/// <summary>
/// Registry chọn IInvoicePdfFetcher theo key nhà cung cấp (tvandnkntt từ payload).
/// Ứng với mỗi key có thể có cách lấy PDF khác nhau; hóa đơn không có key dùng fetcher mặc định (fallback).
/// </summary>
public interface IInvoicePdfFetcherRegistry
{
    /// <summary>
    /// Lấy fetcher tương ứng với mã nhà cung cấp dịch vụ hóa đơn (tvandnkntt).
    /// </summary>
    /// <param name="providerKey">Giá trị tvandnkntt từ payload (mã số thuế nhà cung cấp). Null/empty = dùng fetcher mặc định.</param>
    /// <returns>Fetcher đã đăng ký cho key, hoặc fetcher fallback khi không có key / key chưa đăng ký.</returns>
    IInvoicePdfFetcher GetFetcher(string? providerKey);
}
