---
name: implement
description: Bước 5–6 — code theo từng bước plan, test viết trước, check_all sau mỗi bước, tối đa 5 vòng sửa.
argument-hint: "[task] [bước n]"
disable-model-invocation: true
---

# /implement $ARGUMENTS

Điều kiện: G2 PASS (`flow_next` báo state code). Nhánh `<user>_<issueId>-<slug>` từ nhánh tích hợp (chưa có thì tạo; không commit).

Với mỗi bước ở plan §6 (theo thứ tự, không nhảy bước):
1. Đọc đúng khối bước đó (file + bản đôi R2022, thay đổi, test viết trước, lệnh kiểm + kỳ vọng số, dừng nếu).
2. `rule_for paths=<file sẽ sửa>` — áp các rule blocker/high (thread Revit, transaction, version API thấp nhất, idempotency).
3. Viết test trước, chạy, **thấy FAIL** (ghi tên test + kết quả). Không unit test được → làm theo cách kiểm thay ghi trong bước.
4. Code tối thiểu cho bước. Lớp Tính không dùng XYZ/BoundingBoxXYZ (dùng double/struct). File mới → thêm vào cả csproj bản đôi.
5. `check_all` (quick). FAIL → sửa đúng lỗi do diff (≤ 5 vòng, tool đếm). Không xoá / nới test, không thêm known_failures.
6. Cập nhật cùng MR: registry MCP + contract khi đổi tool, bản đồ code (dòng + hash), tài liệu theo §6.1.

Lệch plan → `deviation_add` (nhỏ: đi tiếp; lớn: tool đặt lại state, **DỪNG**). Hook chặn `protected_paths` — bị chặn thì báo người, không tìm đường vòng.
Code xong mọi bước mới chạy Revit: task đụng Revit → `/verify`; không → `/dod`.
