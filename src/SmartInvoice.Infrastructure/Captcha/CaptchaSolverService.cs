using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Captcha.Preprocessing;
using SmartInvoice.Captcha.Prediction;

namespace SmartInvoice.Infrastructure.Captcha;

public class CaptchaSolverService : ICaptchaSolverService
{
    private readonly PreprocessOptions _preprocessOptions;
    private readonly ILogger _logger;

    public CaptchaSolverService(ILoggerFactory loggerFactory, PreprocessOptions? preprocessOptions = null)
    {
        _logger = loggerFactory.CreateLogger(nameof(CaptchaSolverService));
        _preprocessOptions = preprocessOptions ?? PreprocessOptions.None;
    }

    public Task<string> SolveFromFileAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var solver = new CaptchaSolver(_preprocessOptions);
                solver.Initialize();
                var result = solver.Predict(imagePath);
                _logger.LogDebug("Captcha solved from file: {Path} -> {Result}", imagePath, result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Captcha solve failed from file: {Path}", imagePath);
                throw new InvalidOperationException("Không thể nhận dạng captcha. Vui lòng thử lại hoặc đăng nhập thủ công.", ex);
            }
        }, cancellationToken);
    }

    public Task<string> SolveFromStreamAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var solver = new CaptchaSolver(_preprocessOptions);
                solver.Initialize();
                var result = solver.PredictFromStream(imageStream);
                _logger.LogDebug("Captcha solved from stream -> {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Captcha solve failed from stream");
                throw new InvalidOperationException("Không thể nhận dạng captcha. Vui lòng thử lại hoặc đăng nhập thủ công.", ex);
            }
        }, cancellationToken);
    }
}
