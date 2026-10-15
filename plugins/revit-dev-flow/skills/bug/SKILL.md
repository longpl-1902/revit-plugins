---
name: bug
description: Luồng bug B1–B9 — bằng chứng, phân loại, spec ba khối, test tái hiện FAIL trước khi sửa; cùng các cổng như task. Chỉ repo add-in Revit.
argument-hint: "<issueId> [slug] | hotfix"
disable-model-invocation: true
---

# /bug $ARGUMENTS

> Chỉ dùng khi phát triển add-in Revit (repo có `hawee.project.yaml`, `profiles` có `revit-addin`). Repo / việc khác → báo người plugin này không áp dụng, không làm theo.

Chưa có folder: `task_open bug=true tier=T1` (bugfix-spec dựng sẵn). Gọi `flow_next brief=true` đầu mỗi phiên.

- **B1 bằng chứng** (spec §1): issue, ảnh, version, dữ liệu, bước thao tác; log → dòng lỗi **đầu tiên** trong khoảng thời gian (không lấy lỗi cuối). Dữ liệu khách chỉ khi được phép.
- **B2 phân loại**: Bug / Defect(GapBA → chuyển BA, dừng) / Not bug / input. `kb_search` theo triệu chứng + vùng code; cùng nguyên nhân KI cũ → **lặp**: ghi `Lặp: lần n`, chuẩn bị postmortem.
- **B3 ba khối** (spec §3): Hiện tại / Mong đợi / Không đổi. Hành vi đúng không rõ → `questions/pack-1.yaml` (doc: bug) → `pack_render`, **DỪNG**, `pack_apply`.
- **B4 tái hiện bằng test** (bắt buộc trước khi sửa): ưu tiên L0 hàm thuần → L1 → L2 → case suite L3. Tên/trait gắn issue. Chạy → phải **FAIL**, ghi §4.
  Không tái hiện được → ghi đã thử gì, giả thuyết + cách kiểm, **DỪNG**.
- **G1**: `gate_submit G1` → người xác nhận hành vi đúng + khối Không đổi (`/gate G1`).
- **B5 nguyên nhân gốc**: `file:dòng` + 5 whys ngắn + Loại; grep chỗ cùng mẫu (ghi lệnh); lặp → vì sao kiểm cũ không chặn. Không chắc → **DỪNG**, trình giả thuyết.
- **B6 plan** `/plan` (T1 lite): bất biến = khối Không đổi; bước 1 = test B4. **B7 sửa** `/implement`. **B8** kiểm ba mặt (tái hiện · đã sửa · không hồi quy, + Revit nếu có) → verdict `verified | partial | failed | not-run`.
- **B9 đóng** `/handoff`: KI có Test hồi quy = tên test B4; lặp / high → postmortem + đề xuất rule draft / lint.

Hotfix: B1, B3, B4 tối thiểu (tái hiện tay có ảnh/log), G1 qua chat ghi tên + giờ, sửa, G4/G5; tạo issue nợ B5–B9 hạn 1 tuần (DEC). Lỗi lặp không được hotfix.
