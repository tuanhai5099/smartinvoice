using OpenCvSharp;

namespace SmartInvoice.Captcha.Preprocessing;

/// <summary>
/// Tiền xử lý ảnh CAPTCHA. Với hóa đơn điện tử (nền trắng) không cần gọi; với captcha khác có thể dùng Contrast, Denoise, RemoveLines, Binarize hoặc preset GreenStriped/GrayLines.
/// Trả về ảnh BGR để đưa vào PaddleOCR.
/// </summary>
public static class ImagePreprocessor
{
    public static Mat EnhanceContrast(Mat gray, double clipLimit = 2.5)
    {
        using var clahe = Cv2.CreateCLAHE(clipLimit: clipLimit, tileGridSize: new Size(8, 8));
        var result = new Mat();
        clahe.Apply(gray, result);
        return result;
    }

    public static Mat Denoise(Mat gray)
    {
        var result = new Mat();
        Cv2.BilateralFilter(gray, result, d: 9, sigmaColor: 75, sigmaSpace: 75);
        return result;
    }

    private static Mat CreateDiagonalKernel45()
    {
        var k = new Mat(5, 5, MatType.CV_8UC1);
        k.Set(0, 4, (byte)1); k.Set(1, 3, (byte)1); k.Set(2, 2, (byte)1); k.Set(3, 1, (byte)1); k.Set(4, 0, (byte)1);
        return k;
    }

    private static Mat CreateDiagonalKernelMinus45()
    {
        var k = new Mat(5, 5, MatType.CV_8UC1);
        k.Set(0, 0, (byte)1); k.Set(1, 1, (byte)1); k.Set(2, 2, (byte)1); k.Set(3, 3, (byte)1); k.Set(4, 4, (byte)1);
        return k;
    }

    private static Mat CreateDiagonalKernel45_3x3()
    {
        var k = new Mat(3, 3, MatType.CV_8UC1);
        k.Set(0, 2, (byte)1); k.Set(1, 1, (byte)1); k.Set(2, 0, (byte)1);
        return k;
    }

    private static Mat CreateDiagonalKernelMinus45_3x3()
    {
        var k = new Mat(3, 3, MatType.CV_8UC1);
        k.Set(0, 0, (byte)1); k.Set(1, 1, (byte)1); k.Set(2, 2, (byte)1);
        return k;
    }

    public static Mat PreprocessGreenStriped(Mat gray)
    {
        using var whiteMask = new Mat();
        Cv2.Threshold(gray, whiteMask, 200, 255, ThresholdTypes.Binary);
        using var kernelV = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, 3));
        using var withoutLines = new Mat();
        Cv2.MorphologyEx(whiteMask, withoutLines, MorphTypes.Open, kernelV);
        using var kernelH = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 1));
        using var closedH = new Mat();
        Cv2.MorphologyEx(withoutLines, closedH, MorphTypes.Close, kernelH);
        using var kernelV2 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, 3));
        using var closed = new Mat();
        Cv2.MorphologyEx(closedH, closed, MorphTypes.Close, kernelV2);
        using var kernelDilate = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var thickened = new Mat();
        Cv2.Dilate(closed, thickened, kernelDilate);
        using var inverted = new Mat();
        Cv2.BitwiseNot(thickened, inverted);
        using var blurred = new Mat();
        Cv2.GaussianBlur(inverted, blurred, new Size(3, 3), sigmaX: 0.5, sigmaY: 0.5);
        var result = new Mat();
        Cv2.Threshold(blurred, result, 127, 255, ThresholdTypes.Binary);
        return result;
    }

    private static Mat RemoveLinesOutsideCharacters(Mat binary)
    {
        using var kernelH = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(11, 1));
        using var afterH = new Mat();
        Cv2.MorphologyEx(binary, afterH, MorphTypes.Open, kernelH);
        using var afterD1 = new Mat();
        using (var k45 = CreateDiagonalKernel45())
            Cv2.MorphologyEx(afterH, afterD1, MorphTypes.Open, k45);
        using var kMinus45 = CreateDiagonalKernelMinus45();
        using var cleaned = new Mat();
        Cv2.MorphologyEx(afterD1, cleaned, MorphTypes.Open, kMinus45);
        using var kernelDilate = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var characterRegion = new Mat();
        Cv2.Dilate(cleaned, characterRegion, kernelDilate);
        using var cleanedInv = new Mat();
        Cv2.BitwiseNot(cleaned, cleanedInv);
        using var lineMask = new Mat();
        Cv2.BitwiseAnd(binary, cleanedInv, lineMask);
        using var outsideChar = new Mat();
        Cv2.BitwiseNot(characterRegion, outsideChar);
        using var toRemove = new Mat();
        Cv2.BitwiseAnd(lineMask, outsideChar, toRemove);
        var result = binary.Clone();
        result.SetTo(new Scalar(255), toRemove);
        return result;
    }

    public static Mat PreprocessGrayLines(Mat gray)
    {
        using var denoised = Denoise(gray);
        using var contrasted = EnhanceContrast(denoised, clipLimit: 1.5);
        using var binary = new Mat();
        Cv2.Threshold(contrasted, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var noLinesOutside = RemoveLinesOutsideCharacters(binary);
        var w = noLinesOutside.Width;
        var h = noLinesOutside.Height;
        using var upscaled = new Mat();
        Cv2.Resize(noLinesOutside, upscaled, new Size(w * 2, h * 2), 0, 0, InterpolationFlags.Cubic);
        using var blurred = new Mat();
        Cv2.GaussianBlur(upscaled, blurred, new Size(3, 3), sigmaX: 0.3, sigmaY: 0.3);
        var result = new Mat();
        Cv2.Threshold(blurred, result, 127, 255, ThresholdTypes.Binary);
        return result;
    }

    public static Mat Binarize(Mat gray)
    {
        var result = new Mat();
        Cv2.Threshold(gray, result, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        return result;
    }

    public static Mat RemoveLines(Mat gray)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var kernelH = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 1));
        using var step1 = new Mat();
        Cv2.MorphologyEx(binary, step1, MorphTypes.Open, kernelH);
        using (var kernelDiag45 = CreateDiagonalKernel45())
        using (var step2 = new Mat())
        {
            Cv2.MorphologyEx(step1, step2, MorphTypes.Open, kernelDiag45);
            using (var kernelDiagMinus45 = CreateDiagonalKernelMinus45())
            using (var step3 = new Mat())
            {
                Cv2.MorphologyEx(step2, step3, MorphTypes.Open, kernelDiagMinus45);
                var result = new Mat();
                Cv2.BitwiseNot(step3, result);
                return result;
            }
        }
    }

    public static Mat LoadAndPreprocess(string imagePath, PreprocessOptions options)
    {
        using var img = Cv2.ImRead(imagePath);
        if (img.Empty())
            throw new FileNotFoundException($"Cannot load image: {imagePath}", imagePath);
        Mat gray;
        using (var temp = new Mat())
        {
            Cv2.CvtColor(img, temp, ColorConversionCodes.BGR2GRAY);
            gray = temp.Clone();
        }
        if (options.GreenStriped)
        {
            using var prev = gray;
            gray = PreprocessGreenStriped(prev);
        }
        else if (options.GrayLines)
        {
            using var prev = gray;
            gray = PreprocessGrayLines(prev);
        }
        else
        {
            if (options.Contrast) { using var prev = gray; gray = EnhanceContrast(prev); }
            if (options.Denoise) { using var prev = gray; gray = Denoise(prev); }
            if (options.RemoveLines) { using var prev = gray; gray = RemoveLines(prev); }
            if (options.Binarize) { using var prev = gray; gray = Binarize(prev); }
        }
        var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        gray.Dispose();
        return bgr;
    }

    public static Mat Preprocess(Mat bgrInput, PreprocessOptions options)
    {
        Mat gray;
        using (var temp = new Mat())
        {
            Cv2.CvtColor(bgrInput, temp, ColorConversionCodes.BGR2GRAY);
            gray = temp.Clone();
        }
        if (options.GreenStriped)
        {
            using var prev = gray;
            gray = PreprocessGreenStriped(prev);
        }
        else if (options.GrayLines)
        {
            using var prev = gray;
            gray = PreprocessGrayLines(prev);
        }
        else
        {
            if (options.Contrast) { using var prev = gray; gray = EnhanceContrast(prev); }
            if (options.Denoise) { using var prev = gray; gray = Denoise(prev); }
            if (options.RemoveLines) { using var prev = gray; gray = RemoveLines(prev); }
            if (options.Binarize) { using var prev = gray; gray = Binarize(prev); }
        }
        var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        gray.Dispose();
        return bgr;
    }
}
