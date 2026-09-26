# Pixiv Authors 交接

先讀 [AGENTS](AGENTS.md) 與 [架構契約](ARCHITECTURE.md)。本次重構保留 SDK、public parser、儲存 schema 與單 DLL 發佈，新增刷新／關閉競態保護、可離線測試的 SauceNao 邊界，以及 CI 架構 gate。

## 從哪裡開始修改

| 需求 | 修改位置 | 必跑證據 |
| --- | --- | --- |
| filename／folder／URL 辨識 | 原本兩個 public parser | parser signature gate；新舊輸入的行為案例 |
| Pixiv schema／HTTP | `PixivApiClient` | injectable handler 測試；404／schema／transient 分類 |
| 去重、刷新、avatar 發佈 | `PixivFetchCoordinator` | [刷新競態測試](SceneGallery.Plugin.PixivAuthors.Tests/PixivRefreshTests.cs)、既有 caller-cancellation 測試 |
| cache、設定、secret | 插件 wrapper | persistence／secret 既有測試；舊 JSON 仍可讀 |
| reverse search response | `SauceNaoResponseParser` | [SauceNao 離線測試](SceneGallery.Plugin.PixivAuthors.Tests/SauceNaoTests.cs) |
| tag 文章／翻譯／alias | `PixivTagParser`、`PixivTagLanguage`、`PixivAliasDetector` | [tag payload 與 alias 測試](SceneGallery.Plugin.PixivAuthors.Tests/PixivTagDictionaryTests.cs)、[alias 反例](SceneGallery.Plugin.PixivAuthors.Tests/PixivAliasDetectorTests.cs) |
| tag 去重、TTL、關閉 | `PixivTagDictionary`、`TagDiskCache`、`PixivRuntime` | tag cache 重讀與 [生命週期測試](SceneGallery.Plugin.PixivAuthors.Tests/PixivLifetimeTests.cs) |
| Windows JPEG 編碼 | `SauceNaoImageEncoder` | 純像素測試＋真實 Windows 圖片人工驗證 |
| 所有權與關閉 | `PixivRuntime` | [生命週期測試](SceneGallery.Plugin.PixivAuthors.Tests/PixivLifetimeTests.cs)、Common drain 測試 |

新增 service 時先在 ARCHITECTURE 描述責任和允許方向，再加入明確型別集合的 architecture gate。不要擴大 SDK、公開 class 或改 shared package 來掩蓋局部需求。真正契約變更需要獨立 ADR、相容策略與新舊案例；禁止單純放寬 gate。

## 驗證與發佈

一般獨立 checkout，先設定 GitHub Packages 的合法環境認證，再執行：

```powershell
rtk pwsh -NoProfile -File eng/Verify-Plugin.ps1
```

此入口執行 Release `dotnet test`，檢查插件和測試專案的 SDK `1.3.0`／Common metadata、無 sibling ProjectReference／Compile link、shipping output 沒有 SDK／Common／test DLL，並驗證三份文件的本機連結。SDK runtime assembly version 保持 `1.0.0.0`。所有驗證強制 `DeployPluginToApp=false`；普通 IDE build 原有的本機部署選項仍保留。

套件尚未發佈的工作區等效驗證，可將外部明確指定的 local-feed config、空白隔離 package cache 與 artifacts 目錄傳入 `-NuGetConfig`、`-PackagesPath`、`-ArtifactsPath`。不要把 local feed 寫入本 repo 的 NuGet.config，也不要將這種驗證誤稱為遠端套件已可取得。

Build／Release workflow 均執行同一個 gate。release 使用同一個版本參數先驗證，再複製 `SceneGallery.Plugin.PixivAuthors-{version}.dll`。套件 `0.2.0` 必須先可從 GitHub Packages 還原，才可正式發佈 plugin；本次工作本身不含 push／package publish／release。

## 本次修正證據與剩餘人工驗證

- 重構前已執行 `PixivRefreshTests` 三案，全部重現失敗：舊回應覆寫新 cache、舊 finally 移除替代操作、舊 avatar 覆寫新狀態。`Dispose_RejectsNewFetches` 也先重現 admission 缺口；修改後全部通過。
- 生命週期測試涵蓋 caller 全部取消後共享 producer 仍被追蹤、忽略取消的 encoder 超過 deadline 時延後 persistence、關閉後拒絕新工作及重複 Dispose。另以可注入的 preview-commit barrier 固定 Dispose 時機，驗證提升為磁碟 cache 仍受 producer drain 保護；晚到的 author response 與 preview promotion 都實際重讀 JSON，確認 final flush 沒有遺失寫入。
- 既有 negative cache、schema failure、offline retry、caller cancellation、debounced persistence／DPAPI 測試保留；新增 SauceNao multipart／encoder seam／parser／秘密診斷與實際編譯架構測試。網路測試不對外請求。
- Windows codec 搬移索引：基準 commit `51d93cbbb6f0a99c038d1686e22e5bc4492171a2` 的 `SauceNaoClient.cs:60-95` 對應現在 `SauceNaoImageEncoder.cs:10-41`。WinRT 解碼、EXIF orientation、BGRA、white compositing、JPEG encoding 及 stream 讀回的呼叫順序保留；private method 改成 injectable `Task<Stream>` seam。
- 另已在 Windows `10.0.26100`／.NET `10.0.12` 實際呼叫正式 Release DLL 的 `SauceNaoImageEncoder.EncodeAsync`，不是 encoder mock：自產 8×6 全透明 PNG 編成 631-byte JPEG，WinRT 再解碼維持 8×6 且所有像素為白色；3×2 純紅 PNG 編成 634-byte JPEG，維持 3×2 且所有像素符合紅色（JPEG 色差容許 8）。probe 不讀使用者圖片、不用 API key、不呼叫網路，每案例 20 秒、程序外部 45 秒 deadline；本次約一秒完成。原始 fixture、JPEG、stdout／stderr、DLL SHA256 與 JSON 結果保存在工作區 `validation/plugin-refactor-20260922/pixiv-codec-probe/results`。
- 上述真實 codec 驗證尚未覆蓋 EXIF 旋轉、宿主 UI 取消與 live reverse-search 服務；這些仍需後續驗證。HTTP／encoder seam 的取消傳遞已有離線測試，不能等同於 UI 或外部服務驗證。
- 原有兩個 public parsers、provider ID、設定鍵與原有 `authors.json`／`artworks.json` schema 不變；SDK 套件基線依使用者決定由原計畫的 `1.0.0` 改為現有 `1.3.0`，runtime assembly identity 仍為 `1.0.0.0`。新增 `ITagDictionaryProvider` 與 `tags.json`，tag cache schema `0` 記錄可讀但會視為 miss 重新抓取，以補 `AliasOf`；原 cache 不需遷移。回退原有功能程式時仍能讀正式 JSON／avatar；staging 檔不是快取 schema。
- tag lifetime 缺陷先以 `Dispose_TracksTagProducerAfterItsOnlyWaiterCancels` 重現失敗（caller 取消後，`DisposalCompletion` 過早完成）。現在共享 tag producer 自行加入 runtime drain，HTTP 使用 shutdown token，等待者只取消自己；新測試亦檢查另一個等待者仍拿到文章、只發一個請求且 `tags.json` 重讀成功。`Lazy` 避免並行 `GetOrAdd` value factory 各自啟動請求，worker 在啟動時重查 cache，避免前一代剛完成時重複網路請求。
- SDK 1.3.0 的 tag parser、語言與 alias 案例是離線 payload／cache 測試；尚未對 Pixiv live tag endpoint、宿主 tag cloud UI 或真實使用者語言切換做人工整合驗證。
- 2026-09-26 以宿主工作區的明確 local feed、獨立 package cache／artifacts 跑 `eng/Verify-Plugin.ps1`：**125/125 通過、0 warnings**，含精確的 19 類型 production inventory、SDK 版本／capability、依賴方向、shipping output 與本地文件連結檢查。因執行環境不能連 nuget.org，這次本機驗證設 `NuGetAudit=false`；沒有宣稱完成遠端套件或 CI 驗證。驗證產物在工作區 `validation/plugin-refactor-20260922/pixiv-sdk13-final`。
- 最終全工作區 gate 使用 SDK1.3 fresh local feed 與新輸出再次 **125/125 通過**，證據位於主程式 `artifacts/plugin-workspace/d2ba7560c24246ecb11a173b2518d9f3/PixivAuthorsPlugin`。另用該 gate 的正式 DLL 重跑真實 WinRT codec，兩個合成 PNG→JPEG 案例皆通過，結果在工作區 `validation/plugin-refactor-20260922/pixiv-codec-probe/results-sdk13-final/codec-result.json`；型別 inventory 現亦涵蓋子命名空間。

## 2026-09-27 發布候選驗證

遠端既有最新 release/tag 為 `0.0.5`，且 `origin/master` 已納入原有 `agent/pixiv-fetch-coordinator` commit；本次候選版為 `v0.0.6`。以正式注入版本 `0.0.6`、SDK `1.3.0`、Common/Secrets `0.2.0` 及明確的本機 NuGet feed 執行 `eng/Verify-Plugin.ps1`，**125/125 通過**，並通過組件／套件邊界、發佈輸出及文件連結檢查。測試輸出位於此 checkout 的 `bin/release-validation/artifacts`（Git 忽略）。編譯採 `UseSharedCompilation=false` 避免沙箱外的 Roslyn shared compiler 無法寫入隔離輸出；`NuGetAudit=false` 僅用於無法連線 nuget.org 的本機環境，不代表已完成遠端套件安全稽核。這項驗證使用本機套件來源，官方 GitHub Packages 還原及遠端 Release workflow 須在依賴套件發佈後另行確認。
