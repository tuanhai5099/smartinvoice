using System.Runtime.CompilerServices;
using SmartInvoice.Captcha.Postprocessing;
using SmartInvoice.Captcha.Preprocessing;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;

namespace SmartInvoice.Captcha.Prediction;

/// <summary>
/// Giải captcha bằng PaddleOCR. Có thể dùng preprocess hoặc không (hóa đơn điện tử: nền trắng, không preprocess).
/// Tránh lỗi "PaddlePredictor run failed": dùng PaddleDevice.Blas(), lock gọi Run() (single-thread), và tái tạo engine khi lỗi.
/// </summary>
public sealed class CaptchaSolver : IDisposable
{
    private readonly PreprocessOptions _preprocessOptions;
    private readonly object _runLock = new();
    private PaddleOcrAll? _ocr;
    private FullOcrModel? _model;

    public CaptchaSolver(PreprocessOptions? preprocessOptions = null)
    {
        _preprocessOptions = preprocessOptions ?? PreprocessOptions.None;
    }

    public void Initialize()
    {
        if (_ocr != null) return;
        _model = LocalFullModels.ChineseV5;
        // Dùng Blas (OpenBLAS) mặc định: tương thích tốt hơn, tránh lỗi "PaddlePredictor Detector run failed" khi dùng Mkldnn trên một số máy.
        _ocr = new PaddleOcrAll(_model, PaddleDevice.Blas())
        {
            AllowRotateDetection = false,
            Enable180Classification = false,
        };
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_ocr != null) return Task.CompletedTask;
        Initialize();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<TextWithCenter> ExtractTextWithCenters(PaddleOcrResult result)
    {
        var regions = result?.Regions;
        if (regions == null || !regions.Any())
            return Array.Empty<TextWithCenter>();
        var list = new List<TextWithCenter>();
        foreach (var region in regions)
        {
            var text = region.Text ?? string.Empty;
            var centerX = region.Rect.Center.X;
            list.Add(new TextWithCenter(text, centerX));
        }
        return list;
    }

    /// <summary>Gọi OCR Run trong lock; nếu "run failed" thì dispose engine để lần sau tạo mới.</summary>
    private PaddleOcrResult RunOcr(Mat input)
    {
        lock (_runLock)
        {
            EnsureInitialized();
            try
            {
                return _ocr!.Run(input);
            }
            catch (Exception ex) when (
                ex.Message.Contains("run failed", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("PaddlePredictor", StringComparison.OrdinalIgnoreCase) ||
                (ex.InnerException?.Message?.Contains("run failed", StringComparison.OrdinalIgnoreCase) == true))
            {
                _ocr?.Dispose();
                _ocr = null;
                throw new InvalidOperationException("Nhận dạng captcha thất bại (engine đã được reset). Vui lòng thử lại.", ex);
            }
        }
    }

    public string Predict(string imagePath)
    {
        EnsureInitialized();
        Mat? preprocessed = null;
        try
        {
            Mat input;
            if (_preprocessOptions.AnyEnabled)
            {
                preprocessed = ImagePreprocessor.LoadAndPreprocess(imagePath, _preprocessOptions);
                input = preprocessed;
            }
            else
            {
                input = Cv2.ImRead(imagePath);
                if (input.Empty())
                    throw new FileNotFoundException($"Cannot load image: {imagePath}", imagePath);
            }
            using (input)
            {
                var result = RunOcr(input);
                var textTuples = ExtractTextWithCenters(result);
                return CaptchaPostprocessor.Postprocess(textTuples);
            }
        }
        finally
        {
            preprocessed?.Dispose();
        }
    }

    public string Predict(Mat image)
    {
        EnsureInitialized();
        Mat? preprocessed = null;
        try
        {
            Mat input;
            if (_preprocessOptions.AnyEnabled)
            {
                preprocessed = ImagePreprocessor.Preprocess(image, _preprocessOptions);
                input = preprocessed;
            }
            else
                input = image;
            var result = RunOcr(input);
            var textTuples = ExtractTextWithCenters(result);
            return CaptchaPostprocessor.Postprocess(textTuples);
        }
        finally
        {
            preprocessed?.Dispose();
        }
    }

    /// <summary>
    /// Giải từ stream ảnh (vd. PNG đã render từ SVG). Ghi tạm ra file rồi predict để tránh decode stream nhiều lần.
    /// </summary>
    public string PredictFromStream(Stream imageStream)
    {
        EnsureInitialized();
        using var mat = Mat.FromStream(imageStream, ImreadModes.Color);
        if (mat.Empty())
            throw new InvalidOperationException("Cannot decode image from stream.");
        return Predict(mat);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInitialized()
    {
        if (_ocr == null)
            throw new InvalidOperationException("Call Initialize() or InitializeAsync() before Predict.");
    }

    public void Dispose()
    {
        _ocr?.Dispose();
        _ocr = null;
        _model = null;
    }
}
