namespace SmartInvoice.Application.DTOs;

public record CompanyDto(
    Guid Id,
    string? CompanyCode,
    string Username,
    string? CompanyName,
    string? TaxCode,
    DateTime? LastLoginAt,
    DateTime? LastSyncedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Password = null
);

public record CompanyEditDto(
    string? CompanyCode,
    string Username,
    string Password,
    string? CompanyName,
    string? TaxCode
);
