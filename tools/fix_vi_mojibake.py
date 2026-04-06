"""Fix corrupted Vietnamese (CP1252-style mojibake: Ğ→Đ, Ở in ơ words, Þ→Á, etc.) in src/."""
from pathlib import Path

# Longest matches first.
SUBS = [
    ("HÀNG NÞT THAO TÞC", "HÀNG NÚT THAO TÁC"),
    ("Þp dụng", "Áp dụng"),
    ("gỞi nhỞ", "gợi nhớ"),
    ("Nội dung điỞu chỉnh", "Nội dung điều chỉnh"),
    ("Ğã bị điỞu chỉnh", "Đã bị điều chỉnh"),
    ("Ğã điỞu chỉnh", "Đã điều chỉnh"),
    ("ĞiỞu chỉnh", "Điều chỉnh"),
    ("BỞ lỞc", "Bỏ lọc"),
    ("bỞ viỞn", "bỏ viền"),
    ("hủy bỞ", "hủy bỏ"),
    ("chỞn", "chọn"),
    ("lỞc", "lọc"),
    ("lựa chỞn", "lựa chọn"),
    ("tùy chỞn", "tùy chọn"),
    ("tiỞn", "tiền"),
    ("điỞu", "điều"),
    ("NgưỞi", "Người"),
    ("ngưỞi", "người"),
    ("HĞ", "HĐ"),
    ("ĞiỞn", "Điền"),
    ("ĞỞc", "Đọc"),
    ("Ğông", "Đông"),
    ("VNĞ", "VNĐ"),
    ("Ğơn", "Đơn"),
    ("Ğảm", "Đảm"),
    ("Ğợi", "Đợi"),
    ("Ğến", "Đến"),
    ("Ğã ", "Đã "),
    ("Ğã hủy", "Đã hủy"),
    ("Ğã bị", "Đã bị"),
    ("Ğồng bộ lại", "Đồng bộ lại"),
    ("Ğồng bộ", "Đồng bộ"),
    ("Ğang thực hiện", "Đang thực hiện"),
    ("Ğang tải", "Đang tải"),
    ("Ğang ", "Đang "),
    ("Ğóng", "Đóng"),
    ("BỞ ", "Bỏ "),
    ("bỞ qua", "bỏ qua"),
    ("bỞ)", "bỏ)"),
    ("bỞ ", "bỏ "),
    ("vỞ", "về"),
    ("nỞn", "nền"),
    ("gỞi", "gọi"),
    ("gỞn", "gọn"),
    ("truyỞn", "truyền"),
    ("nhiỞu", "nhiều"),
    ("thỞi", "thời"),
    ("đỞc", "đọc"),
    ("đảo chiỞu", "đảo chiều"),
    ("chiỞu", "chiều"),
    ("dỞc", "dọc"),
    ("viỞn", "viền"),
    ("mỞ đi", "mờ đi"),
    ("mỞi ", "mọi "),
    ("mỞi TTin", "mọi TTin"),
    ("tiêu đỞ", "tiêu đề"),
    ("đưỞng", "đường"),
    ("ĐưỞng", "Đường"),
    ("trưỞng", "trường"),
    ("TrưỞng", "Trường"),
    ("điỞn", "điền"),
    ("thưỞng", "thường"),
    ("ChỞn navigation", "Chờ navigation"),
    ("ChỞ popup", "Chờ popup"),
    ("ChỞn fetcher", "Chọn fetcher"),
    ("ChỞn radio", "Chọn radio"),
    ("ChỞn ", "Chọn "),
    ("NÞT", "NÚT"),
    ("TÞC", "TÁC"),
    # Remaining Latin capital (wrong encoding for Đ)
    ("Ğ", "Đ"),
]


def main() -> None:
    root = Path(__file__).resolve().parents[1] / "src"
    exts = {".cs", ".xaml"}
    for p in sorted(root.rglob("*")):
        if p.suffix.lower() not in exts:
            continue
        try:
            text = p.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            print("skip (not utf-8)", p)
            continue
        orig = text
        for a, b in SUBS:
            text = text.replace(a, b)
        if text != orig:
            p.write_text(text, encoding="utf-8", newline="\n")
            print("updated", p.relative_to(root.parent))


if __name__ == "__main__":
    main()
