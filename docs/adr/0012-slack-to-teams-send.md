# ADR 0012：Slack → Teams 發話：開頭 @bot、只有自己看得到的確認、狀態留在 Slack

## 決定

relay 的 Phase 2：在 Slack 寫的話經 gateway 的 `POST /v1/chats/{id}/messages` 發到 Teams。頻道平常是討論用的，**預設什麼都不送**，只有明確要求、按過確認的那則才送。

### 怎麼發

```
@teams-relay 好，明天見          ← 開頭 @bot
   │ app_mention（Socket Mode）
   ▼
只有本人看得到的確認（ephemeral）
   → Teams: <群名>
   預覽（轉成純文字後的樣子）、提醒（例如附件不會送）
   [Send] [Cancel]
   │ Send
   ▼
⏳ → 重讀那則訊息 → POST /v1/chats/{id}/messages（clientMessageId 由 Slack ts 算）
   │ Teams 推回這則（SSE）
   ▼
⏳ 換成「已送出」表情
```

- **觸發**：訊息**開頭**是 @bot 才算。@bot 在句子中間（在討論 bot 本身）一律不理。
- **確認**：relay 回一則 ephemeral（`chat.postEphemeral`，thread 裡就帶 `thread_ts`），寫明目標對話、預覽、提醒，附 **Send** / **Cancel**。按了之後經 `response_url` 改成結果或收掉。
- **按 Send 時才讀內容**：按鈕的 value 只放頻道與 `ts`（thread 裡再加 `thread_ts`），送出前重新讀那則訊息、去掉開頭的 @bot。頂層用 `conversations.history`（`latest=ts`、`inclusive`、`limit=1`）；thread 裡的回覆 history 讀不到，要用 `conversations.replies`，判斷目標對話也要靠 `thread_ts` 找到上層那則。所以確認前改錯字會送改過的版本；relay 不用記待確認的東西，中間重啟按鈕照樣有效。
- **誰能發**：`Relay:Slack:Senders`（Slack user id 白名單）。觸發與按 Send 都檢查：是白名單裡的人、而且是那則訊息的作者。不符的回 ephemeral 說明，不送。
- **目標對話**：
  - thread 裡：上層是轉過來的 Teams 訊息，就送到那則所在的對話；上層是頻道裡的討論，同頂層的規則。這裡只決定送去哪個對話，送出的是一則普通訊息，不是 Teams 的引用回覆（見〈之後〉）。
  - 頂層：這個頻道在 Routes 裡**只對到一個**對話才送。`DefaultChannel`、或一個頻道對到多個對話時，頂層不能發（不知道要送哪），回 ephemeral 請改在那則 Teams 訊息的 thread 裡發。
- **內容**：gateway 目前只送純文字（`messagetype: Text`）。Slack 的寫法轉成看得懂的文字：`<@U…>` → 名字、`<#C…>` → 頻道名、`<url|label>` → `label (url)`、`:smile:` → 😄、HTML entity 還原。附件、圖片這一版先不送（不是確定做不到，見〈之後〉），確認時就提醒。

### 送出後頻道怎麼呈現

頻道裡的規則：**bot 貼的 = Teams 那邊的對話，人打的 = 討論。**

| 在哪發 | Teams 推回來時 |
|---|---|
| thread 裡 | 照一般訊息由 bot 貼到頂層（「*Eric*: …」）。Teams 個人版沒有 thread，頂層才是它的時間線 |
| 頂層 | 不再貼，那則就代表 Teams 上的這句 |

兩種都在發話的那則按上「已送出」表情。

- **狀態用表情**，不在頻道留言：⏳ 送出中 → 「已送出」表情；失敗 ⚠️ 加一則 ephemeral 說原因。「已送出」是 **Teams 推回這則**才標，不是 API 回 200 就標。API 成功但一段時間（先定 2 分鐘）沒推回來，也換成 ⚠️、說「未確認」。
- **重試**：
  - API 失敗：經 `response_url` 把確認改成錯誤原因加 **Retry** 鈕，按了拿掉 ⚠️ 照 Send 的流程重來。
  - 逾時未確認：不給 Retry。可能其實送到了只是晚到，重送會變兩則；之後真的推回來，⚠️ 照樣換成已送出表情。
  - 確認不見了（ephemeral 不保存）：重新 @bot 一次。那是新的一則（`ts` 不同），跟原本那則無關。
- 「已送出」表情由 `Relay:Slack:SentEmoji` 設定，預設 `outbox_tray`（📤）。想用自訂表情（例如用 relay 的 icon 做 `:teams-relay:`）要手動上傳一次再改設定。啟動時用 `emoji.list` 確認它存在，不在就退回 📤、記 warning。
- **只認 bot 自己按的表情**：人手動按 📤 沒有意義。

### 用 `clientMessageId` 連起 Slack 與 Teams

- gateway：`SendMessageRequest` 加選填的 `ClientMessageId`（沒給照舊亂數）；`MessageDto` 帶出 Teams 的 `clientmessageid`。都是加欄位，事件 schema 不升版（ADR 0006）。
- relay 發話時用那則 Slack 訊息的 `ts` 去掉小數點當 `clientMessageId`（`1729834567.123456` → `1729834567123456`）。推回來的訊息帶著它，就知道是哪一則 Slack 訊息發的。換回 `ts` 用字串處理：剛好 16 碼、全是數字才在倒數第 6 碼前插回小數點，不經過浮點數。gateway 自己產生的亂數 id 是 19 碼，長度就分得開；誤判也只是查不到那則 Slack 訊息。
- **防重送**：兩次 Send（連點、Socket Mode 重送事件）是同一則 `ts`。同一則 `ts` 在記憶體裡上鎖（relay 只跑一份），再看那則有沒有 bot 按的 ⏳ / 已送出表情，有就不送。只靠查表情擋不住幾乎同時的兩次點擊。Teams 會不會把同一個 `clientMessageId` 當同一則去重還沒驗證，不依賴它。
- **推回來的處理**：先查記憶體裡剛送出的；查不到（relay 重啟過）就把 id 換回 `ts`，看那個頻道的那則訊息上有沒有 bot 按的已送出表情（`reactions.get`）。有就照上表處理（頂層的不貼），沒有就當一般的 Teams 訊息。
- **對應表**：頂層發的那則沒有 bot 的 metadata 可以夾 Teams id。推回來時記進 `MessageLinks`（Teams id → 那則 Slack 訊息）。之後 Teams 那邊引用回覆它、按表情，都接得回這則。thread 裡發的推回來時是 bot 貼的，照原本的 metadata 機制。
- **重啟回補**（`WarmupLinksAsync`，在開始收事件之前）：讀頻道歷史時，除了帶 metadata 的 bot 訊息，帶 bot 已送出表情的人發訊息也算（history 本來就附 reactions），拿 gateway 近期訊息的 `clientMessageId` 比對補回對應。這些訊息也算進續接點（`NewestTeamsId`、往更早找最新一則的 `ReadNewestRelayedAsync`），不然頻道最後一則是自己發的時候，續接點會退到更早。就算退了也不會出錯：那則會再推一次，照上一條查到已送出表情，不貼、順便補回對應。

### Slack 連線

- **Socket Mode**：relay 主動連出去，部署那台不用開對外的 HTTP。要 app-level token（`xapp-…`，`connections:write`），設定是 `Relay:Slack:AppToken`；沒設就不開發話，只轉送（Phase 1 照舊）。
- 只訂 `app_mention`，不訂 `message.channels`：relay 只拿到有 @bot 的訊息，讀不到頻道裡其他討論。
- manifest 加：`socket_mode_enabled`、`interactivity`、事件 `app_mention`，scope `app_mentions:read`、`reactions:read`、`users:read`（@ 換名字）、`emoji:read`。
- Native AOT 用不了 Slack 的 SDK：Socket Mode 用 `ClientWebSocket` 自己接（收 envelope、回 ack、遇到 `disconnect` 重連），payload 走 `SlackJsonContext`。
- 開了發話時，`/health` 也看 Socket Mode 連著沒：relay 的工作包含發話，斷了要看得出來。

### 之後

- 從 thread 發的變成 Teams 的引用回覆（要 gateway 支援發引用）。
- 圖片、附件、格式：都還沒研究過 Teams 那邊怎麼發，要先做 spike。目前的推測：
  - 圖片：網頁版是先傳到 Teams 的媒體服務（AMS），再在 `RichText/Html` 訊息裡用 `<img>` 引用。gateway 讀圖時已經在代抓同一種網址（`/v1/media`）。
  - 附件：個人版的檔案放在發送者的 OneDrive（`AttachmentDto` 的 `ShareUrl`），要先上傳 OneDrive、建分享連結，再掛在訊息上。可能要另外的 token，最麻煩。
  - 粗體、清單這類格式：改送 `RichText/Html`，Slack 的寫法轉成 HTML。

  做的時候 gateway 的發話 API 跟著擴充（例如帶 HTML 或檔案），relay 確認時就不再提醒。
- Slack 上編輯、刪除已送出的那則，同步到 Teams：
  - 跟發話一樣要確認，不自動同步：Teams 那邊是對外的，順手改個錯字不該直接改過去。偵測到編輯就回 ephemeral「Already sent to Teams. Update it there too?」**[Update]** / **[Keep]**，預覽改過的內容；刪除就問「Delete it in Teams too?」。
  - 刪除選 Keep 時，頂層發的那則在 Slack 上沒了、Teams 上還在，bot 把它補貼成一般的「*Eric*: …」，頂層的 Teams 對話才不會缺一句。
  - 同步過去的編輯、刪除 Teams 會再推回來：thread 裡發的照 Phase 1 原地更新 bot 那則；頂層發的要認出是自己剛同步的、跳過（bot 改不了人發的訊息）。
  - 要先做 spike 確認 Teams 上改、刪的 API，gateway 再加對應的端點。
  - 要偵測 Slack 上的編輯、刪除，relay 得改訂 `message.channels`（`app_mention` 收不到刪除，編輯也不保證會再送），只理會帶 bot 已送出表情的那幾則。這會改掉上面「只訂 `app_mention`」的決定，到時在那句後面用括號補充。
- 📤 批准別人擬的草稿：同樣要訂 `message.channels`，等真的需要再說。

## 背景

- 目標是只開 Slack 就能收發（relay README）。Phase 1 只轉進來；要能回，又不能讓頻道裡隨口講的話被送出去：頻道之後是討論怎麼回的地方。
- 考慮過的觸發方式：
  - 頂層打字就送：最直覺，但頻道就不能討論了。
  - 在草稿上按 📤：適合「別人擬、我批准」，但要讀整個頻道，而且按下去沒有再看一眼的機會。
  - message shortcut 開 modal：可以改、可以挑目標，但步驟多，手機上麻煩。
  - **開頭 @bot + ephemeral 確認**：寫的時候就決定要送，確認畫面讓你看到 Teams 那邊會長什麼樣、送去哪；relay 只收得到 @ 它的訊息。
- 確認用 ephemeral 而不是 modal：留在對話裡、手機上也順，別人看不到，不會干擾討論。
- 推回來的分兩種處理：thread 裡的話不搬到頂層，只看頂層就拼不出 Teams 上的對話；頂層的話再貼一次，同一句會連著出現兩次。
- 狀態用表情不用留言：留言會讓每則發話底下都開一個 thread。表情掛不了 metadata，所以用 `clientMessageId` = Slack `ts` 讓兩邊不用另外存對照，跟 Phase 1「狀態都放在 Slack 上」一致。
- 自訂表情不能由 bot 自動加：只有 Enterprise Grid 的 `admin.emoji.add` 能加，一般 workspace 的 bot token 沒有這個權限。

## 代價與注意

- 發話用的是 gateway 綁的那個 Teams 帳號。白名單管的是「誰能用這個帳號說話」，不要隨便加人。
- ephemeral 不保存：重新整理、換裝置就不見。不見就是沒送，要送再 @bot 一次；不會在不知情的情況下送出。
- 送出後在 Slack 編輯、刪除，Teams 不會跟著變，relay 也看不到（沒訂 `message.channels`），不會提醒。
- 反過來，在 Teams 那邊編輯、刪除從頂層發的那則，不特別處理：gateway 綁的是專用帳號，不會有人直接用 Teams 改它的訊息。真的發生時，照 Phase 1 的流程在 thread 留舊版後 `chat.update`，但 bot 不能改人發的訊息（`cant_update_message`），會記 error，Slack 上留著原內容。
- 這一版只送純文字：粗體、清單等格式會變成原樣的符號或消失；附件、圖片不送。
- `clientMessageId` 換回 `ts` 後查不到東西時，當一般的 Teams 訊息處理。Teams 自己的 client 產生的 id 剛好撞上某則 Slack 訊息的 `ts`、那則又剛好有 bot 的已送出表情，才會誤判，不另外防。
- 重啟後的處理多打 Slack API（`reactions.get`、讀歷史），只發生在推回來的是自己發的訊息、記憶體裡又沒有的時候。
- 從 Slack 發的和經其他路徑發的（別的 API key、`/mcp` 的 `send_message`），推回來時 `From` 都是帳號自己。後者照 Phase 1 由 bot 轉送，兩者只靠 `clientMessageId` 分得出來。
