# -*- coding: utf-8 -*-
"""Fix mixed Latin-1 + UTF-8 fragments in BackgroundJobService.cs (invalid UTF-8)."""
from pathlib import Path

p = Path(__file__).resolve().parents[1] / "src/SmartInvoice.Infrastructure/Services/BackgroundJobService.cs"
b = p.read_bytes()

# (bad bytes substring, good UTF-8 text)
REPLACEMENTS: list[tuple[bytes, str]] = [
    (b'Kh\xf4ng t\xecm th?y job ngu?n.', "Không tìm thấy job nguồn."),
    (b'Kh\xf4ng c\xf3 h\xf3a ??n l?i ?? ch?y l?i cho ch? ?? n\xe0y.', "Không có hóa đơn lỗi để chạy lại cho chế độ này."),
    (b'C\xf4ng ty kh\xf4ng t?n t?i.', "Công ty không tồn tại."),
    (
        b'Ch? job ??ng b? h\xf3a ??n (ho?c ch?y l?i chi ti?t) m?i c\xf3 danh s\xe1ch l?i chi ti?t.',
        "Chỉ job đồng bộ hóa đơn (hoặc chạy lại chi tiết) mới có danh sách lỗi chi tiết.",
    ),
    (b'Ch?y l?i chi ti?t ({distinctIds.Count} H?)', "Chạy lại chi tiết ({distinctIds.Count} HĐ)"),
    (b'Job ngu?n thi?u th? m?c XML.', "Job nguồn thiếu thư mục XML."),
    (
        b'Job ngu?n kh\xf4ng c\xf3 b??c t?i XML ?? ch?y l?i.',
        "Job nguồn không có bước tải XML để chạy lại.",
    ),
    (
        b'Ch? job t?i PDF h\xe0ng lo?t m?i c\xf3 danh s\xe1ch l?i PDF.',
        "Chỉ job tải PDF hàng loạt mới có danh sách lỗi PDF.",
    ),
    (b'Ph?c h?i SCO (m\xe1y t\xednh ti?n) {(options.IsSold ? "B\xe1n ra" : "Mua v\xe0o")}', None),  # special
]

# Handle Description f-string separately — build from parts
old_desc = (
    b'Description = $"Ph?c h?i SCO (m\xe1y t\xednh ti?n) {(options.IsSold ? "B\xe1n ra" : "Mua v\xe0o")} '
    b'{from:dd/MM/yyyy} - {to:dd/MM/yyyy}",'
)
new_desc = (
    'Description = $"Phục hồi SCO (máy tính tiền) {(options.IsSold ? "Bán ra" : "Mua vào")} '
    '{from:dd/MM/yyyy} - {to:dd/MM/yyyy}",'
).encode("utf-8")

extra: list[tuple[bytes, str]] = [
    (old_desc, new_desc.decode("utf-8")),
    (b'SCO recovery: payload kh\xf4ng h?p l?.', "SCO recovery: payload không hợp lệ."),
    (b'Dong bo lai SCO that bai.', "Đồng bộ lại SCO thất bại."),
    (
        b'job.LastError = $"T?i PDF xong nh\xfng kh\xf4ng t?o ?c file ZIP: {ex.Message}";',
        None,
    ),
]

# Build replacements list without None
pairs = [(a, b) for a, b in REPLACEMENTS if b is not None]
# LastError line - find exact bytes
for line in b.split(b"\n"):
    if b"T?i PDF xong" in line and b"ZIP" in line:
        print("ZIP line repr:", repr(line[:160]))
        break
