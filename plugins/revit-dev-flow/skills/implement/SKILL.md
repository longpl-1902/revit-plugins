---
name: implement
description: Bước 5–6 — code theo từng bước plan, test viết trước, check_all sau mỗi bước, tối đa 5 vòng sửa. Chỉ repo add-in Revit.
argument-hint: "[task] [bước n]"
disable-model-invocation: true
---

# /implement $ARGUMENTS

> Chỉ dùng khi phát triển add-in Revit (repo có `hawee.project.yaml`, `profiles` có `revit-addin`). Repo / việc khác → báo người plugin này không áp dụng, không làm theo.

Điều kiện: G2 PASS (`flow_next brief=true` báo state code). Nhánh `<user>_<issueId>-<slug>` từ nhánh tích hợp (chưa có thì tạo; không commit).

Với mỗi bước ở plan §6 (theo thứ tự, không nhảy bước):
1. Đọc đúng khối bước đó (file + bản đôi R2022, thay đổi, test viết trước, lệnh kiểm + kỳ vọng số, dừng nếu).
2. `rule_for paths=<file sẽ sửa>` — áp các rule blocker/high (thread Revit, transaction, version API thấp nhất, idempotency).
3. Viết test trước, chạy, **thấy FAIL** (ghi tên test + kết quả). Không unit test được → làm theo cách kiểm thay ghi trong bước.
4. Code tối thiểu cho bước. Lớp Tính không dùng XYZ/BoundingBoxXYZ (dùng double/struct). File mới → thêm vào cả csproj bản đôi.
5. `check_all` (quick). FAIL → sửa đúng lỗi do diff (≤ 5 vòng, tool đếm). Không xoá / nới test, không thêm known_failures.
6. Cập nhật cùng MR: registry MCP + contract khi đổi tool, bản đồ code (dòng + hash), tài liệu theo §6.1.

Lệch plan → `deviation_add` (nhỏ: đi tiếp; lớn: tool đặt lại state, **DỪNG**). Hook chặn `protected_paths` — bị chặn thì báo người, không tìm đường vòng.
Code xong mọi bước mới chạy Revit: task đụng Revit → `/verify`; không → `/dod`.

**Kiểm trong Revit chỉ qua suite + `test_run`** — runner tự mở Revit, mở bản sao model, chạy step, chấm, đóng Revit.
Bước plan ghi "chạy thử trong Revit" / "gọi lệnh thật 2 lần" → **không** tự gọi tool Revit, **không** bảo người mở Revit:
thêm step vào suite của task (chưa có → `suite_scaffold` + `dataset_register` cho model; step preview rồi lệnh thật ×2,
luật `tương đối: chạy lại không tạo trùng`) rồi `/revit-test <suite>` (giữa chừng) hoặc `/verify`. Không thấy tool `test_run` →
hawee-flow chưa nối: báo người gõ `/mcp` (kèm dòng lỗi hook nếu có) và DỪNG. Chỉ nhờ người khi `manual_steps` cần mắt / thao tác UI.
