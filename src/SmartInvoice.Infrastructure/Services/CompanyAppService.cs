using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;
using SmartInvoice.Infrastructure.HoaDonDienTu;

namespace SmartInvoice.Infrastructure.Services;

public class CompanyAppService : ICompanyAppService
{
    private const int MinCaptchaLength = 6;
    private const int MaxCaptchaRetries = 4;

    private readonly IUnitOfWork _uow;
    private readonly IHoaDonDienTuApiClient _apiClient;
    private readonly ICaptchaSolverService _captchaSolver;
    private readonly ILoginAttemptTracker _loginAttemptTracker;
    private readonly ILogger _logger;

    public CompanyAppService(
        IUnitOfWork uow,
        IHoaDonDienTuApiClient apiClient,
        ICaptchaSolverService captchaSolver,
        ILoginAttemptTracker loginAttemptTracker,
        ILoggerFactory loggerFactory)
    {
        _uow = uow;
        _apiClient = apiClient;
        _captchaSolver = captchaSolver;
        _loginAttemptTracker = loginAttemptTracker;
        _logger = loggerFactory.CreateLogger(nameof(CompanyAppService));
    }

    public async Task<IReadOnlyList<CompanyDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var list = await _uow.Companies.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return list.Select(c => MapToDto(c, includePassword: false)).ToList();
    }

    public async Task<CompanyDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _uow.Companies.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return entity == null ? null : MapToDto(entity, includePassword: false);
    }

    public async Task<CompanyDto?> GetByIdForEditAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _uow.Companies.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return entity == null ? null : MapToDto(entity, includePassword: true);
    }

    public async Task<CompanyDto> CreateAsync(CompanyEditDto dto, CancellationToken cancellationToken = default)
    {
        var companyCodeNorm = NormalizeCompanyCode(dto.CompanyCode);
        if (!string.IsNullOrEmpty(companyCodeNorm))
        {
            var existingByCode = await _uow.Companies.GetByCompanyCodeAsync(companyCodeNorm, cancellationToken).ConfigureAwait(false);
            if (existingByCode != null)
                throw new InvalidOperationException("Mã công ty này đã tồn tại trong hệ thống.");
        }
        var username = dto.Username.Trim();
        var existingByUsername = await _uow.Companies.GetByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        if (existingByUsername != null)
            throw new InvalidOperationException("Mã số thuế (MST) này đã tồn tại trong hệ thống.");

        var company = new Company
        {
            Id = Guid.NewGuid(),
            CompanyCode = companyCodeNorm,
            Username = username,
            Password = dto.Password,
            CompanyName = dto.CompanyName?.Trim(),
            TaxCode = dto.TaxCode?.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _uow.Companies.AddAsync(company, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Company created: {Username}", company.Username);
        return MapToDto(company, includePassword: false);
    }

    private static string? NormalizeCompanyCode(string? code)
    {
        var t = code?.Trim();
        return string.IsNullOrWhiteSpace(t) ? null : (t.Length > 30 ? t[..30] : t);
    }

    public async Task<AddCompanyResult> AddCompanyWithLoginAsync(CompanyEditDto dto, CancellationToken cancellationToken = default)
    {
        var username = dto.Username?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username))
            return new AddCompanyResult(false, "Nhập MST đăng nhập.");
        if (string.IsNullOrWhiteSpace(dto.Password))
            return new AddCompanyResult(false, "Nhập mật khẩu.");

        var companyCodeNorm = NormalizeCompanyCode(dto.CompanyCode);
        if (!string.IsNullOrEmpty(companyCodeNorm))
        {
            var existingByCode = await _uow.Companies.GetByCompanyCodeAsync(companyCodeNorm, cancellationToken).ConfigureAwait(false);
            if (existingByCode != null)
                return new AddCompanyResult(false, "Mã công ty này đã tồn tại trong hệ thống.");
        }
        var existing = await _uow.Companies.GetByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        if (existing != null)
            return new AddCompanyResult(false, "Mã số thuế (MST) này đã tồn tại trong hệ thống.");

        if (_loginAttemptTracker.IsBlocked(username))
            return new AddCompanyResult(false, "Đã đăng nhập sai 3 lần. Dừng để tránh bị khóa tài khoản (sai 5 lần sẽ bị khóa).");

        try
        {
            // Tự động retry nếu server báo sai captcha: chỉ thông báo lỗi cho user khi chắc chắn là sai user/password.
            for (var attempt = 0; attempt < MaxCaptchaRetries; attempt++)
            {
                var (captcha, captchaText, _) = await FetchAndSolveCaptchaAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(captchaText) || captchaText.Length < MinCaptchaLength)
                {
                    if (attempt == MaxCaptchaRetries - 1)
                        return new AddCompanyResult(false, "Không giải được captcha đủ 6 ký tự. Thử lại sau ít phút.");
                    continue;
                }

                var auth = await _apiClient.AuthenticateAsync(username, dto.Password, captcha.Key, captchaText, cancellationToken).ConfigureAwait(false);
                if (auth.Success && !string.IsNullOrEmpty(auth.Token))
                {
                    _loginAttemptTracker.Reset(username);

                    var company = new Company
                    {
                        Id = Guid.NewGuid(),
                        CompanyCode = companyCodeNorm,
                        Username = username,
                        Password = dto.Password,
                        AccessToken = auth.Token,
                        RefreshToken = auth.RefreshToken,
                        TaxCode = username,
                        LastLoginAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    var profile = await _apiClient.GetProfileAsync(auth.Token, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(profile.Name)) company.CompanyName = profile.Name;
                    if (!string.IsNullOrEmpty(profile.TaxCode)) company.TaxCode = profile.TaxCode;

                    var firstUserJson = await _apiClient.GetSystemTaxpayerUsersFirstItemJsonAsync(auth.Token, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(firstUserJson))
                    {
                        company.UserDataJson = firstUserJson;
                        MapCompanyFromUserJson(firstUserJson, company);
                    }
                    company.LastSyncedAt = DateTime.UtcNow;

                    await _uow.Companies.AddAsync(company, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Company added with login: {Username}", company.Username);
                    return new AddCompanyResult(true, "Thêm công ty thành công.");
                }

                var msg = auth.Message ?? "Đăng nhập thất bại.";
                if (IsCaptchaErrorMessage(msg))
                {
                    // Sai captcha: không thông báo cho user, chỉ log và thử lại với captcha mới.
                    _logger.LogDebug("Login failed due to captcha error (attempt {Attempt}): {Message}", attempt + 1, msg);
                    if (attempt == MaxCaptchaRetries - 1)
                        return new AddCompanyResult(false, "Không thể đăng nhập do captcha liên tục sai từ máy chủ. Vui lòng thử lại sau ít phút.");
                    continue;
                }

                // Sai user/password hoặc lỗi khác: không retry nhiều lần để tránh khóa tài khoản.
                _loginAttemptTracker.RecordFailure(username);
                var count = _loginAttemptTracker.GetFailureCount(username);
                if (IsWrongCredentialsMessage(msg))
                    msg = "Sai tên đăng nhập hoặc mật khẩu. Vui lòng kiểm tra lại MST và mật khẩu.";
                if (count >= 3)
                    msg += $" Đã sai {count} lần. Dừng để tránh bị khóa (sai 5 lần sẽ bị khóa).";
                else
                    msg += $" (Đã sai {count}/3 lần.)";
                return new AddCompanyResult(false, msg, count);
            }

            // Không nên tới đây, nhưng phòng trường hợp logic thay đổi.
            return new AddCompanyResult(false, "Không thể đăng nhập. Vui lòng thử lại sau ít phút.");
        }
        catch (Exception ex)
        {
            _loginAttemptTracker.RecordFailure(username);
            var count = _loginAttemptTracker.GetFailureCount(username);
            var msg = "Lỗi: " + ex.Message;
            if (count >= 3)
                msg += " Đã sai 3 lần. Dừng để tránh bị khóa tài khoản.";
            else
                msg += $" (Đã sai {count}/3 lần.)";
            return new AddCompanyResult(false, msg, count);
        }
    }

    public async Task UpdateAsync(Guid id, CompanyEditDto dto, CancellationToken cancellationToken = default)
    {
        var entity = await _uow.Companies.GetByIdTrackedAsync(id, cancellationToken).ConfigureAwait(false);
        if (entity == null)
            throw new InvalidOperationException("Công ty không tồn tại.");

        var username = dto.Username.Trim();
        var companyCodeNorm = NormalizeCompanyCode(dto.CompanyCode);

        if (!string.IsNullOrEmpty(companyCodeNorm))
        {
            var existingByCode = await _uow.Companies.GetByCompanyCodeAsync(companyCodeNorm, cancellationToken).ConfigureAwait(false);
            if (existingByCode != null && existingByCode.Id != id)
                throw new InvalidOperationException("Mã công ty này đã được sử dụng bởi công ty khác.");
        }

        var existingByUsername = await _uow.Companies.GetByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        if (existingByUsername != null && existingByUsername.Id != id)
            throw new InvalidOperationException("Mã số thuế (MST) này đã được sử dụng bởi công ty khác.");

        entity.CompanyCode = companyCodeNorm;
        entity.Username = username;
        entity.Password = dto.Password;
        entity.CompanyName = dto.CompanyName?.Trim();
        entity.TaxCode = dto.TaxCode?.Trim();
        entity.UpdatedAt = DateTime.UtcNow;
        await _uow.Companies.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Company updated: {Id}", id);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _uow.Companies.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Company deleted: {Id}", id);
    }

    public async Task<bool> ValidateTokenAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        return await EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> EnsureValidTokenAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null || string.IsNullOrWhiteSpace(company.AccessToken))
            return false;
        try
        {
            await _apiClient.GetProfileAsync(company.AccessToken, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException)
        {
            // Token hết hạn (401) hoặc lỗi mạng: thử refresh nếu có refresh token
            if (string.IsNullOrWhiteSpace(company.RefreshToken))
                return false;
            var auth = await _apiClient.RefreshTokenAsync(company.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (!auth.Success || string.IsNullOrEmpty(auth.Token))
            {
                _logger.LogDebug("Refresh token failed for company {Id}: {Message}", companyId, auth.Message);
                return false;
            }
            company.AccessToken = auth.Token;
            if (!string.IsNullOrEmpty(auth.RefreshToken))
                company.RefreshToken = auth.RefreshToken;
            company.UpdatedAt = DateTime.UtcNow;
            await _uow.Companies.UpdateAsync(company, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Refreshed token for company {Username}", company.Username);
            return true;
        }
    }

    public async Task<LoginResult> LoginAndSyncProfileAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            return new LoginResult(false, "Công ty không tồn tại.", null, null);

        try
        {
            // Tự động retry nếu server báo sai captcha: chỉ show lỗi cho user khi chắc chắn do user/password.
            for (var attempt = 0; attempt < MaxCaptchaRetries; attempt++)
            {
                var (captcha, captchaText, _) = await FetchAndSolveCaptchaAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(captchaText) || captchaText.Length < MinCaptchaLength)
                {
                    if (attempt == MaxCaptchaRetries - 1)
                        return new LoginResult(false, "Không giải được captcha đủ 6 ký tự. Vui lòng thử lại sau ít phút.", null, null);
                    continue;
                }

                var auth = await _apiClient.AuthenticateAsync(company.Username, company.Password, captcha.Key, captchaText, cancellationToken).ConfigureAwait(false);
                if (auth.Success && !string.IsNullOrEmpty(auth.Token))
                {
                    company.AccessToken = auth.Token;
                    company.RefreshToken = auth.RefreshToken;
                    company.LastLoginAt = DateTime.UtcNow;

                    var profile = await _apiClient.GetProfileAsync(auth.Token, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(profile.Name))
                        company.CompanyName = profile.Name;
                    if (!string.IsNullOrEmpty(profile.TaxCode))
                        company.TaxCode = profile.TaxCode;
                    if (string.IsNullOrEmpty(company.TaxCode))
                        company.TaxCode = company.Username;

                    var firstUserJson = await _apiClient.GetSystemTaxpayerUsersFirstItemJsonAsync(auth.Token, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(firstUserJson))
                    {
                        company.UserDataJson = firstUserJson;
                        MapCompanyFromUserJson(firstUserJson, company);
                    }
                    company.LastSyncedAt = DateTime.UtcNow;
                    company.UpdatedAt = DateTime.UtcNow;
                    await _uow.Companies.UpdateAsync(company, cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation("Login and profile synced for company: {Username}", company.Username);
                    return new LoginResult(true, null, company.CompanyName, company.TaxCode);
                }

                var msg = auth.Message ?? "Đăng nhập thất bại.";
                if (IsCaptchaErrorMessage(msg))
                {
                    // Sai captcha: không báo lỗi cho user, chỉ log và thử lại với captcha mới.
                    _logger.LogDebug("Login (sync profile) failed due to captcha error (attempt {Attempt}): {Message}", attempt + 1, msg);
                    if (attempt == MaxCaptchaRetries - 1)
                        return new LoginResult(false, "Không thể đăng nhập do captcha liên tục sai từ máy chủ. Vui lòng thử lại sau ít phút.", null, null);
                    continue;
                }

                // Sai user/password hoặc lỗi khác: trả về ngay để user sửa lại thông tin đăng nhập.
                if (IsWrongCredentialsMessage(msg))
                    msg = "Sai tên đăng nhập hoặc mật khẩu. Vui lòng cập nhật lại thông tin đăng nhập trong phần chỉnh sửa công ty.";
                return new LoginResult(false, msg, null, null);
            }

            // Không nên tới đây, nhưng để fallback an toàn.
            return new LoginResult(false, "Không thể đăng nhập. Vui lòng thử lại sau ít phút.", null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Login failed for company {Id}", companyId);
            return new LoginResult(false, ex.Message, null, null);
        }
    }

    /// <summary>
    /// Lấy captcha từ API, giải trực tiếp từ stream PNG (không lưu file). Nếu kết quả &lt; 6 ký tự thì lấy captcha mới và giải lại (tối đa <see cref="MaxCaptchaRetries"/> lần).
    /// </summary>
    private async Task<(CaptchaResponse captcha, string solvedText, string tempFilePath)> FetchAndSolveCaptchaAsync(CancellationToken cancellationToken)
    {
        CaptchaResponse? lastCaptcha = null;
        var lastText = "";
        var lastPath = "";

        for (var attempt = 0; attempt < MaxCaptchaRetries; attempt++)
        {
            var captcha = await _apiClient.GetCaptchaAsync(cancellationToken).ConfigureAwait(false);
            using var pngStream = SvgToImageHelper.SvgToPngStream(captcha.ContentSvg);
            pngStream.Position = 0;
            var captchaText = (await _captchaSolver.SolveFromStreamAsync(pngStream, cancellationToken).ConfigureAwait(false))?.Trim() ?? "";
            if (captchaText.Length >= MinCaptchaLength)
                return (captcha, captchaText, string.Empty);

            _logger.LogDebug("Captcha solved with {Length} chars (< {Min}), fetching new captcha (attempt {Attempt})", captchaText.Length, MinCaptchaLength, attempt + 1);
            lastCaptcha = captcha;
            lastText = captchaText;
            lastPath = string.Empty;
        }

        return (lastCaptcha ?? (await _apiClient.GetCaptchaAsync(cancellationToken).ConfigureAwait(false)), lastText, lastPath);
    }

    /// <summary>
    /// Map từ object datas[0] của API system-taxpayer/users (camelCase).
    /// Cấu trúc: username, name, email, groupId, type, phoneNumber, status, lastLoginAt, createdAt, modifiedAt, ...
    /// </summary>
    private static void MapCompanyFromUserJson(string userJson, Company company)
    {
        try
        {
            using var doc = JsonDocument.Parse(userJson);
            var r = doc.RootElement;

            if (r.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(name)) company.CompanyName = name;
            }
            if (r.TryGetProperty("taxCode", out var taxEl) && taxEl.ValueKind == JsonValueKind.String)
            {
                var tax = taxEl.GetString();
                if (!string.IsNullOrWhiteSpace(tax)) company.TaxCode = tax;
            }
            if (string.IsNullOrEmpty(company.TaxCode) && r.TryGetProperty("username", out var userEl))
                company.TaxCode = userEl.GetString() ?? company.Username;

            if (r.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String)
            {
                var email = emailEl.GetString();
                if (!string.IsNullOrWhiteSpace(email)) company.Email = email;
            }
            if (r.TryGetProperty("groupId", out var groupEl) && groupEl.ValueKind == JsonValueKind.String)
                company.GroupId = groupEl.GetString();
            if (r.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.Number && typeEl.TryGetInt32(out var typeVal))
                company.UserType = typeVal;
            if (r.TryGetProperty("phoneNumber", out var phoneEl) && phoneEl.ValueKind == JsonValueKind.String)
                company.PhoneNumber = phoneEl.GetString();
            if (r.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.Number && statusEl.TryGetInt32(out var statusVal))
                company.UserStatus = statusVal;

            if (r.TryGetProperty("lastLoginAt", out var lastEl) && lastEl.ValueKind == JsonValueKind.String)
            {
                var s = lastEl.GetString();
                if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, out var dt))
                    company.LastLoginAt = dt;
            }
        }
        catch
        {
            // ignore parse errors
        }
    }

    /// <summary>Nhận diện lỗi do sai tên đăng nhập/mật khẩu từ API — không retry, báo user cập nhật lại.</summary>
    private static bool IsWrongCredentialsMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        // Chuẩn hóa: lower + bỏ dấu + bỏ ký tự không phải chữ/số
        var lower = message.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = lower.Where(ch =>
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark)
                return false; // bỏ dấu
            return char.IsLetterOrDigit(ch);
        }).ToArray();
        var norm = new string(chars);

        // Các pattern tiếng Việt/Anh phổ biến cho sai user/pass hoặc unauthorized
        return
            // Tiếng Việt: "sai mật khẩu", "sai ten dang nhap", "dang nhap sai", ...
            norm.Contains("saimatkhau") ||
            norm.Contains("saitendangnhap") ||
            norm.Contains("dangnhapsai") ||
            norm.Contains("dangnhapkhongdung") ||
            // Tiếng Anh: invalid/incorrect credentials
            (norm.Contains("invalid") && (norm.Contains("credential") || norm.Contains("password") || norm.Contains("username"))) ||
            (norm.Contains("incorrect") && (norm.Contains("password") || norm.Contains("username"))) ||
            norm.Contains("wrongpassword") ||
            // HTTP 401 / unauthorized
            norm.Contains("401") ||
            norm.Contains("unauthorized");
    }

    /// <summary>Nhận diện lỗi captcha từ API (ví dụ: "Mã xác thực không chính xác", "captcha không hợp lệ"). Những lỗi này sẽ được tự động retry, không báo user.</summary>
    private static bool IsCaptchaErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message.Trim().ToLowerInvariant();
        return m.Contains("captcha")
               || m.Contains("mã xác thực") || m.Contains("ma xac thuc")
               || (m.Contains("xác thực") || m.Contains("xac thuc")) && m.Contains("không chính xác")
               || (m.Contains("xác thực") || m.Contains("xac thuc")) && m.Contains("không hợp lệ");
    }

    private static CompanyDto MapToDto(Company c, bool includePassword) => new(
        c.Id,
        c.CompanyCode,
        c.Username,
        c.CompanyName,
        c.TaxCode,
        c.LastLoginAt,
        c.LastSyncedAt,
        c.CreatedAt,
        c.UpdatedAt,
        includePassword ? c.Password : null
    );
}
