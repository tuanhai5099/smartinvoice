using System.ComponentModel.DataAnnotations;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Modules.Companies.ViewModels;

/// <summary>
/// ViewModel for Add/Edit Company popup. On save, performs login/token fetch and shows status in the popup.
/// Mã công ty, MST đăng nhập, Mật khẩu là bắt buộc (validation + dấu * đỏ trên UI).
/// </summary>
public partial class CompanyEditViewModel : ObservableValidator
{
    private readonly ICompanyAppService _companyService;
    private readonly Action<bool, string?> _closeCallback;
    private readonly IConfirmationService _confirmationService;

    /// <summary>Lần đăng nhập thất bại gần nhất do sai user/password (trong phiên popup này).</summary>
    private string? _lastFailedUsername;
    private string? _lastFailedPassword;
    private bool _lastFailedWasWrongCredentials;

    [ObservableProperty]
    private bool _isAddMode = true;

    [ObservableProperty]
    private Guid _companyId;

    [ObservableProperty]
    [Required(ErrorMessage = "Mã công ty là bắt buộc.")]
    private string _companyCode = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "MST đăng nhập là bắt buộc.")]
    private string _username = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "Mật khẩu là bắt buộc.")]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isPasswordVisible;

    [ObservableProperty]
    private string _companyNameDisplay = string.Empty;

    [ObservableProperty]
    private string _taxCodeDisplay = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _statusIsError;

    public string WindowTitle => IsAddMode ? "Thêm công ty" : "Sửa công ty";

    public CompanyEditViewModel(
        ICompanyAppService companyService,
        IConfirmationService confirmationService,
        Action<bool, string?> closeCallback)
    {
        _companyService = companyService;
        _confirmationService = confirmationService;
        _closeCallback = closeCallback;
        ErrorsChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public void SetAddMode()
    {
        IsAddMode = true;
        CompanyId = Guid.Empty;
        CompanyCode = string.Empty;
        Username = string.Empty;
        Password = string.Empty;
        IsPasswordVisible = false;
        CompanyNameDisplay = string.Empty;
        TaxCodeDisplay = string.Empty;
        StatusMessage = string.Empty;
        StatusIsError = false;
        ClearErrors(nameof(CompanyCode));
        ClearErrors(nameof(Username));
        ClearErrors(nameof(Password));
    }

    /// <summary>Gọi với kết quả GetByIdForEditAsync để có Password và CompanyCode.</summary>
    public void SetEditMode(CompanyDto company)
    {
        IsAddMode = false;
        CompanyId = company.Id;
        CompanyCode = company.CompanyCode ?? string.Empty;
        Username = company.Username ?? string.Empty;
        Password = company.Password ?? string.Empty;
        IsPasswordVisible = false;
        CompanyNameDisplay = company.CompanyName ?? "—";
        TaxCodeDisplay = company.TaxCode ?? "—";
        StatusMessage = string.Empty;
        StatusIsError = false;
        ClearErrors(nameof(CompanyCode));
        ClearErrors(nameof(Username));
        ClearErrors(nameof(Password));
    }

    [RelayCommand]
    private void Cancel()
    {
        _closeCallback(false, null);
    }

    [RelayCommand]
    private void TogglePasswordVisibility()
    {
        IsPasswordVisible = !IsPasswordVisible;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ValidateProperty(CompanyCode, nameof(CompanyCode));
        ValidateProperty(Username, nameof(Username));
        ValidateProperty(Password, nameof(Password));
        if (HasErrors)
            return;
        if (IsBusy) return;

        // Nếu lần trước đăng nhập sai user/password với đúng MST/mật khẩu này, hỏi lại trước khi tiếp tục
        var currentUsername = Username?.Trim() ?? string.Empty;
        var currentPassword = Password ?? string.Empty;
        if (_lastFailedWasWrongCredentials
            && string.Equals(currentUsername, _lastFailedUsername, StringComparison.OrdinalIgnoreCase)
            && string.Equals(currentPassword, _lastFailedPassword, StringComparison.Ordinal))
        {
            var confirmed = await _confirmationService.ConfirmAsync(
                "Xác nhận đăng nhập",
                "Lần trước đăng nhập với MST và mật khẩu hiện tại đã thất bại vì sai tên đăng nhập hoặc mật khẩu.\n\n"
                + "Đăng nhập sai nhiều lần có thể khiến tài khoản hóa đơn điện tử bị khóa.\n\n"
                + "Bạn có chắc chắn muốn tiếp tục đăng nhập với thông tin cấu hình hiện tại?"
            ).ConfigureAwait(true);

            if (!confirmed)
            {
                SetStatus("Đã hủy đăng nhập. Vui lòng kiểm tra lại MST và mật khẩu trước khi thử lại.", isError: true);
                return;
            }
        }

        IsBusy = true;
        SetStatus("Đang xác thực và lấy token...", isError: false);
        try
        {
            if (IsAddMode)
            {
                var dto = new CompanyEditDto(NormalizeCompanyCode(CompanyCode), Username, Password, null, null);
                var result = await _companyService.AddCompanyWithLoginAsync(dto).ConfigureAwait(true);
                if (result.Success)
                {
                    _lastFailedWasWrongCredentials = false;
                    _lastFailedUsername = null;
                    _lastFailedPassword = null;
                    SetStatus("Đã thêm công ty và lấy token thành công.", isError: false);
                    _closeCallback(true, null);
                }
                else
                {
                    SetStatus(result.Message, isError: true);
                    // Nếu lỗi là sai user/password, ghi lại cặp MST/mật khẩu để cảnh báo ở lần sau.
                    if (!string.IsNullOrWhiteSpace(result.Message)
                        && result.Message.Contains("Sai tên đăng nhập hoặc mật khẩu", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastFailedWasWrongCredentials = true;
                        _lastFailedUsername = currentUsername;
                        _lastFailedPassword = currentPassword;
                    }
                }
            }
            else
            {
                var company = await _companyService.GetByIdAsync(CompanyId).ConfigureAwait(true);
                if (company == null)
                {
                    SetStatus("Công ty không tồn tại.", isError: true);
                    return;
                }
                SetStatus("Đang cập nhật thông tin...", isError: false);
                var editDto = new CompanyEditDto(
                    NormalizeCompanyCode(CompanyCode), Username, Password,
                    company.CompanyName, company.TaxCode);
                await _companyService.UpdateAsync(CompanyId, editDto).ConfigureAwait(true);
                SetStatus("Đang lấy token mới...", isError: false);
                var loginResult = await _companyService.LoginAndSyncProfileAsync(CompanyId).ConfigureAwait(true);
                if (loginResult.Success)
                {
                    _lastFailedWasWrongCredentials = false;
                    _lastFailedUsername = null;
                    _lastFailedPassword = null;
                    SetStatus("Đã cập nhật và lấy token thành công.", isError: false);
                    _closeCallback(true, null);
                }
                else
                {
                    SetStatus("Cập nhật đã lưu nhưng lấy token thất bại: " + (loginResult.Message ?? "Lỗi không xác định."), isError: true);
                    if (!string.IsNullOrWhiteSpace(loginResult.Message)
                        && loginResult.Message.Contains("Sai tên đăng nhập hoặc mật khẩu", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastFailedWasWrongCredentials = true;
                        _lastFailedUsername = currentUsername;
                        _lastFailedPassword = currentPassword;
                    }
                    _closeCallback(true, loginResult.Message);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi: " + ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy && !HasErrors;

    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnCompanyCodeChanged(string value) => ValidateProperty(value, nameof(CompanyCode));
    partial void OnUsernameChanged(string value) => ValidateProperty(value, nameof(Username));
    partial void OnPasswordChanged(string value) => ValidateProperty(value, nameof(Password));

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    private static string? NormalizeCompanyCode(string? code)
    {
        var t = code?.Trim();
        return string.IsNullOrWhiteSpace(t) ? null : (t.Length > 30 ? t[..30] : t);
    }
}
