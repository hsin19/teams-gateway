# TeamsGateway.SlackRelay

把個人版 Teams 的訊息轉進 Slack，目標是日常只用 Slack 收發。它是 gateway 的**訂閱者**，只用公開的 `/v1` API + API key，不碰 Teams 非官方 API。

分兩階段：

- **Phase 1（已做）Teams → Slack**：當 `GET /v1/events`（SSE）的訂閱者，把 Teams 訊息貼進對應的 Slack 頻道。斷線用 `Last-Event-ID`（事件序號）續接；relay 自己重啟時序號沒了，改從 Slack 上最後一則自己的 bot 轉過去的 Teams 訊息之後續接（`afterMessage`；近 24 小時沒轉過東西就往更早的歷史找），停機期間的不會漏，重送的照版本去重。限制：Slack 上一則都沒轉過（例如剛清空），或那則訊息的事件已超過 gateway 的保留天數（預設 7 天），就只能從現在開始。60 秒沒收到任何資料（gateway 每 20 秒有心跳）就當連線半死、重連。
- **Phase 2（已做）Slack → Teams**：訊息開頭 @bot → 只有本人看得到的確認 → 按 Send 才經 `POST /v1/chats/{id}/messages` 發出，狀態用表情標在那則上。Socket Mode 收事件，沒設 `Relay:Slack:AppToken` 就不開。用法見下面〈從 Slack 發到 Teams〉，設計見 [ADR 0012](../../docs/adr/0012-slack-to-teams-send.md)。

## Slack app

bot 的 app 定義在 [`slack-app-manifest.yaml`](slack-app-manifest.yaml)。到 api.slack.com/apps → Create New App → From an app manifest 貼它建立，Install 後把 **Bot User OAuth Token**（`xoxb-…`）設成 `Relay__Slack__BotToken`。各 scope 的用途寫在 manifest 註解；改了 scope 要重新 Install 才生效。

表情：Teams 有人按 / 收回表情時，在那則 Slack 訊息的 thread 留一句「*誰* reacted 👀」/「removed」，bot 也在原訊息按上同一個 Slack 表情（最後一個人收回時拿掉），頻道裡一眼看得到有人按。送到 Teams 的 Slack 訊息會保留送出狀態表情；若 Teams 的表情對應到同一個名稱，只留 thread 通知、不鏡像，以免移除狀態標記。留言的 metadata 夾著 gateway 的事件序號（`teams_reaction`）：relay 重啟後從 Slack 上最後一則續接會重送之後的表情事件（表情沒有版本號可去重），貼之前先讀那則 thread（`conversations.replies`，之後記在記憶體），同一個序號貼過就不再貼。表情代碼對照見 `TeamsEmoji`，對不到 Slack 表情的只留言（顯示原始代碼）。訊息晚到時（例如 gateway 睡眠後才補到），當時已經有人按的表情會在訊息貼出後接著補上。需要 `reactions:write`（manifest 已列，舊的 app 要加上並重新 Install）。

訊息裡的內嵌圖片：relay 經 gateway 的 `/v1/media` 抓下來，上傳成 Slack 檔案（不分享到頻道），再用 image block 貼在同一則訊息裡；抓不到或上傳失敗時文字照送，末尾註記幾張圖未能轉送。

送不出去時：Slack 一時不通（連不上、`internal_error` 之類 Slack 自己的錯誤）每則重試兩次，還是不行就放棄那則、記 error；被限流（`ratelimited`，每頻道約每秒一則，睡眠醒來補一批時容易撞到）不算次數，照 `Retry-After` 等到送得出去為止，後面的事件跟著排隊。頻道不存在、沒權限這類重試也沒用的錯誤直接記 error 放棄。編輯時原地更新被拒，對照表留著舊版，不會當成已經更新過。

## 從 Slack 發到 Teams

頻道平常是討論用的，預設什麼都不送；只有明確要求、按過確認的那則才送。

- **怎麼發**：訊息**開頭** @bot（例如 `@teams-relay 好，明天見`）。relay 回一則只有你看得到的確認：送去哪個 Teams 對話、內容預覽、提醒（例如附件不會送），按 **Send** 才送、**Cancel** 收掉。@bot 在句子中間（在討論 bot 本身）不理。
- **送去哪**：在自己的 bot 轉過來的 Teams 訊息的 thread 裡發，送到那則所在的對話、當成那則的**引用回覆**；從 Slack 發過去的那則（有 📤）也一樣，在它的 thread 裡發就是引用回覆它。別的 Slack app 轉的（例如本機開發用的 bot）不能從這裡回。在頂層（或上層是頻道裡的討論）發，這個頻道在 Routes 裡只對到一個對話才送；`DefaultChannel`、或一個頻道對到多個對話時頂層不能發，請改在那則 Teams 訊息的 thread 裡發。
- **內容**：按 Send 時才重讀那則訊息，所以確認前改錯字會送改過的版本。Slack 的格式（粗體、斜體、刪除線、code、連結、條列、引用、程式碼區塊、emoji）照樣轉成 Teams 的 HTML，@人 換成名字。附件、圖片這一版不送。
- **誰能發**：`Relay:Slack:Senders` 裡的 Slack user id，而且只能送自己寫的那則。發話用的是 gateway 綁的 Teams 帳號，不要隨便加人。
- **狀態**：bot 在那則按 ⏳（送出中）→ Teams 收下後換成已送出表情（`Relay:Slack:SentEmoji`，預設 📤）；固定的 📤 標記會保留，讓改設定或重啟後仍能去重。失敗時按 ⚠️，確認那則改成原因加 **Retry**。gateway 沒給明確結果時，relay 會先到對話裡找那則有沒有送到（從現在往前翻到那則 Slack 訊息寫下的時間）；查不完時保留 ⏳，Retry 會再查一次才送（Teams 不會替同一則去重，直接重送可能變兩則）。按 Send 之後確認那則會變成「Sending to Teams…」，附一個 **Check again**：relay 剛好重啟、一直沒有結果時按它，會先到對話裡找再決定要不要送。確認那則不見了（ephemeral 不保存）：那則上沒有 bot 的狀態表情就是沒送，重新 @bot 一次；還停在 ⏳、或帶著 ⚠️（gateway 沒回應時，當時查不到的也可能之後才出現，確認那則不見了就分不出是哪一種）都可能已經送出，先到 Teams 看或等一下看會不會換成 📤，不然重新 @bot 是新的一則、不經過防重送的查詢，可能變兩則。
- **Teams 推回來時**：頂層發的那則就代表 Teams 上的這句，不再貼；thread 裡發的由 bot 照一般訊息貼到頂層（Teams 個人版沒有 thread，頂層才拼得出 Teams 上的對話），因為是引用回覆，會畫成「*名字* replied to <連結>」。之後 Teams 上有人引用、按表情，都接得回 Slack 上那則。
- **Teams 上改了頂層發的那則**：bot 改不了人發的訊息，在那則的 thread 貼「_Edited in Teams_」加新版（刪除是「_Deleted in Teams_」），Slack 上的原文不動。這則留言夾著 metadata（`teams_edit_note`：Teams id 與版本），relay 重啟後 gateway 重送同一個編輯時認得出貼過了，不會重貼。
- **送出後在 Slack 上改、刪**（[ADR 0013](../../docs/adr/0013-slack-edit-delete-sync.md)）：不自動同步，relay 回一則只有你看得到的確認。改了是「Already sent to Teams. Update it there too?」加新內容的預覽，**Update** 才把 Teams 上那則整則換掉（引用回覆會再帶引用），**Keep** 就只改 Slack。刪了是「Deleted here, but it's still in Teams. Delete it there too?」，**Delete** 刪 Teams 上那則；**Keep** 留在 Teams，頂層發的那則 bot 會照一般轉送補貼回頂層（只看頂層才不會缺一句），thread 裡發的頂層本來就有 bot 那則。改、刪重送都安全，失敗一律給 **Retry**。同步過去的編輯 / 刪除 Teams 會推回來：頂層發的認得出是自己剛改的，不再貼「Edited / Deleted in Teams」；thread 裡發的照常原地更新 bot 那則。還沒按 Send 就改了不會問（按 Send 時本來就讀最新的）；確認那則沒按就等於 Keep。

開啟步驟：

1. 用更新過的 [`slack-app-manifest.yaml`](slack-app-manifest.yaml) 更新 app（多了 Socket Mode、interactivity、`app_mention` 與 `message.channels` 事件、`app_mentions:read`、`reactions:read`、`users:read`），然後重新 Install。
2. Basic Information → App-Level Tokens 建一個有 `connections:write` 的 token（`xapp-…`），設成 `Relay__Slack__AppToken`。
3. 把能發話的人的 Slack user id 設進 `Relay__Slack__Senders__0`、`__1`…
4. 想用自訂的已送出顯示表情（例如用 relay 的 icon 做 `:teams-relay:`），先在 Slack 手動上傳，再設 `Relay__Slack__SentEmoji=teams-relay`；固定的 📤 去重標記仍會保留，Slack 不認得自訂名稱時會只用 📤 並記 warning。

## 設定

appsettings 放完整 default，secret 走 env（雙底線覆蓋）或 user-secrets。

| 設定 | env | 說明 |
|---|---|---|
| `Relay:Source` | `Relay__Source` | 收事件的來源（`IEventSource`，ADR 0006）：目前只有 `Sse`（預設），換傳輸 = 多一個實作 |
| `Relay:Gateway:BaseUrl` | `Relay__Gateway__BaseUrl` | gateway 位址（預設 `http://localhost:5199`） |
| `Relay:Gateway:ApiKey` | `Relay__Gateway__ApiKey` | gateway 的 API key（gateway 的 `POST /admin/api-keys` 建） |
| `Relay:Slack:BotToken` | `Relay__Slack__BotToken` | Slack bot token（`xoxb-…`，scope 見 manifest） |
| `Relay:Slack:DefaultChannel` | `Relay__Slack__DefaultChannel` | 沒對應到 Routes 的對話落這個頻道（頻道 id）；不設 = 不轉 |
| `Relay:Slack:Routes` | — | `Teams 對話 → Slack 頻道` 路由（list） |
| `Relay:Links:RetentionHours` | `Relay__Links__RetentionHours` | 啟動時從 Slack 歷史回補多久內的訊息對應（預設 24），給回覆連結 / 原地編輯用；重啟的續接點不受這個限制 |
| `Relay:Slack:AppToken` | `Relay__Slack__AppToken` | Socket Mode 的 app-level token（`xapp-…`，`connections:write`）。設了才開「從 Slack 發到 Teams」 |
| `Relay:Slack:Senders` | `Relay__Slack__Senders__<i>` | 能從 Slack 發到 Teams 的 Slack user id（list）；空的 = 誰都不能發 |
| `Relay:Slack:SentEmoji` | `Relay__Slack__SentEmoji` | 送到 Teams 後 bot 按的顯示表情名稱（不含冒號，預設 `outbox_tray`；固定 `outbox_tray` 去重標記會保留） |

**頻道策略**：有 `Routes` 就只訂閱表內那些 Teams 對話、各自轉到對應頻道；要「全部進一個頻道」就只設 `DefaultChannel`、不填 Routes。

**頻道只能填 id**（`C…`）：除了發新訊息，原地更新、按表情、讀歷史這些 Slack API 都只認 id，而且頻道改名 id 也不變。核對哪個 id 是哪個群 / 頻道看啟動時的 log：`路由：<Teams 群名>（<對話 id>）→ #<頻道名>（<頻道 id>）`。名稱來自 gateway 的 `/v1/chats` 與 Slack 的 `conversations.info`（要 `channels:read`），查不到就只印 id，不影響轉送；路由的對話不在帳號的對話清單裡（id 打錯、帳號不在群裡）會記 warning。

Routes 刻意用 **list** 而非 dict：Teams 對話 id 含 `:`，當 config dict 的 key 會被 .NET config 的階層分隔符拆掉。非 secret 但屬部署專屬，放 gitignored 的 `appsettings.Local.json`（會載入、不進 repo）。

```jsonc
// appsettings.Local.json：兩個 Teams 群各自一個 Slack 頻道
"Relay": {
  "Slack": {
    "Routes": [
      { "Chat": "19:aaa@thread.v2", "Channel": "C0ABC" },
      { "Chat": "19:bbb@thread.v2", "Channel": "C0DEF" }
    ]
  }
}
```

## 跑

開發用 AppHost 跟 gateway 一起起（`aspire start` / `aspire run`，見根目錄 README）。`Relay__Gateway__BaseUrl` 由 AppHost 指到 gateway 的 endpoint，其餘（`Relay__Gateway__ApiKey`、`Relay__Slack__BotToken`、`Relay__Slack__DefaultChannel`、Routes、`Relay__Slack__AppToken`、Senders、`Relay__Slack__SentEmoji`）從 `tools/TeamsGateway.AppHost/appsettings.Local.json` 同名的 key 原樣帶進來；Routes 攤平成 `Relay__Slack__Routes__<i>__Chat` / `Relay__Slack__Routes__<i>__Channel`，Senders 攤平成 `Relay__Slack__Senders__<i>`。relay 等 gateway 的 `/health` 過了才起。

本機只轉進 Slack 測試頻道（`docs/adr/0007`）。本機用另一個 Slack app（自己的 bot token；要測發話再加它的 app token）：同一個 app 開兩條 Socket Mode 連線會互搶事件。頻道可以跟正式環境共用，relay 讀 metadata 只認自己的 bot 貼的。relay 目錄下的 `appsettings.Local.json` 是部署那台用的，本機不要放：它最後載入，會蓋掉 AppHost 帶進來的 env。

單獨跑（不經 AppHost）就自己給 env：

```
Relay__Gateway__ApiKey=tgw_… Relay__Slack__BotToken=xoxb-… Relay__Slack__DefaultChannel=C0ABC \
  dotnet run --project services/TeamsGateway.SlackRelay
```

## Native AOT

正式的 image 是 Native AOT（不帶 .NET runtime，image 與記憶體都小很多）；本機 `dotnet run` / AppHost 照樣是 JIT。
寫程式時要守 AOT 的限制：不靠反射，JSON 一律走 source generator（`SlackJsonContext`、`ContractsJsonContext`），
設定繫結由 SDK 的 source generator 處理。`TeamsGateway.SlackRelay` 開了 `PublishAot`、共用的 `ServiceDefaults` /
`Contracts` 開了 `IsAotCompatible`，平常 build 就會跑 AOT 分析，警告一樣擋建置。在本機出 Linux image 的方式見 AGENTS.md。

## 健康檢查

relay 是 worker，開 HTTP（container 裡是 8080）只為了這兩個端點和回版本的 `/version`，沒有別的：

| 端點 | Healthy 的條件 | 用途 |
|---|---|---|
| `/health` | 設定齊全（有 BotToken 和頻道），**而且**連著 gateway 的事件串流；開了發話（有 AppToken）時 Socket Mode 也要連著 | 確認 relay 真的在轉送、發話 |
| `/alive` | process 有回應 | 只確認活著（release 的 smoke check 用，那裡沒有 gateway） |

- 剛啟動（從 Slack 回補對應時還沒連上）、gateway 重啟或斷線重連的空檔，`/health` 都是 Unhealthy，回應只有 `Healthy` / `Unhealthy`，原因記在 log。所以探測的那端要容忍短暫失敗，連續失敗一段時間才算出事。
- 連線半死（gateway 睡眠、NAT 砍掉）時，最多 60 秒沒收到心跳就會斷線重連，`/health` 也跟著變成 Unhealthy，不會一直回 Healthy。
- 不看 Slack Web API：Slack 一時不通由單則重試處理，不代表 relay 壞了。Socket Mode 不一樣：斷了就收不到發話，所以算進去（Slack 定期要求換連線，換的那一下會短暫 Unhealthy）。
- 不看 gateway 的 Teams 登入狀態：那是 gateway 的事，由 `AuthMonitor` 在 Slack 發告警。

log 等級同 gateway：`Microsoft.*` / `System.*` / `Polly` 只記 warning 以上。沒設 BotToken 或任何頻道時，worker 會記一筆 error 然後不啟動（不會空轉）。
