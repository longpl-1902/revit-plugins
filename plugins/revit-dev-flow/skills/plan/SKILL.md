---
name: plan
description: Bước 4a–4c — plan để code được ngay, làm cùng DEV / Tech Lead; dừng ở gói 4a, walkthrough và G2.
argument-hint: "[task] | trả lời gói 4a"
disable-model-invocation: true
---

# /plan $ARGUMENTS

Gọi `flow_next brief=true` trước (điều kiện vào: G1 PASS/CONCERNS). Plan đã dựng sẵn theo tier — chỉ điền.
Task chạy trong Revit + spec có AC đo bằng suite mà chưa có suite → `/test-design` trước.

**4a — thiết kế cùng DEV**
1. `baseline_run` trên commit gốc của nhánh tích hợp (tự điền §1.3, lưu baseline.json). Fail sẵn có phải có KI ID.
   Có luật "theo baseline" (matrix_build báo) → chạy suite `test_run purpose=baseline` trên build commit gốc.
2. §3 phạm vi ảnh hưởng: file + bản đôi R2022; **caller tìm thật** (`grep -rn "<Hàm>(" --include=*.cs`, ghi lệnh); `rule_for paths=<file sẽ sửa>`.
3. §2 ≥ 2 phương án (đúng AC / phạm vi / rủi ro / công sức S-M-L / test được / lùi được). §4 rủi ro (T3: điểm = xác suất × tác động).
4. §5 thiết kế nháp: luồng `file:Hàm`, chữ ký trước → sau, lớp Đọc/Tính/Ghi (lớp Tính dùng double/struct để test L0), thread, transaction, lỗi & log (logger + tracking), bất biến §5.6 (mỗi cái một test canh).
5. `questions/pack-4a.yaml` (doc: plan): chọn phương án, chỗ đặt code, đổi chữ ký hay overload, bất biến DEV biết, **mức tự chủ** (`sets: autonomy`). `pack_render`. **DỪNG.** Trả lời → `pack_apply` (ghi §1.2, autonomy).

**4b — bước code & walkthrough**: §6 mỗi bước một khối 6 dòng (file + bản đôi · thay đổi · test viết trước · build/test sau bước với số so baseline · dừng nếu · commit dự kiến).
Bước 1 = characterization test khi code chưa có test; bug: bước đầu = test tái hiện FAIL. §6.1 việc đi kèm (mỗi dòng làm/không + lý do), §7 ma trận AC → bước → test, §8–§11.
Trình walkthrough: người sẽ code trả lời từng bước "code được không cần hỏi lại? thiếu gì?". Ghi §12. **DỪNG.**

**4c — rà**: `doc_check plan` tới khi không còn CRITICAL/HIGH; rà độc lập theo tier (agent `independent-reviewer`). `gate_submit G2` (máy kiểm baseline, luật chờ, mức tự chủ).
**DỪNG** — trình G2: checklist (baseline số thật, walkthrough, bảng luật 3d không còn chờ, mức tự chủ). Người duyệt `/gate G2 … ` (tool tự khoá hash suite).

Trong lúc code phát hiện plan sai: lệch nhỏ ghi §13 rồi đi tiếp; lệch lớn (đổi chữ ký có caller ngoài file, đổi hành vi, đụng contract / cấu hình dùng chung, kịch bản mới) → dừng, quay G2/G1.
