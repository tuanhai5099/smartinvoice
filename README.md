# Smart Invoice

Ứng dụng WPF quản lý công ty và đăng nhập trang hóa đơn điện tử (GDT), theo kiến trúc Clean Architecture + Prism + WPF-UI.

## Cấu trúc solution

- **SmartInvoice.Core** – Domain (Company), interfaces repository/unit of work
- **SmartInvoice.Application** – DTOs, interfaces service (ICompanyAppService, IHoaDonDienTuApiClient, ICaptchaSolverService)
- **SmartInvoice.Infrastructure** – EF Core SQLite, Repository, HoaDonDienTu API client, Captcha solver, CompanyAppService
- **SmartInvoice.Captcha** – Thư viện giải captcha (PaddleOCR, preprocess tùy chọn; hóa đơn điện tử dùng nền trắng, không preprocess)
- **SmartInvoice.UI** – Shell WPF (FluentWindow), tài nguyên
- **SmartInvoice.Modules.Companies** – Module Prism: màn hình CRUD công ty
- **SmartInvoice.Bootstrapper** – Prism Bootstrapper, DI, entry point (App.xaml)

## Chạy ứng dụng

```bash
cd src/SmartInvoice.Bootstrapper
dotnet run
```

Hoặc mở `SmartInvoice.sln`, đặt **SmartInvoice.Bootstrapper** làm startup project rồi F5.

## Tính năng

- **Quản lý công ty**: Thêm / Sửa / Xóa; mỗi công ty có Username (MST đăng nhập), Password.
- **Đăng nhập & đồng bộ**: Nút "Đăng nhập & đồng bộ" gọi API hóa đơn điện tử (captcha → login → profile), lưu AccessToken (và RefreshToken nếu có), cập nhật tên công ty và mã số thuế.
- **Database**: SQLite (`smartinvoice.db`), tạo tự động lần chạy đầu. Truy cập async, mỗi thao tác dùng DbContext/UnitOfWork riêng để tránh block.
- **Captcha**: Thư viện `SmartInvoice.Captcha` (tương tự paddlesharp): PaddleOCR, preprocess tùy chọn; với captcha hóa đơn điện tử chỉ cần ảnh nền trắng (SVG từ API được render ra PNG rồi giải).

## Tham chiếu

- Đăng nhập/API hóa đơn điện tử: `References/VLKCrawlData/` (WebScraping.cs: captcha, authenticate, profile).
- Giải captcha: `References/paddlesharp/` (PaddleOCR, preprocess options).
