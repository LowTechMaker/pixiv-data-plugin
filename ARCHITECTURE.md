# Pixiv Authors 架構契約

本文件描述維護時必須保留的邊界；執行證據在 [ArchitectureTests](SceneGallery.Plugin.PixivAuthors.Tests/ArchitectureTests.cs) 與 [驗證入口](eng/Verify-Plugin.ps1)。變更流程見 [HANDOFF](HANDOFF.md)。

## 責任與依賴方向

| 元件 | 責任 | 不得承擔的責任 |
| --- | --- | --- |
| `PixivAuthorPlugin` | SDK 適配、設定映射、組合 runtime | HTTP、JSON 解析、共享 fetch／關閉計數 |
| `PixivRuntime` | 擁有 client、shutdown token、producer drain；reverse-search SDK 映射 | 提供 parser 或決定 cache TTL |
| `PixivFetchCoordinator` | 去重、force refresh、cache promotion、author generation 與 avatar 發佈 | 自建 HTTP handler、Windows 編碼 |
| `PixivApiClient` | 匿名 HTTP／重試、Pixiv response 檢查、avatar staging | 寫入 cache、決定哪個 refresh 可以發佈 |
| `PixivTagDictionary` | tag `(language, tag)` 去重、從 API 結果到 cache 的流程；完整 producer 登記在 runtime drain | 自行擁有 HTTP／SDK、讓 caller 取消共用 producer |
| `PixivTagParser`／`PixivTagLanguage`／`PixivAliasDetector` | 純 payload 解析、BCP-47 對 Pixiv 語言映射、保守 alias 判斷 | 網路、快取、host、runtime |
| `SauceNaoClient` | multipart HTTP 與 transport 所有權 | Windows bitmap 細節、cache、SDK result 映射 |
| `SauceNaoResponseParser` | 純 JSON 政策：選最高相似度的完整 Pixiv 結果 | HTTP、檔案、host、runtime |
| `SauceNaoImageEncoder` | Windows 解碼、透明像素白底合成、JPEG 編碼 | HTTP、JSON、provider policy |
| `AuthorDiskCache`／`ArtworkDiskCache`／`TagDiskCache`／`PluginSettings` | 插件自己的 JSON schema、鍵、TTL、secret 欄位 | HTTP、coordinator、runtime |
| `SceneGallery.PluginCommon` | source-only debounce、原子寫入、rate limit、關閉 primitive | provider ID／schema／TTL／SDK 能力 |

所有下層禁止依賴 `PixivAuthorPlugin`。public parser 維持原本 namespace；內部依賴 gate 使用明確型別集合，不為分層測試搬動 public API。
`ArchitectureTests` 另外精確列舉插件根命名空間及子命名空間的所有 top-level 型別；新增類別若沒有更新此責任表及對應依賴規則，CI 會失敗。

主要依賴方向：`PixivAuthorPlugin → PixivRuntime → {PixivFetchCoordinator, PixivTagDictionary, SauceNaoClient}`；coordinator 與 tag 字典只向各自 HTTP client、純解析／政策和磁碟 cache 取資料，HTTP、純解析及持久化元件不回頭依賴 runtime 或入口。`PluginOperationDrain` 由 runtime 建立並借給兩種共享 producer；它只管理生命週期，不決定 Pixiv 的 TTL、鍵或 JSON。

## 公開契約與套件

- 單一發佈組件 `SceneGallery.Plugin.PixivAuthors.dll`。public 型別只有 `PixivAuthorPlugin`、`PixivFilenameParser`、`PixivFolderNameParser`，皆在 `SceneGallery.Plugin.PixivAuthors`。
- 入口保留無參數建構子；能力為 `IPlugin`、`IFolderAuthorProvider`、`ICardImportProvider`、`IArtworkMetadataRefresher`、`IImportDestinationProvider`、`IReverseImageSearchProvider`、`IPluginSettingsProvider`、`ITagDictionaryProvider` 與 `IDisposable`。tag 公開方法僅為 `FetchTagAsync` 與 `TryGetCached`，文章型別由 SDK 定義。
- `Name = Pixiv Authors`、`ProviderId = pixiv`、原更新 URL 及 parser 的方法／constant 不變。內部新增型別必須 internal。
- SDK NuGet 套件固定 `1.3.0`，`ExcludeAssets="runtime"`／`PrivateAssets="all"`；唯一 runtime SDK 由宿主提供，刻意保留 SDK assembly version `1.0.0.0` 作為二進位識別。Common 與 Secrets 固定 `0.2.0`、`PrivateAssets="all"`，只把 internal 原始碼編入插件。
- `NuGet.config` 將 `SceneGallery.*` 映射到 GitHub Packages。認證使用 `NuGetPackageSourceCredentials_SceneGalleryGitHub`，不提交 token；正式來源不得加入 local fallback。
- 測試專案獨立引用 `NetArchTest.Rules 1.3.2`；其 DLL 與 Mono.Cecil 不得進入插件產物。repo 不連結 sibling runtime／test source，也不依賴宿主 ProjectReference。

## 並行、取消與關閉

- author 操作依 author ID 去重；正常 caller 只用 `WaitAsync(callerToken)` 等候，caller 取消不能取消共享 producer 或其他 caller。
- 每個新的 author producer 有 operation identity 與遞增 generation。force refresh 取代目前登記；舊操作 finally 只能移除自己，不能移除替代操作。
- 新 generation 開始後，舊 generation 禁止寫 author cache 或最終 avatar。下載使用同目錄的唯一 `*.tmp`，只有 coordinator 在 generation lock 下可以同時 move avatar、更新 cache。落後操作丟棄自己的 staging file。
- artwork 去重鍵仍是 `artworkId:saveToLocalCache`。unsaved 結果在記憶體保存，之後 save=true 可提升至磁碟；提升也必須先以 drain 登記為 producer，再進 state gate 寫入。producer admission／completion 不能持有 state gate，以免 final flush 重新進入造成鎖定問題。舊 cache 缺 title 時仍重新查詢。
- runtime 使用唯一 `PluginOperationDrain`。共享 author／artwork producer 的完整工作與 SauceNao 編碼＋HTTP 都在 drain 範圍內；個別 waiters 不代替 producer 計數。
- tag 字典每個 `(Pixiv 語言, tag)` 只啟動一個 `Lazy` producer，由同一個 drain 覆蓋 HTTP、解析、cache 寫入與 finally 清除。個別 caller 取消只取消 `WaitAsync`；共用 HTTP 使用 runtime shutdown token。清除 in-flight entry 必須比對 operation identity，不能讓舊完成事件移除新請求。
- Dispose 先關閉 admission、取消 runtime token，共用十秒 monotonic deadline。HTTP execution 資源先釋放；逾時仍有 producer 時，cache flush／dispose 延後至最後 producer 結束。關閉後新查詢以 `ObjectDisposedException` 拒絕；重複 Dispose 不重複釋放資源。
- 呼叫端取消仍向 caller 傳遞。共享 Pixiv producer 的取消保持原本 null 結果語意；SauceNao caller／runtime linked cancellation 向外傳遞。`DisposalCompletion` 僅供內部驗證最終清理，不屬於 SDK。

## 儲存與錯誤不變量

- 儲存檔名與 schema 不變：`authors.json`、`artworks.json`、`settings.json`、`avatars/{authorId}.{extension}`。author 成功 cache 不自動過期，確認不存在的 negative cache 為 24 小時；schema／transport 錯誤不寫 negative cache。
- artwork wrapper 保留七天 failed-entry TTL；API 無結果不新增負快取。tags、rating、title 缺失與 unsaved promotion 行為不變。
- `tags.json` 以 `Pixiv 語言 + NUL + 原始 tag` 為鍵；成功文章無 TTL，確定的 HTTP 404 負快取三天，傳輸錯誤或 malformed 200 不寫負快取。文章 cache schema `1` 保存 `AliasOf`；舊 schema `0` 可讀但視為 miss 重新抓取。`zh-Hant`／`zh-TW` 對 Pixiv `zh_tw`、簡體對 `zh`，不相互混用。alias 只在文章文字明確宣告「另一種名稱」時成立，不可單靠 parentTag 合併真正的子標籤。
- cache schema 與邏輯 mutation 留在插件。Common 只處理 generation、debounce、atomic write、失敗重試與 disposal；寫入失敗保持 dirty 並從五秒退避到五分鐘。
- `settings.json` 保留 `destinationFolderName`、`usesRatingFolders`、`sauceNaoApiKey`。API key 使用既有 Windows DPAPI CurrentUser 與版本 prefix；舊 plaintext 只在原有 save point 遷移。解密失敗視為未設定，不輸出 secret 值。
- SauceNao request 仍使用 JPEG、八筆跨資料庫搜尋；完整 Pixiv 結果按最高 similarity 選擇，沒有新增最低門檻。status<0 為無結果；診斷只輸出 status／例外類別，禁止原樣輸出可能反射 key 的 message／request URL。

## 架構決策紀錄

- **ADR-001：保留既有 public API。** public parsers 不是新增公開的實作細節，必須相容；新增元件一律 internal，反射測試檢查 exported types、capability maps 與 parser signatures。
- **ADR-002：刷新以 generation 發佈，不以移除 dictionary key 代表取消。** 原程式的舊 finally 會移除新 producer，且晚到回應能覆寫新 cache／avatar。identity 與 generation 分別保護登記和發佈。
- **ADR-003：runtime 擁有完整 producer。** 只追蹤 caller task 會在 caller 取消後漏掉仍寫 cache 的共享工作。關閉採 source-only drain primitive，provider 的清理與持久化順序仍由本 repo 決定。
- **ADR-004：測試 seam 不改 shipping 形態。** HTTP handler 與 JPEG encoder 可注入；parser 為純函式。NetArchTest 只在測試使用，實際 dependency 規則以 ordinary／static／async 反例自測，禁止用 grep 或行數替代。
- **ADR-005：接受 SDK 1.3.0 作為本次新基線。** 原重構計畫寫 1.0.0，後續 9/23 的未提交變更已將宿主及本插件升至 1.3.0，並加入 tag dictionary；使用者明示保留這些變更。NuGet 版本與 capability gate 改鎖 1.3.0，runtime assembly version 仍為 1.0.0.0；原有 provider ID、public parsers、設定及 cache 不改，新增 `ITagDictionaryProvider`／`tags.json` 以獨立案例驗證。
- **ADR-006：tag 查詢是共享 producer，不是 caller task。** 接入 1.3 capability 後發現 caller 取消會使 runtime drain 先歸零，但不帶 caller token 的 tag HTTP 仍在工作；新回歸測試先重現 disposal 過早完成。現由字典把完整 fetch 登記到 runtime drain，並用 shutdown token 取消共用請求；`Lazy` 避免併發 factory 多發請求，operation identity 保護 in-flight 清理。
