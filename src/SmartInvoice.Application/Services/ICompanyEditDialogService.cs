using SmartInvoice.Application.DTOs;

namespace SmartInvoice.Application.Services;

/// <summary>
/// Service to show Add/Edit company dialog (popup).
/// On save, login/token fetch runs inside the popup and status is shown to the user.
/// </summary>
public interface ICompanyEditDialogService
{
    /// <summary>
    /// Shows the Add Company dialog. Returns result when user closes the dialog.
    /// </summary>
    Task<CompanyEditDialogResult> ShowAddAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the Edit Company dialog for the given company. Returns result when user closes the dialog.
    /// </summary>
    Task<CompanyEditDialogResult> ShowEditAsync(CompanyDto company, CancellationToken cancellationToken = default);
}

public record CompanyEditDialogResult(bool Success, string? Message = null);
