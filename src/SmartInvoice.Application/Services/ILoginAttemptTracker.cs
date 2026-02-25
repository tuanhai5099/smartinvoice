namespace SmartInvoice.Application.Services;

/// <summary>
/// Đếm số lần đăng nhập sai theo username (MST). Quá 3 lần thì chặn để tránh bị khóa (sai 5 lần sẽ bị khóa).
/// </summary>
public interface ILoginAttemptTracker
{
    int GetFailureCount(string username);
    void RecordFailure(string username);
    bool IsBlocked(string username);
    void Reset(string username);
}
