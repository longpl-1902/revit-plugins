# Số đo chi phí nạp ngữ cảnh — BIM_KB_Base 0.8 (trước khi có hawee-flow)

Đo ngày 2026-10-12 trên bản BIM_KB_Base người dùng gửi. Cách đo: đếm **ký tự** của SKILL.md và các file mà skill
dẫn agent đọc ở từng bước; quy đổi token ước lượng **≈ 3 ký tự tiếng Việt / token** (khoảng 2,5–3,5).
Đây là **ước lượng tĩnh** — số thật đo bằng `/context` trong Claude Code sau mỗi bước trên máy dự án.

| Bước | File nạp | Ký tự | Token ước |
|---|---|---|---|
| Mô tả 11 skill (luôn trong context, mỗi lượt) | frontmatter `description` | 5 725 | ~2K |
| Bước 2 task-start | SKILL, hawee.project.yaml, AGENTS.md, steps, preflight, tiers, conventions, kb/INDEX, mẫu status/spec/plan | 70 508 | 20–28K |
| Bước 3 spec | SKILL, spec-plan, mẫu spec, question-pack, decision-log, tiers, /analyze, README profile | 72 147 | 21–29K |
| Bước 3d test-design | SKILL, suite-format, mẫu suite/rules, test-dataset, testing, ADR-0002 | 44 905 | 13–18K |
| Bước 4 plan | SKILL, spec-plan, mẫu plan, tiers, README profile, /analyze, gates | 78 534 | 22–31K |
| Bước 6/8 dod | SKILL, dod.md, README profile | 20 187 | 6–8K |
| Bước 7 verify + revit-test | 2 SKILL, suite-format, testing, night-report | 72 063 | 21–29K, cộng 30–60 lời gọi MCP mỗi lần chạy × tối đa 5 lần |
| Bước 10 handoff | SKILL, mẫu handoff, known-issue | 6 537 | ~2K |
| Rule khi đọc cả file | general, mcp, revit-api, geometry-mep, performance, principles | 50 616 | 15–20K |

Một phiên đi bước 2 → 4 (bỏ phần trùng): ≈ 188K ký tự ≈ 54–75K token **trước khi đọc dòng code nào**.
Mẫu spec/plan còn đi qua context hai lần (đọc mẫu + ghi file). Mỗi gói câu hỏi thêm một lượt và context dài dần.

## Cách đo lại trên máy dự án

1. Mở phiên mới, chạy bước cần đo (vd. `/task-start 123 abc`), gõ `/context` — ghi "Messages" và "Skills".
2. Lặp lại với plugin `revit-dev-flow` cùng task.
3. Ghi kết quả vào bảng "So sánh" ở cuối file này.

## So sánh (điền sau khi đo)

| Bước | Cũ (/context) | Mới (/context) | Giảm |
|---|---|---|---|
