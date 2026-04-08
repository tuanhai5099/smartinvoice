## Danh sách nhà cung cấp hóa đơn & trang tra cứu

File này chỉ để **tham khảo cho lập trình viên** khi thêm/sửa PDF fetcher.

- **Mỗi dòng**: MST NCC (hoặc key logic) → Tên NCC → Ghi chú / Link tra cứu chính (nếu có).
- Khi implement fetcher mới, vui lòng **cập nhật thêm dòng** tương ứng.

### Thứ tự chọn PDF fetcher (`InvoicePdfProviderResolver`)

1. **nbmst** (MST người bán): nếu có `[InvoiceProvider(..., SellerTaxCode)]` khớp (chuẩn hóa giống registry: hậu tố chi nhánh, bỏ số 0 đầu khi toàn chữ số) thì dùng fetcher đó.
2. **msttcgp / TVAN**: nếu không có cấu hình theo người bán, khớp `[InvoiceProvider(..., ProviderTaxCode)]` theo `msttcgp` hoặc `tvanDnKntt` / `tvandnkntt` (JSON hoặc XML).
3. **Đăng ký DI**: mọi `IKeyedInvoicePdfFetcher` phải có trong `KeyedInvoicePdfFetcherProvider` + `ProviderKey` (và thường kèm `[InvoiceProvider]` trừ fetcher mẫu / key chỉ dùng thủ công).

### NCC theo MST (`msttcgp` / ProviderTaxCode)

- **0101360697** – eHoadon / BKAV  
  - Tra cứu: portal riêng (BKAV), dùng XML + DLHDon/@Id.

- **0102519041** – iHoadon / EFY  
  - Tra cứu: portal iHoadon, bắt buộc upload XML.

- **0100727825** – Fast e-Invoice  
  - Tra cứu (keysearch): `https://invoice.fast.com.vn/tra-cuu-hoa-don-dien-tu/` (cũ – hiện trang này có thể đã đổi).  
  - Fetcher hiện tại: `FastInvoicePdfFetcher` – đọc `keysearch` từ `cttkhac/ttkhac` rồi điền vào form.

- **0106026495** – M-invoice  
  - Tra cứu: `https://tracuuhoadon.minvoice.com.vn/tra-cuu-hoa-don`  
  - Fetcher: `MinvoiceInvoicePdfFetcher` – dùng `nbmst` + “Số bảo mật” từ `cttkhac`.

- **0101243150** – MISA meInvoice  
  - Tra cứu: `https://www.meinvoice.vn/tra-cuu`  
  - Lookup provider: `MeinvoiceLookupProvider` – đọc transaction id từ `cttkhac`.

- **0105987432** – EasyInvoice  
  - Tra cứu qua PortalLink trong `cttkhac` / `ttkhac` (domain `easyinvoice.vn` / `easy-invoice.com`).  
  - Fetcher: `EasyInvoicePdfFetcher`.

- **0100109106** – Viettel  
  - Tra cứu API: `https://vinvoice.viettel.vn` (generate captcha → verify → downloadPDF).  
  - Fetcher: `ViettelInvoicePdfFetcher` – dùng `nbmst` + “Mã số bí mật” từ `cttkhac/ttkhac`.

- **0314058603** – VDSG (Viễn Thông Đông Sài Gòn)  
  - Tra cứu qua URL template: `https://portal.vdsg-invoice.vn/invoice/download/{MTCuu}?type=pdf`  
  - Fetcher: `VdsgInvoicePdfFetcher` – đọc `MTCuu` từ XML/JSON.

- **0309612872** – Smartsign (Công ty CP Chữ Ký Số Vi Na)  
  - Tra cứu: `https://tracuuhd.smartsign.com.vn/` – “Mã tra cứu HĐ” + (nếu có captcha).  
  - Fetcher: `SmartsignInvoicePdfFetcher` – đọc mã tra cứu từ `cttkhac/ttkhac`.

- **0109282176** – Vininvoice  
  - Tra cứu API: `https://tracuu.vininvoice.vn/erp/rest/s1/iam-entry/invoices/{MCCQT}/pdf`  
  - Fetcher: `VininvoiceInvoicePdfFetcher` – đọc MCCQT/id từ `cttkhac/ttkhac` hoặc field trực tiếp.

- **0302712571** – Matbao-invoice  
  - Tra cứu: `https://matbao.in/tra-cuu-hoa-don/` (chế độ “Tra cứu dùng file XML”).  
  - Định hướng fetcher: upload file XML hóa đơn, chờ hệ thống hiển thị & tải PDF.

- **0312303803** – WinInvoice (portal `tracuu.wininvoice.vn`)  
  - Tra cứu: `https://tracuu.wininvoice.vn/` – dùng private_code + mã công ty (`cmpn_key`).  
  - Fetcher: `WinInvoicePdfFetcher` (hiện tại đang map theo MST người bán WinCommerce – xem phần dưới).

- **0108971656** – MyInvoice (MCCQT trong XML / payload)  
  - Fetcher: `MyinvoiceInvoicePdfFetcher`.

- **0315382923** – SES Group  
  - Fetcher: `SesGroupInvoicePdfFetcher`.

- **0315638251** – HT Invoice  
  - Fetcher: `HtInvoiceInvoicePdfFetcher`.

- **0101300842** – Einvoice (FPT / eInvoice tùy portal)  
  - Fetcher: `EinvoiceInvoicePdfFetcher`.

- **0106870211** – Vietinvoice  
  - Tra cứu: `https://tracuuhoadon.vietinvoice.vn/`  
  - Fetcher: `VietinvoiceInvoicePdfFetcher` – dùng trường `Mã tra cứu` từ payload.

- **0306731335** – thegioididong  
  - Tra cứu: `https://hddt.thegioididong.com/`  
  - Fetcher: `ThegioididongInvoicePdfFetcher` – dùng SĐT người mua + số hóa đơn/mã tra cứu, captcha, tải HĐ chuyển đổi (ZIP) rồi bóc PDF.

- **0306784030** – ehoadon.net  
  - Tra cứu: `https://{MST-nguoi-ban}.ehoadon.net/look-up-invoice`  
  - Fetcher: `EhoadonNetInvoicePdfFetcher` – chọn tra cứu theo tệp XML, upload XML, click tải PDF (hoặc bóc PDF từ ZIP).

### NCC theo MST người bán (`nbmst` / SellerTaxCode)

Những nhà cung cấp này được chọn **theo MST người bán**, không chỉ theo `msttcgp`.

- **0104918404** – WinCommerce / WinMart  
  - **Mặc định resolve**: `WinInvoicePdfFetcher` (portal `tracuu.wininvoice.vn`, `private_code` + `cmpn_key`).  
  - **Thêm**: `WinCommerceInvoicePdfFetcher` — portal `https://hoadon.winmart.vn/` (MCCQT), key registry `WIN-INVOICE`; không gắn `[InvoiceProvider]` vì cùng MST với WinInvoice (tránh hai fetcher cùng priority). Chỉ dùng khi tách route theo NCC/loại HĐ trong tương lai hoặc gọi trực tiếp theo key.

- **VNPT-MERCHANT** – VNPT merchant (các siêu thị LOTTE, v.v.)  
  - Fetcher: `MerchantVnptInvoiceFetcher`  
  - Một số cấu hình mẫu:
    - `0304741634-003` – LOTTE MART BDG – `https://lottemart-bdg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0304741634`     – LOTTE TỔNG  – `http://lottemart-nsg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0702101089`     – LOTTE MART NSG – `https://lottemart-nsg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0304741634-005` – LOTTE MART VTU – `https://lottemart-vtu-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0304741634-008` – LOTTE MART BDH – `https://lottemart-bdh-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0304741634-007` – LOTTE MART CTO – `https://lottemart-cto-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0304741634-011` – LOTTE MART NTG – `https://lottemart-ntg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0304741634-001` – LOTTE MART DNI – `https://lottemart-dni-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0304741634-002` – LOTTE MART BTN – `https://lottemart-btn-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - `0300555450`     – PETROLIMEX – `https://hoadon.petrolimex.com.vn/`

- **0312650437** – Grab (vn.einvoice.grab.com)  
  - Fetcher: `GrabInvoicePdfFetcher`  
  - Download PDF: `https://vn.einvoice.grab.com/Invoice/DowloadPdf?Fkey={Fkey}`  
  - `{Fkey}` lấy trực tiếp từ payload (field tên `Fkey` trong `cttkhac/ttkhac` hoặc `ttchung.Fkey`).

- **VNPT-PORTAL (định hướng)** – VNPT invoice portal theo MST người bán  
  - Ý tưởng: dùng 1 fetcher chung (ví dụ `VnptPortalInvoicePdfFetcher`) + service cấu hình URL theo `nbmst`:
    - Ví dụ: `0303752249` (Safoco) → `https://safoco-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
    - Ví dụ: `0302505776` (NewVietDairy) → `https://newvietdairy-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey`
  - TODO: khi implement đầy đủ, thêm chi tiết fetcher / config service vào đây.

### Ghi chú mở rộng

- Các fetcher mới nên:
  - Gắn `[InvoiceProvider(...)]` với `Key` = MST NCC hoặc MST người bán hoặc logical key.  
  - Ghi rõ tên NCC + URL tra cứu (nếu có) vào file này để dev khác dễ tra cứu.
- Với VNPT portal nhiều domain:
  - Nên lưu mapping MST người bán → URL portal trong một service cấu hình (DB hoặc JSON),  
    file này chỉ đóng vai trò “sổ tay” để biết MST nào đang trỏ tới domain nào.

