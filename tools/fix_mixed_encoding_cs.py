# -*- coding: utf-8 -*-
"""Replace known Latin-1 + ? fragments so .cs files are valid UTF-8 with Vietnamese."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

BG = ROOT / "src/SmartInvoice.Infrastructure/Services/BackgroundJobService.cs"
INV = ROOT / "src/SmartInvoice.Infrastructure/Services/InvoiceDetailViewService.cs"


def main() -> None:
    b = BG.read_bytes()
    reps: list[tuple[bytes, str]] = [
        (b'Kh\xf4ng t\xecm th?y job ngu?n.', "Không tìm thấy job nguồn."),
        (
            b'Kh\xf4ng c\xf3 h\xf3a ??n l?i ?? ch?y l?i cho ch? ?? n\xe0y.',
            "Không có hóa đơn lỗi để chạy lại cho chế độ này.",
        ),
        (b'C\xf4ng ty kh\xf4ng t?n t?i.', "Công ty không tồn tại."),
        (
            b'Ch? job ??ng b? h\xf3a ??n (ho?c ch?y l?i chi ti?t) m?i c\xf3 danh s\xe1ch l?i chi ti?t.',
            "Chỉ job đồng bộ hóa đơn (hoặc chạy lại chi tiết) mới có danh sách lỗi chi tiết.",
        ),
        (
            b'Description = $"Ch?y l?i chi ti?t ({distinctIds.Count} H?)",',
            'Description = $"Chạy lại chi tiết ({distinctIds.Count} HĐ)",',
        ),
        (b'Job ngu?n thi?u th? m?c XML.', "Job nguồn thiếu thư mục XML."),
        (
            b'Job ngu?n kh\xf4ng c\xf3 b??c t?i XML ?? ch?y l?i.',
            "Job nguồn không có bước tải XML để chạy lại.",
        ),
        (
            b'Ch? job t?i PDF h\xe0ng lo?t m?i c\xf3 danh s\xe1ch l?i PDF.',
            "Chỉ job tải PDF hàng loạt mới có danh sách lỗi PDF.",
        ),
        (
            b'Description = $"Ph?c h?i SCO (m\xe1y t\xednh ti?n) {(options.IsSold ? "B\xe1n ra" : "Mua v\xe0o")} {from:dd/MM/yyyy} - {to:dd/MM/yyyy}",',
            'Description = $"Phục hồi SCO (máy tính tiền) {(options.IsSold ? "Bán ra" : "Mua vào")} {from:dd/MM/yyyy} - {to:dd/MM/yyyy}",',
        ),
        (b'SCO recovery: payload kh\xf4ng h?p l?.', "SCO recovery: payload không hợp lệ."),
        (b'Dong bo lai SCO that bai.', "Đồng bộ lại SCO thất bại."),
        (
            b'job.LastError = $"T?i PDF xong nh\xfdng kh\xf4ng t?o \xf0\xfd?c file ZIP: {ex.Message}";',
            'job.LastError = $"Tải PDF xong nhưng không tạo được file ZIP: {ex.Message}";',
        ),
        (
            b'var detail = failDetails.Count > 0 ? $" V\xed d? l?i: {string.Join(" | ", failDetails)}" : string.Empty;',
            'var detail = failDetails.Count > 0 ? $" Ví dụ lỗi: {string.Join(" | ", failDetails)}" : string.Empty;',
        ),
        (
            b'job.LastError = $"C\xf3 {failCount} h\xf3a \xf0\xf5n t?i PDF th?t b?i.{detail}";',
            'job.LastError = $"Có {failCount} hóa đơn tải PDF thất bại.{detail}";',
        ),
        (
            b'return "L?i kh\xf4ng x\xe1c \xf0?nh khi ch?y job n?n.";',
            'return "Lỗi không xác định khi chạy job nền.";',
        ),
    ]
    for old, new in reps:
        nb = new.encode("utf-8")
        if old not in b:
            raise SystemExit(f"Missing pattern in BackgroundJobService: {old!r}")
        b = b.replace(old, nb)
    BG.write_bytes(b)

    b2 = INV.read_bytes()
    old_inv = (
        b'/// L?y HTML xem h\xf3a ??n: g?i API detail, fill template C26TAA-31 (token), '
        b'ghi file v\xe0o th? m?c t?m, tr? v? ???ng d?n file.'
    )
    new_inv = (
        "/// Lấy HTML xem hóa đơn: gọi API detail, fill template C26TAA-31 (token), "
        "ghi file vào thư mục tạm, trả về đường dẫn file."
    ).encode("utf-8")
    if old_inv not in b2:
        raise SystemExit("Missing pattern in InvoiceDetailViewService")
    b2 = b2.replace(old_inv, new_inv)
    INV.write_bytes(b2)

    # Verify UTF-8
    BG.read_text(encoding="utf-8")
    INV.read_text(encoding="utf-8")
    print("OK: BackgroundJobService + InvoiceDetailViewService are valid UTF-8")


if __name__ == "__main__":
    main()
