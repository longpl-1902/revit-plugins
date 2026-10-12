---
name: verify
description: Bước 6–7 — check_all, chạy suite trong Revit bằng runner, vòng tự sửa theo test-summary (FIX / PASS / STOP_G3 / STOP_LIMIT), gửi issue khi mọi tiêu chí PASS; dừng ở G3.
argument-hint: "[task] [--autonomy ask|night]"
disable-model-invocation: true
context: fork
---

# /verify $ARGUMENTS

Chạy trong subagent; cuối cùng trả về ≤ 8 dòng cho phiên chính. Không commit / push. Không sửa `rules.md`, `baseline\`, suite, dữ liệu, `known_failures`, ngưỡng.

Mỗi lượt n = 0, 1, …:
1. `check_all` (quick) — chưa xanh → sửa lỗi do diff (≤ 5 vòng), không xanh được → dừng, báo.
2. Với mỗi suite trong `flow_status`: `test_run suite=<…> task=<task> build=true` (Revit phải đang đóng; runner tự build, kiểm DLL + chữ ký, chạy trọn vòng). Không gọi lại khi đang chạy.
3. Đọc kết quả runner:
   - **Cần agent chấm** → `get_detail kind=rule ref=R-xx`, mở ảnh bằng Read, `record_verdict` (PASS phải nói rõ đã kiểm gì; còn nghi → SUSPECT).
   - **Chẩn đoán** (máy chỉ gợi ý) → `get_detail kind=log` quanh dòng lỗi đầu; xác nhận hoặc sửa bằng `diagnosis_set`
     (CODE chỉ khi có `file:dòng`; in_task_diff=true chỉ khi file đó nằm trong diff của task; thuộc KI → ghi ki).
     FAIL không kèm lỗi log → tự chẩn đoán từ dữ liệu (`get_detail kind=rule`) rồi `diagnosis_set`.
   - **test-summary**: `PASS` → bước 4 · `FIX` → sửa tối thiểu đúng `file:dòng` chẩn đoán (ghi plan §13 nếu lệch nhỏ), về bước 1 với n + 1 ·
     `STOP_G3` / `STOP_LIMIT` → bước 4. Không sửa theo giả thuyết; không chạy quá số lượt.
   - Mức `night`: `DECIDE` → chọn theo thứ tự quyết định đã chốt > ràng buộc spec > hành vi baseline > thay đổi ít nhất, ghi DEC-AUTO-n vào
     `night-report-<ngày>.md` (≤ 2 mỗi phiên); `FIX scope=out_of_task` → sửa tách riêng + tìm thật mọi nơi gọi + chạy test phạm vi đó; `scope=test_data` → biến thể dữ liệu mới.
4. `matrix_build` (điền cột Kết quả theo AC). PASS mọi suite → `issue_note_draft`; đủ điều kiện thì upload ảnh + ghi chú + đổi trạng thái bằng MCP issue tracker
   (chỉ sang `issue_tracker.on_pass.status`, chỉ khi trạng thái hiện tại thuộc `from_status`). `gate_submit G3`.
5. Trả về: kết luận (PASS / dừng ở G3 vì … / hết lượt), số lượt sửa + đã sửa gì `file:dòng`, lỗi còn lại + loại + ai quyết, issue đã gửi chưa, đường dẫn test-summary.md.
