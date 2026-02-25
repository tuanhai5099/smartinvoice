namespace SmartInvoice.Application.DTOs;

/// <summary>
/// Một hóa đơn từ API (tổng hợp hoặc chi tiết). Dùng cho hiển thị và lưu trữ.
/// </summary>
public record InvoiceDto(
    string Id,
    Guid CompanyId,
    string? Nbmst,
    string? Khhdon,
    int Shdon,
    ushort Khmshdon,
    DateTime? Tdlap,
    DateTime? Nky,
    string? Nbten,
    string? Nmten,
    string? Nmmst,
    decimal? Tgtttbso,
    decimal? Tgtcthue,
    decimal? Tgtthue,
    short Tthai,
    short Ttxly,
    string? Thdon,
    string? Thtttoan,
    string PayloadJson,
    string? LineItemsJson,
    DateTime SyncedAt
);

/// <summary>
/// Kết quả API danh sách hóa đơn: datas, total, state (pagination), time.
/// </summary>
public record InvoiceListApiResponse(
    IReadOnlyList<InvoiceItemApiDto> Datas,
    int Total,
    string? State,
    int Time
);

/// <summary>
/// Một phần tử trong datas từ API (tổng hợp). Đủ để gọi API chi tiết (nbmst, khhdon, shdon, khmshdon).
/// </summary>
public record InvoiceItemApiDto(
    string? Id,
    string? Nbmst,
    string? Khhdon,
    int Shdon,
    ushort Khmshdon,
    string? Cqt,
    DateTime? Tdlap,
    DateTime? Nky,
    string? Nbten,
    string? Nmten,
    string? Nmmst,
    decimal? Tgtttbso,
    decimal? Tgtcthue,
    decimal? Tgtthue,
    short Tthai,
    short Ttxly,
    string? Thdon,
    string? Thtttoan,
    string RawJson
);

/// <summary>
/// Một dòng hóa đơn liên quan (API query/invoices/relative).
/// </summary>
public record InvoiceRelativeItemDto(
    int Index,
    string Loai,
    ushort Khmshdon,
    string? Khhdon,
    int Shdon,
    string HoaDonGoc,
    string? NoiDungDieuChinh
);
