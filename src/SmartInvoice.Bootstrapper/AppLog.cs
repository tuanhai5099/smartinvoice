using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SmartInvoice.Bootstrapper;

/// <summary>
/// Log khởi tạo và exception ra file. Khởi tạo trước khi Run() để mọi bước và lỗi đều ghi vào file.
/// </summary>
public static class AppLog
{
    private static ILoggerFactory? _loggerFactory;
    private static ILogger? _appLogger;

    /// <summary>Thư mục log (LocalApplicationData\SmartInvoice\Logs).</summary>
    public static string LogDirectory { get; private set; } = "";

    /// <summary>Đường dẫn file log hiện tại (để mở nhanh khi cần).</summary>
    public static string CurrentLogFilePath { get; private set; } = "";

    /// <summary>LoggerFactory dùng cho toàn app (đăng ký trong DI sau khi gọi Initialize).</summary>
    public static ILoggerFactory LoggerFactory => _loggerFactory ?? throw new InvalidOperationException("Gọi AppLog.Initialize() trước khi chạy ứng dụng.");

    /// <summary>Logger cho startup và exception (dùng trước khi có DI).</summary>
    public static ILogger AppLogger => _appLogger ?? throw new InvalidOperationException("Gọi AppLog.Initialize() trước.");

    /// <summary>
    /// Khởi tạo log ra file. Gọi ngay đầu OnStartup (trước base.OnStartup và bootstrapper.Run()).
    /// File: {LocalApplicationData}\SmartInvoice\Logs\smartinvoice_YYYYMMDD.log (rolling theo ngày).
    /// </summary>
    public static void Initialize()
    {
        if (_loggerFactory != null) return;

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartInvoice",
            "Logs");
        LogDirectory = baseDir;
        Directory.CreateDirectory(baseDir);

        var logPath = Path.Combine(baseDir, "smartinvoice_.log");
        CurrentLogFilePath = Path.Combine(baseDir, $"smartinvoice_{DateTime.Now:yyyyMMdd}.log");

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(baseDir, "smartinvoice_.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddSerilog(serilogLogger, dispose: true);
        });
        _appLogger = _loggerFactory.CreateLogger("Startup");
        AppLogger.LogInformation("=== SmartInvoice log initialized. LogDir={LogDir} ===", LogDirectory);
    }

    /// <summary>Ghi exception ra log (và file) rồi trả về message ngắn cho hiển thị.</summary>
    public static void LogException(Exception ex, string context = "")
    {
        var logger = _appLogger;
        if (logger != null)
        {
            logger.LogError(ex, "Exception {Context}: {Message}", context, ex.Message);
        }
        else
        {
            try
            {
                var dir = string.IsNullOrEmpty(LogDirectory)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartInvoice", "Logs")
                    : LogDirectory;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"smartinvoice_{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERR] {context}: {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}";
                File.AppendAllText(path, line);
            }
            catch { /* best effort */ }
        }
    }
}
