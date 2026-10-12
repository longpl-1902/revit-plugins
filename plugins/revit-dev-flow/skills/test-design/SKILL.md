---
name: test-design
description: Bước 3d — sinh suite.yaml + rules.md từ AC của spec đã qua G1 và bộ dữ liệu; bảng luật duyệt ở G2.
argument-hint: "[task] [--from <đường dẫn dữ liệu> | --dataset <bộ>]"
disable-model-invocation: true
---

# /test-design $ARGUMENTS

Máy làm phần cơ học, agent viết luật. Định dạng luật + cú pháp Máy chấm: [references/rules-cheatsheet.md](references/rules-cheatsheet.md).

1. **Dữ liệu**: `--from` → `dataset_register` (hỏi người `by` + `classification` nếu chưa nói). `--dataset` → `dataset_check`.
   Không có cả hai → hỏi một lần: "đường dẫn bộ dữ liệu, hay dùng bộ nào trong kho?". Không đưa dữ liệu vào repo.
2. **Đọc spec** §4 (K), §5 (AC, Đo bằng), §6 (dữ liệu + điểm kiểm), §7 (tool + tham số), §8 (thời gian). Danh mục đo của dự án: file `revit_test.measures`.
3. **Suite**: `suite_scaffold` (một suite cho mỗi tổ hợp bộ dữ liệu × biến thể × chuỗi bước; hậu tố `bien`, `baseline`…).
   Sửa `suite.yaml`: steps theo spec §7, đường dẫn `'{dataset}\…'` (nháy đơn), timeouts, `manual_steps`.
   Cần sửa model giữa chừng → step `purpose: setup` + `why`, tool phải có trong `revit_test.setup_tools`; không có → Chỗ hở.
4. **Luật**: mỗi AC đo bằng suite → ≥ 1 luật R-xx đủ trường; ngưỡng/sai số spec không nói → `chờ` + câu hỏi (P-001). Không đo được → Chỗ hở, tối đa SUSPECT.
   Thêm gói luật chung của dự án cho tool có trong steps (giữ ID gói). Suite đã có → sửa `*.proposed.*`, giữ ID, luật bỏ ghi `retired`.
5. `suite_lint` tới khi chỉ còn luật `chờ`. `matrix_build` → test-design.md (AC không phủ, luật chờ, cần baseline).
   Ghi thêm vào test-design.md (ngoài khối máy sinh): kịch bản K → suite | không áp dụng | thủ công; câu hỏi `chờ` + đề xuất + mặc định; số lần chạy ước lượng.
6. **DỪNG** — trả lời 5–10 dòng: bộ dữ liệu (mới / dùng lại, hash khớp), số suite + luật, AC phủ / chỗ hở, câu `chờ` cần trả lời. Bảng luật duyệt ở G2 cùng plan.
