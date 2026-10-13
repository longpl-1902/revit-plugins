# revit-dev-flow — luồng BIM_KB_Base cho add-in Revit, ít token

Plugin Claude Code chạy luồng **bước 1 → 10, cổng G1 → G6, tier T0–T3** của BIM_KB_Base 0.8 nhưng chuyển phần
tất định sang máy: một MCP server C# (`hawee-flow`) giữ trạng thái, dựng tài liệu, chạy lint/test, chạy suite Revit
trọn vòng và chấm luật. Agent chỉ đọc "lát cắt" của bước đang làm và chỉ làm việc cần suy luận (khảo sát code, viết
nội dung spec/plan/luật, hỏi người, sửa code, nhìn ảnh).

Ước lượng nạp ngữ cảnh giảm 78–95% mỗi bước so với bộ skill cũ — chi tiết và cách đo thật: [docs/measurements.md](docs/measurements.md).

## Cài đặt

Yêu cầu: Windows, Claude Code, **.NET 8 SDK** (`dotnet`), Windows PowerShell 5.1; repo dự án có `hawee.project.yaml`
và `BIM_KB_Base/` (giữ nguyên định dạng — plugin đọc, không đổi).

```text
/plugin marketplace add longpl-1902/revit-plugins
/plugin install revit-dev-flow@revit-plugins
```

Lần đầu mở phiên, hook SessionStart dựng server vào `${CLAUDE_PLUGIN_DATA}/server` (`dotnet publish`, ~30 s; lần sau
chỉ dựng lại khi mã nguồn plugin đổi). Tool `hawee-flow` chưa hiện → gõ `/mcp` để kết nối lại.

Server dựng vào `server.next` rồi mới chép sang `server\`. Lúc mở phiên, Claude Code chạy MCP server song song với hook, nên
server **cũ** có thể đang giữ khoá DLL: hook đổi tên file đang bị khoá (`*.old-<số>`, dọn ở lần sau) rồi chép bản mới vào, xong nhắc
`/mcp` để nối lại. **Dựng lỗi** → hook in luôn các dòng `error` đầu tiên của `${CLAUDE_PLUGIN_DATA}\build.log`. Còn file không thay
được (phiên Claude Code khác đang chạy server) → đóng các phiên đó, hoặc
`Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | ? CommandLine -match 'Hawee.Flow.Server' | % { Stop-Process -Id $_.ProcessId }`,
rồi chạy `powershell -ExecutionPolicy Bypass -File "<thư mục plugin>\scripts\ensure-server.ps1" -Force` và `/mcp`.

**Chạy suite Revit (`test_run`)**: runner tự kết nối gateway `revit_test.mcp` (vd. `hawee`) và `revit_test.capture.tracking_mcp`
(vd. `bim-tracking`) như một MCP client (đọc `${VAR}` / `${VAR:-mặc định}` và `headers` như Claude Code). Thứ tự tìm cấu hình: biến `HAWEE_FLOW_MCP_<TÊN>` = JSON `{command, args, env}` /
`{url}` → `.mcp.json` ở gốc repo dự án → `.hawee-flow/mcp.json` → `~/.claude.json` (khai bằng `claude mcp add`, cấp local
của repo rồi cấp user). Khai server ở đâu cho Claude Code thì runner cũng tìm được, không cần chép. `desktop-commander` không còn cần cho agent.

Biến môi trường tuỳ chọn: `HAWEE_FLOW_REPO` (gốc repo), `HAWEE_INTEGRATION_BRANCH` (nhánh tích hợp, mặc định tự tìm
`origin/DEV`), `HAWEE_REVIT_TASKS_DIR`, `HAWEE_REVIT_DATASETS_DIR` (ghi đè `suites_dir` / `datasets_dir`),
`HAWEE_FLOW_POST_EDIT=1` (bật hook lint gọn sau khi sửa — xem Hook).

## Luồng: bước → lệnh → tool → cổng

| Bước | Lệnh (skill) | Tool máy làm | Dừng ở |
|---|---|---|---|
| mọi lúc | `process` (tự nạp) | `flow_next`, `flow_status` | — |
| 2 nạp ngữ cảnh | `/task-start` | `task_open`, `context_pack`, `rule_for`, `rule_get`, `kb_search` | Q0 tier |
| 3a–3c spec | `/spec` | `pack_render`, `pack_apply`, `doc_check`, `gate_submit` | mỗi gói câu hỏi, G1 |
| 3d sinh test | `/test-design` (`/plan` tự gọi) | `dataset_register`, `dataset_check`, `suite_scaffold`, `suite_lint`, `matrix_build` | bảng luật (G2 chặn khi AC đo bằng suite mà chưa có suite) |
| 4a–4c plan | `/plan` | `baseline_run`, `pack_render`, `pack_apply`, `doc_check`, `gate_submit` | gói 4a, walkthrough, G2 |
| 5–6 code | `/implement` | `check_all`, `deviation_add`, `rule_for` | lệch lớn, hết 5 vòng |
| 7 test Revit | `/verify` (subagent), `/revit-test` | `test_run`, `get_detail`, `record_verdict`, `diagnosis_set`, `matrix_build`, `issue_note_draft` | G3 |
| 8 DoD | `/dod` | `dod_report` | G4 |
| 9 review | `/review` | `review_packet` + agent `adversarial-reviewer` | G5 |
| 10 đóng | `/handoff` | `handoff_draft`, `ki_report` | G6 |
| bug B1–B9 | `/bug` | như trên, `task_open bug=true` | G1… |
| cổng | `/gate` (chỉ người gọi) | `gate_record` | — |

Mỗi bước: `flow_next` trả ≤ 15 dòng (việc, tool, điều kiện cổng, nhắc việc còn thiếu, điểm dừng) thay cho đọc
`steps.md` / `gates.md` / `tiers.md` / `spec-plan.md`.

## Máy làm gì, agent làm gì

| Máy (C#, có test) | Agent |
|---|---|
| status.yaml (sửa đúng dòng, giữ comment), DEC-YYYY-NNN, cổng + khoá hash suite ở G2 | khảo sát code, `file:dòng` |
| spec/plan dựng theo tier, gói câu hỏi YAML → markdown, áp "Q1: B" vào §14/§1.2/§12/§13 | viết nội dung spec/plan/luật, hỏi người |
| doc-lint gom theo mã, kiểm tham chiếu / phủ AC / bất biến / baseline | phần rà cần suy luận (agent independent-reviewer) |
| baseline, build + L0 so baseline theo tên test, lint, đếm vòng sửa, dod-report | sửa code |
| dataset (chép + băm), dựng suite, suite-lint, ma trận AC | chọn số đo, nguồn kỳ vọng |
| **test_run**: môi trường (AddInId trùng, DLL ký), Revit + bản sao, step có chặn tool cấm, tracking + điểm kiểm, cắt log, reconcile, chẩn đoán gợi ý, chấm "Máy chấm", evidence, report, dọn dẹp, test-summary | nhìn ảnh (`record_verdict`), xác nhận chẩn đoán (`diagnosis_set`), DECIDE mức night |
| gói review, handoff nháp, ki-report | review đối kháng, điền handoff |

"Máy chấm" theo **đúng cú pháp và ngữ nghĩa của `core/tools/rule-eval.ps1`** (Base 0.8, `suite-format.md` §2.1 "Máy thực thi"):
`đếm`, `giao`, `trong`, `bằng`, `thứ tự`, `khớp baseline`, `có dòng`, `không có dòng`, `step xong`. Mỗi phép một cặp `` ` ``,
nhiều phép nối bằng "và" (luật lấy verdict xấu nhất FAIL > AGENT > NOT_MEASURABLE > PASS); lọc `[Trường=…]`, `!=`,
`bắt đầu bằng "…"`, `chứa "…"`, `không chứa "…"` (so đúng từng ký tự, trên chính phần tử); bỏ tên file → file của phép trước,
không có thì file `.json` duy nhất trong `data\` có thuộc tính gốc trùng; `theo <khoá>` lấy khoá ở phần tử hoặc cha gần nhất,
nhóm rỗng vẫn tính (= 0). Còn chỗ trống `<…>`, cú pháp lạ, `ảnh(...)` → **cần agent**; thiếu file / baseline, ngưỡng `chờ` →
`NOT_MEASURABLE`; không bao giờ đoán thành `PASS`. Bộ test `RuleEvalConformanceTests` chép nguyên fixture của
`rule-eval.tests.ps1` — 26 luật phải ra cùng verdict.

Mở rộng của plugin (chỉ ở chỗ `rule-eval.ps1` ra AGENT, trừ `default_tol_mm`): tên loại trần `giao(Label, Keyplan)` (tìm theo
`type`/`kind`/… trong `*component*.json`), `cài đặt(<file>, <khoá>)` trong `bằng` / `thứ tự`, bbox dạng `{minX..maxY}`,
`{min:{x,y},max:{x,y}}`, `{x,y,width,height}`, `[x1,y1,x2,y2]`, `± x` ở `giao`, thêm sheet trong "Áp dụng cho" làm nhóm khi khoá là
`Sheet…`. Sai số khi luật không ghi `± x`: `revit_test.default_tol_mm`, **mặc định 0 như Base** (đổi feet → mm cho ra
99.99999997 → ghi `± 0.01` vào luật, `suite_lint` cảnh báo `rule-tol`). Verdict agent chấm bằng mắt chỉ được dùng lại ở lượt
sau khi luật **và ảnh / dữ liệu** không đổi.

**Lệch có chủ ý so với Base 0.8 (chạy bóng):** Base coi verdict agent trong `result.json` là chính thức và chạy
`rule-eval.ps1` bóng để so. Plugin coi verdict máy (C#) là chính thức để agent không phải chấm lại mọi luật, rồi ở bước 8b
gọi chính `rule-eval.ps1` của Base làm máy thứ hai (`data\rule-eval.json` + `rule-eval.md`, `run-info.json.grading.cross_check`):
luật hai máy chấm ngược nhau (PASS ↔ FAIL) → chuyển **cần agent**, ghi lại `result.json` / `report.md` kèm đề xuất. Base chưa có
`rule-eval.ps1` hoặc máy không có PowerShell → ghi "chưa chạy máy chấm", không chặn.

Định dạng file giữ nguyên để script cũ của Base vẫn dùng được: `status.yaml`, `suite.yaml`, `rules.md`,
`dataset.yaml`, `run-info.json`, `result.json`, `data\step_status.json`, `data\checkpoints\<step>\outputs.json`,
`data\reconcile.json`, `data\diagnosis.json`, `test-summary.json` (đã chạy thử với `host-test-reconcile.ps1` và
`test-summary.ps1` thật). Dữ liệu và kết quả chạy vẫn nằm ở `D:\RevitTest\datasets\<bộ>` và `D:\RevitTest\tasks\<suite>\runs\<thời điểm>\`.

## Hook

| Hook | Sự kiện | Làm gì |
|---|---|---|
| `plugin-guard.ps1` | PreToolUse `gate_record`, Bash, PowerShell | ghi cổng → **hỏi người**; `git commit` / `git push` → **hỏi người** (G4); force-push, `--no-verify` → chặn. Lỗi hook → chặn |
| `ensure-server.ps1` | SessionStart | dựng server khi cần, im lặng khi không có gì làm |
| `post-edit-compact.ps1` | PostToolUse Edit/Write | chỉ khi `HAWEE_FLOW_POST_EDIT=1`: cùng lint với `post-edit-check.ps1` của Base nhưng ≤ 8 dòng — bật thì **bỏ** hook gốc trong `.claude/settings.json` |

Hook `guard-protected-paths` / `post-edit-check` của dự án vẫn để ở `.claude/settings.json` như cũ (plugin không lặp).

**Luật an toàn trong runner** (`Policy.cs`, chặt hơn `revit-test-guard.ps1` của Base ở 2 chỗ):
- Revit hỏng khi khởi động (không có PID) → không gọi gì vào Revit; trước **mọi** step và sau step cuối, document đang mở phải nằm
  trong `RUN_DIR` (bản sao); tool runner tự quản (`tools.open_document / close_document / call / list_*`) không được làm step.
- `forbidden … when: "k=v"` mà args của step không có `k` → **chặn** (Base cho qua): ghi rõ giá trị an toàn, vd. `dry_run: true`.
- Luật nhìn ảnh: `record_verdict PASS` chỉ khi run có ảnh và note nêu đúng tên file ảnh đã xem.
- G2 PASS ghi `approved.json` vào folder suite; `/revit-test` chạy lẻ trên suite đã khoá mà luật đổi → chạy nhưng đánh dấu
  "LUẬT CHƯA DUYỆT"; ma trận AC (`matrix_build`) chỉ lấy run `purpose=task` của phiên kiểm chứng hiện tại.
- G2 không qua khi suite còn `*.proposed.*` (đổi tên thành bản chính trước); G1 cần `doc_check` gần nhất sạch.
An toàn khi chạy Revit (tool cấm, `allow_draw`, `setup_tools` có người duyệt, document phải nằm trong `RUN_DIR`, chỉ kill
đúng PID, hash model gốc trước = sau) nằm **trong runner** — agent không gọi tool ghi vào Revit nữa.

## Model

Theo quy ước của dự án: agent **phân tích** chạy Opus — `independent-reviewer` (rà spec / plan), `adversarial-reviewer`
(review diff); việc **thực thi** chạy Sonnet trong phiên con — `/verify` (vòng test → sửa → test), `/revit-test`.
Các lệnh còn lại chạy bằng model của phiên chính. Đổi: sửa `model:` trong frontmatter của file agent / skill.

## Giới hạn hiện tại

- Đã chạy thật lần đầu 2026-10-13 (mở Revit, bản sao model, 4 step preview). Phần còn lại vẫn kiểm bằng gateway / tracking / tiến trình giả (`src/Hawee.Flow.Tests`) — nên so với `/revit-test` cũ trên một suite đã có kết quả.
- Suite chỉ gọi tool preview / đọc (không chạy pipeline) → `capture: none` trong `suite.yaml`: không đọc tracking / điểm kiểm, không đối chiếu log; luật chấm trên `data\steps\NN-<tool>.json` (runner lưu kết quả tool của mọi step).
- Gateway và bim-tracking đang gọi qua MCP client (giao thức chuẩn). Có protocol pipe / HTTP API riêng thì thêm một `IToolClient`, phần còn lại không đổi.
- Tên tham số `run_timeline` / `run_outputs` dùng như skill cũ (`externalRunId`, `StepCode`, `Page`); hình dạng kết quả đọc linh hoạt theo tên khoá. Khác → sửa `JsonScan`.
- Chưa ghi run tracking cho **chính lần test** (`start_envelope_run` cần định danh dự án); run của add-in vẫn được đọc đầy đủ.
- `capture.source: sql` chưa hỗ trợ trong runner (báo để agent thu tay).
- `ensure-server` cần .NET 8 SDK trên máy chạy Claude Code.

## Phát triển

```text
cd plugins/revit-dev-flow/src
dotnet build
dotnet test                       # chạy được cả trên Linux
powershell -File ../hooks/plugin-guard.tests.ps1         # kiểm hook chặn commit / push
dotnet run --project Hawee.Flow.Server -- tools          # liệt kê tool
dotnet run --project Hawee.Flow.Server -- call flow_next "{}"   # gọi một tool từ dòng lệnh
```

Quy trình đổi → sửa `src/Hawee.Flow.Core/Flow/flow.yaml` (dự án ghi đè được bằng `.hawee-flow/flow.yaml`).
Không dùng ký hiệu ①–⑩ trong văn bản (lỗi font trên terminal) — test kiểm.
