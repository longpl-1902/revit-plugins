# Luật trong rules.md — tóm tắt cho /test-design

Định dạng gốc: `BIM_KB_Base/profiles/revit-addin/skills/revit-test/references/suite-format.md` §2 (chỉ đọc khi cần chi tiết hơn bảng dưới).

```markdown
## Áp dụng cho: <số sheet, hoặc "mọi sheet">

### R-01 — <tên luật>
- AC: AC4 (K1)
- Kiểm: <điều kiện cụ thể, đo được>
- Kỳ vọng: <nguồn>: <giá trị>
- Cách đo: <file trong RUN_DIR\data + trường>
- Sai số: <±1 mm | không | chờ — câu hỏi cho người duyệt>
- Mức: FAIL nếu sai
- Máy chấm: <phép tính, cú pháp dưới>
- Ảnh minh chứng: <role / tên file ảnh | bỏ trống = ảnh sheet | không>

## Chỗ hở
| AC | Thiếu gì để đo | Khi nào có | Tạm thời |
```

**Nguồn kỳ vọng** (mở đầu trường Kỳ vọng): `theo spec (ACn): …` · `theo cài đặt (<file> → <khoá>): …` (đọc từ đúng file cấu hình lần chạy dùng) ·
`theo baseline (<file>)` (cần lần chạy purpose=baseline ở 4a) · `tương đối: …` (so trong cùng lần chạy) · `theo ảnh: …` (tối đa SUSPECT).

**Máy chấm** — nguồn: `<file trong data> → <đường dẫn JSON>[lọc]`, cài đặt: `cài đặt(<file>, <khoá>)`:

| Dạng | Nghĩa |
|---|---|
| `đếm(<nguồn>) [theo <khoá>] <op> <số>` | số phần tử (mỗi nhóm) so với số; op: = ≠ < ≤ > ≥ |
| `giao(<A>, <B>) theo <khoá> = 0` | diện tích chung hai bbox (mm²) trong cùng nhóm |
| `trong(<A>, <vùng>) theo <khoá> [± <mm>]` | bbox A nằm trọn trong vùng |
| `bằng(<giá trị>, <kỳ vọng>) [± <sai số>]` | so số / chuỗi |
| `thứ tự(<danh sách>) theo <khoá> = <danh sách kỳ vọng>` | đúng thứ tự |
| `khớp baseline(<file>, khoá <k>, trường <f…>) [± <sai số>]` | so với baseline\<file> |
| `có dòng(<log>, "<regex>")` · `không có dòng(<log>, "<regex>")` | log add-in đã cắt (data\addin.log) |
| `step xong(<mã step>)` | data\step_status.json |
| `ảnh(<điều cần nhìn>)` | agent nhìn ảnh; tối đa SUSPECT trừ khi mô tả được cụ thể |

Quy ước: ID R-xx ổn định, không đánh số lại, không dùng lại; luật bỏ → `### R-xx — <tên> · retired <YYYY-MM-DD>` + `- **Bỏ vì:** …`.
Đổi luật sau G2 = duyệt lại (hash suite khoá ở G2; test_run dừng nếu lệch).
