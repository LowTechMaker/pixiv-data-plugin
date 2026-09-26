# Pixiv Authors：agent 開工規則

先閱讀本 repo 的 [架構契約](ARCHITECTURE.md) 與 [交接文件](HANDOFF.md)。本 repo 可以獨立 checkout，不以 sibling 宿主原始碼作為建置或測試前置條件。

- 保留既有與未提交變更。只操作本 repo，不替其他插件、宿主 commit、push 或發佈。
- 先透過工作區 graft graph 取得上下文；工具不可用時記錄限制，再讀目標檔案。搜尋優先 `rg`，修改優先 `apply_patch`，本機 shell 命令加 `rtk` 前綴。
- 公開入口與兩個既有 public parser 是相容性契約。不得為了整理目錄更改它們的 namespace、signature、provider ID 或 assembly 名稱。此 repo 目前以 `SceneGallery.PluginSdk` 套件 `1.3.0` 為基線，含 `ITagDictionaryProvider`；SDK runtime assembly identity 仍是 `1.0.0.0`。
- HTTP、影像轉換、pure parser、fetch policy、disk cache 與 runtime 所有權各留在自己的層。不得把共用實作搬入 SDK；Common 必須維持 internal source-only 套件。
- 修競態先加可重現測試；共享 fetch 不得使用任何 caller token。force refresh 必須保留 operation identity、generation 與 avatar/cache 的原子發佈邊界。
- tag 字典的共享 producer 也要由 runtime drain 追蹤到解析與 `tags.json` 寫入完成；單一 caller 取消只結束自己的等待。調整語言映射、alias 或 cache schema 時先補真實 payload／舊 JSON 測試，並確認既有文章與負快取政策。
- 所有建置與測試設 `DeployPluginToApp=false`。標準驗證入口是 `rtk pwsh -NoProfile -File eng/Verify-Plugin.ps1`；它執行離線測試與編譯依賴／產物／文件檢查。
- 不能為通過 gate 刪除測試、放寬 allowlist 或改成只檢查行數。合法的契約變動需同步更新架構決策、相容性說明及能證明新規則的測試。
- 不記錄 SauceNao API key、加密字串或其片段；網路例外可能包含 query，診斷只記錄失敗類別。
- 完成後交代修改、實際驗證、未驗證的平台行為及後續風險。文件的本機連結必須存在，不以「請讀 sibling」取代交接內容。
