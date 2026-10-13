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

**Nguồn dữ liệu** (trong `RUN_DIR\data`): `steps\NN-<tool>.json` = kết quả tool của step thứ NN (runner luôn lưu, kể cả step lỗi:
`{ ok, text }`) — dùng cho tool trả kết quả trực tiếp (preview, đọc); `checkpoints\<STEP>\…` = output pipeline tải từ tracking;
`addin.log`; `step_status.json`. Tên trường: xem `get_detail kind=steps` sau lần chạy đầu.

**Viết để máy đọc được** (`rule-eval.ps1` của Base và bộ chấm của plugin cùng luật):
- mỗi phép trong một cặp `` ` ``, nhiều phép nối bằng "và";
- tham số là **đường dẫn dữ liệu hoặc giá trị**, không phải câu văn (`ViewName tờ 03/04/06 bắt đầu bằng` → máy ghi cần agent);
- lọc trong `[...]`: `Trường=giá trị`, `Trường!=giá trị`, `Trường bắt đầu bằng "…"`, `Trường chứa "…"`, `Trường không chứa "…"`, cách nhau bằng dấu phẩy; so đúng hoa thường;
- bỏ tên file → file của phép trước trong cùng dòng, không có thì file `.json` duy nhất trong `data\` có thuộc tính gốc trùng đoạn đầu;
- `theo <khoá>`: khoá lấy ở chính phần tử hoặc cha gần nhất; nhóm không có phần tử nào vẫn tính (= 0);
- so số thực (`bằng`, `trong`, `khớp baseline`) không ghi `± x` là so đúng tuyệt đối → ghi `± <sai số>` (feet → mm lệch ~1e-8).

Còn `<…>`, ngưỡng `chờ`, thiếu file / baseline → cần agent / NOT_MEASURABLE, không bao giờ thành PASS.

Quy ước: ID R-xx ổn định, không đánh số lại, không dùng lại; luật bỏ → `### R-xx — <tên> · retired <YYYY-MM-DD>` + `- **Bỏ vì:** …`.
Đổi luật sau G2 = duyệt lại (hash suite khoá ở G2; test_run dừng nếu lệch).
