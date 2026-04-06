using PuppeteerSharp;
using SmartInvoice.Application.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartInvoice.InvoicePdfFetchers
{
    internal class CaptchaUtils
    {
        public static async Task<byte[]> FindCaptchaById(IPage page, string elemId)
        {
            var imageCaptcha = await page.WaitForSelectorAsync($"#{elemId}");
            var base64 = await imageCaptcha.ScreenshotBase64Async();
            byte[] imageBytes;
            imageBytes = Convert.FromBase64String(base64);
            return imageBytes;
        }
    }
}
