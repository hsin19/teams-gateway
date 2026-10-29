# ADR 0012：Slack → Teams 發話：開頭 @bot、只有自己看得到的確認、狀態留在 Slack

## 決定

relay 的 Phase 2：在 Slack 寫的話經 gateway 的 `POST /v1/chats/{id}/messages` 發到 Teams。頻道平常是討論用的，**預設什麼都不送**，只有明確要求、按過確認的那則才送。

### 怎麼發

```
@teams-relay 好，明天見          ← 開頭 @bot
   │ app_mention（Socket Mode）
   ▼
只有本人看得到的確認（ephemeral）
   → Teams: <群名>（thread 裡：回覆 <誰> 的那則）
   預覽、提醒（例如附件不會送）
   [Send] [Cancel]
   │ Send
   ▼
⏳ → 重讀那則訊息 → POST /v1/chats/{id}/messages（clientMessageId 由 Slack ts 算）
   │ 201，帶回 Teams 訊息 id
   ▼
⏳ 換成「已送出」表情
```

- **觸發**：訊息**開頭**是 @bot 才算。@bot 在句子中間（在討論 bot 本身）一律不理。bot 自己的 user id 啟動時用 `auth.test` 查。
- **確認**：relay 回一則 ephemeral（`chat.postEphemeral`，thread 裡就帶 `thread_ts`），寫明目標對話、預覽、提醒，附 **Send** / **Cancel**。按了之後經按鈕事件帶的 `response_url` 改成結果或收掉（每個 `response_url` 30 分鐘內最多用 5 次）。
- **按 Send 時才讀內容**：按鈕的 value 只放頻道與 `ts`（thread 裡再加 `thread_ts`），送出前重新讀那則訊息、去掉開頭的 @bot。
  - 頂層用 `conversations.history`；thread 裡的回覆 history 讀不到，要用 `conversations.replies`（`ts=thread_ts`）。兩種都帶 `oldest=latest=ts`、`inclusive`，而且要核對讀到那則的 `ts` 與作者：那則被刪掉時 Slack 會回別的訊息，不核對就可能把別人的話送出去。
  - 讀不到（刪了）就回 ephemeral 說明、不送；開頭的 @bot 被編掉、或去掉之後沒有內容，當成取消。
  - 所以確認前改錯字會送改過的版本；relay 不用記待確認的東西，中間重啟按鈕照樣有效。
- **誰能發**：`Relay:Slack:Senders`（Slack user id 白名單）。觸發與按 Send 都檢查：是白名單裡的人、而且是那則訊息的作者。不符的回 ephemeral 說明，不送。
- **目標對話**：
  - thread 裡：上層是轉過來的 Teams 訊息，就送到那則所在的對話，**送成 Teams 的引用回覆**（見〈內容〉）；上層是頻道裡的討論，同頂層的規則，送一則普通訊息。
  - 上層在哪個對話，從 bot 訊息的 metadata 讀：`teams_relay` 的 payload 加上對話 id，轉送與原地更新都帶。光有 Teams 訊息 id 不夠：`DefaultChannel` 與一個頻道對到多個對話時，正是要改在 thread 裡發的情況，頻道反推不出對話。這一項先在 Phase 1 出版，等發話上線時近期的訊息都已經帶著；沒帶的舊訊息，頻道只對到一個對話就用它，不然回 ephemeral 說找不到對話。
  - 頂層：這個頻道在 Routes 裡**只對到一個**對話才送。`DefaultChannel`、或一個頻道對到多個對話時，頂層不能發（不知道要送哪），回 ephemeral 請改在那則 Teams 訊息的 thread 裡發。
- **內容**：送 `RichText/Html`，從訊息的 `rich_text` blocks 轉（沒有 blocks 才退回 `text`）。
  - 粗體、斜體、刪除線、code、連結、條列、引用、程式碼區塊照樣轉成 HTML，Teams 都畫得出來。emoji 用 block 裡帶的 `unicode`，不用自己準備 `:smile:` → 😄 的對照表；workspace 自訂的表情沒有 `unicode`，留 `:name:`。`@人` → `@名字`（`users.info`）、`#頻道` → `#名字`、`@here` 之類照字面。文字一律 HTML escape。
  - 引用回覆：內容前面加 `<blockquote itemtype="http://schema.skype.com/Reply" itemid="<被引用 id>">`（作者、時間、預覽），同時 `properties.qtdMsgs` 放 `[{messageId, sender, time}]`。兩個都要有：只有 blockquote，Teams 畫得出引用但標成驗證不到原訊息；只有 `qtdMsgs`，內容裡沒有引用，畫成普通訊息。兩個都有時伺服器會照原訊息重寫 blockquote，所以 gateway 組的預覽不用講究。
  - 附件、圖片這一版先不送（見〈之後〉），確認時就提醒。

### gateway 的發話 API

都是加欄位，事件 schema 不升版（ADR 0006）：

- `SendMessageRequest` 加三個選填欄位：
  - `Html`：跟 `Text` 二選一，帶它就送 `RichText/Html`。
  - `ClientMessageId`：沒給照舊亂數；只收數字。
  - `ReplyToId`：送成引用回覆。gateway 從 DB 查那則的發話者與內容組引用，查不到回 400。帶了它就一律送 `RichText/Html`（`Text` 轉成 escape 過的段落）。
- `SendMessageResult` 加 `MessageId`：chatsvc 回 201 時 body 是 `{"OriginalArrivalTime": <id>}`，這個值就是新訊息的 Teams id（跟推播、讀回來的一致）。`ChatsvcClient.SendMessageAsync` 現在把回應丟掉了。
- `MessageDto` 帶出 Teams 的 `clientmessageid`。

### 送出後頻道怎麼呈現

頻道裡的規則：**bot 貼的 = Teams 那邊的對話，人打的 = 討論。**

| 在哪發 | Teams 推回來時 |
|---|---|
| thread 裡 | 照一般訊息由 bot 貼到頂層。Teams 個人版沒有 thread，頂層才是它的時間線。它是引用回覆，Phase 1 會畫成「*Eric* replied to <被回覆那則的連結>」，頂層看得出在回哪一則 |
| 頂層 | 不再貼，那則就代表 Teams 上的這句 |

兩種都在發話的那則按上「已送出」表情。

- **狀態用表情**，不在頻道留言：⏳ 送出中 → API 回 201 就換成「已送出」表情；失敗 ⚠️，原因寫在確認那則（經 `response_url` 改掉）；結果不明而且查不了時留著 ⏳（見下面〈失敗與重試〉）。換表情一律先按新的再拿掉舊的：中間一步失敗時那則至少還帶著一個狀態，防重送與重啟後的辨認都靠它。換表情失敗也一定回話：已經送到就說送到了（不給 Retry），沒送到照樣給 Retry。201 帶著 Teams 給的訊息 id，已經是 Teams 收下的證明，不用等推回來才標；推回來只決定貼不貼（上表）。
- **推回可能比 API 回應早到**（推播約 0.4 秒就到）：POST **之前**就在記憶體登記 `clientMessageId` →（頻道、`ts`、頂層還是 thread），推回時才查得到。
- **失敗與重試**：
  - gateway 回 4xx（請求有問題、對話不存在）：確定沒送。確認那則改成錯誤原因加 **Retry** 鈕，按了拿掉 ⚠️ 照 Send 的流程重來。
  - 逾時、5xx、連不上：不知道送了沒。Teams **不會**用 `clientmessageid` 去重（同一個 id 送兩次就是兩則，兩則都會推回來），直接重送可能變兩則。所以過幾秒經 gateway 找那個對話裡有沒有這個 `clientMessageId`：照 `nextCursor` 往前翻，翻到比那則 Slack 訊息還舊的就停（送出不可能比它早；看 `version` 不看 `composetime`，chatsvc 的排序沒驗證過），最多 40 頁。找到就當成功（換成已送出），確定沒有才按 ⚠️ 給 Retry。查不完（gateway 出錯、游標重複、翻到上限）時結果仍不明：留著 ⏳ 與登記、不按 ⚠️，確認那則改成「Send outcome unknown」加 **Retry** / **Dismiss**；按 Retry 照〈防重送〉先再查一次。
  - 確認不見了（ephemeral 不保存）：重新 @bot 一次。那是新的一則（`ts` 不同），跟原本那則無關。（所以不經過〈防重送〉的查詢：原本那則還停在 bot 的 ⏳、或帶著 ⚠️ 時都可能已經送出——只有 gateway 拒絕的 ⚠️ 確定沒送，確認那則不見了就分不出來——先到 Teams 確認或等 📤，再 @bot。）
  - 送出中確認那則是「Sending to Teams…」加 **Check again**：relay 送到一半重啟、或最後的結果沒回到確認那則時，按它會照〈防重送〉先查再決定；送出中按了被鎖擋掉，什麼都不做。
- 「已送出」一律按 📤（`outbox_tray`）當固定標記，防重送與重啟後的辨認都靠它：只認可以改的名稱的話，改過設定後之前發的那幾則就認不得了。`Relay:Slack:SentEmoji` 可以另外設一個顯示用的表情（預設也是 `outbox_tray`，這時只有一個）；想用自訂表情（例如用 relay 的 icon 做 `:teams-relay:`）要手動上傳一次再改設定，bot 在 📤 之外再按上它。它只是顯示：Slack 回 `invalid_name`（打錯或還沒上傳）就只留 📤、記 warning、之後不再試，其他失敗也只記 warning。
- **只認 bot 自己按的表情**：人手動按 📤 沒有意義。反過來，Teams 上有人按了跟狀態同名的表情（例如 `SentEmoji` 設成 `+1` 時的讚），不鏡像到人發的那則，只在 thread 留言：不然收回時會連 bot 的狀態一起拿掉。

### 用 `clientMessageId` 連起 Slack 與 Teams

- relay 發話時用那則 Slack 訊息的 `ts` 去掉小數點當 `clientMessageId`（`1729834567.123456` → `1729834567123456`，16 碼；Teams 原樣保存，推播、讀回來都一樣）。推回來的訊息帶著它，就知道是哪一則 Slack 訊息發的。換回 `ts` 用字串處理，在倒數第 6 碼前插回小數點，不經過浮點數。
- Teams 自己的 client 產生的 id 是 13 碼（毫秒時間戳）到 19 碼（亂數），16 碼也有，長度分不開。所以要 16 碼、全是數字，而且換回的 `ts` 落在訊息時間之前一段時間內（例如 24 小時，確認到送出的間隔），才當成可能是 Slack 發的；亂數幾乎不會落進這個範圍，也就不會為了它多打 Slack API。最後還要那則 Slack 訊息上有 bot 的狀態表情才算數。
- **防重送**：兩次 Send（連點、Socket Mode 重送事件）是同一則訊息。以頻道 + `ts` 在記憶體裡上鎖（relay 只跑一份），再看那則上 bot 按的表情：有已送出就不送；還停在 ⏳（沒人在送，表示上次送到一半 relay 重啟、Slack 出錯，或結果不明）或帶著 ⚠️（當時查不到，但可能之後才出現）就先照〈失敗與重試〉找一次，找到就換成已送出、不送，確定沒有才送，查不完就再回「Send outcome unknown」、留著 Retry。只靠查表情擋不住幾乎同時的兩次點擊，Teams 也不會替我們去重。發話與推回兩邊同時結束時（推回來的比查詢先到），同一則一次只讓一邊換狀態，已經確認送到的不會再被標成失敗。
- **推回來的處理**：先查記憶體裡剛送出的；查不到（relay 重啟過）就照上一條把 id 換回 `ts`，用 `reactions.get` 看那個頻道的那則訊息上有沒有 bot 按的 ⏳ / 已送出 / ⚠️。重啟時可能還停在 ⏳；有 ⚠️ 的表示其實送到了。有就照上表處理（頂層的不貼），狀態換成已送出；`reactions.get` 回的訊息帶 `thread_ts`，分得出是頂層還是 thread。沒有就當一般的 Teams 訊息。
- **對應表**：頂層發的那則沒有 bot 的 metadata 可以夾 Teams id。API 回 201 時就記進 `MessageLinks`（Teams id → 那則 Slack 訊息），推回來時沒在記憶體裡（重啟過、或 API 回應沒帶 Teams id）再補記。之後 Teams 那邊引用回覆它、按表情，都接得回這則。thread 裡發的推回來時是 bot 貼的，照原本的 metadata 機制。
- **重啟回補**（`WarmupLinksAsync`）這一版不補人發的那幾則：續接點只看帶 metadata 的 bot 訊息，頻道最後一則是自己發的時候，續接點會退到更早的那則 bot 訊息。退了也不會出錯：之後的事件會再推一次，自己發的那則照上一條查到狀態表情，不貼、順便補回對應。代價是重啟後、那則還沒再推回來之前，Teams 上引用它或按表情接不回 Slack（見〈之後〉）。

### Slack 連線

- **Socket Mode**：relay 主動連出去，部署那台不用開對外的 HTTP。要 app-level token（`xapp-…`，`connections:write`），設定是 `Relay:Slack:AppToken`；沒設就不開發話，只轉送（Phase 1 照舊）。
- 只訂 `app_mention`，不訂 `message.channels`：relay 只拿到有 @bot 的訊息，讀不到頻道裡其他討論。
- manifest 加：`socket_mode_enabled`、`interactivity`、事件 `app_mention`，scope `app_mentions:read`、`reactions:read`、`users:read`（@ 換名字）。
- Native AOT 用不了 Slack 的 SDK：Socket Mode 用 `ClientWebSocket` 自己接（收 envelope、回 ack、遇到 `disconnect` 重連），payload 走 `SlackJsonContext`。envelope 收到就先 ack（Slack 要 3 秒內），處理丟到背景；同一個事件重送的照 `event_id` 略過。
- 發話跟 Phase 1 的轉送是兩條同時跑的路：發話的流程放在自己的類別，跟 `RelayWorker` 共用的狀態（剛送出的、`MessageLinks`）要能並行存取。`RelayWorker` 裡逐筆處理用的 `Dictionary` 不能直接拿來共用。
- 開了發話時，`/health` 也看 Socket Mode 連著沒：relay 的工作包含發話，斷了要看得出來。

### 之後

- 圖片、附件：還沒研究過 Teams 那邊怎麼發，要先做 spike。目前的推測：
  - 圖片：網頁版是先傳到 Teams 的媒體服務（AMS），再在 `RichText/Html` 訊息裡用 `<img>` 引用。gateway 讀圖時已經在代抓同一種網址（`/v1/media`）。
  - 附件：個人版的檔案放在發送者的 OneDrive（`AttachmentDto` 的 `ShareUrl`），要先上傳 OneDrive、建分享連結，再掛在訊息上。可能要另外的 token，最麻煩。

  做的時候 gateway 的發話 API 跟著擴充，relay 確認時就不再提醒。
- Slack 上編輯、刪除已送出的那則，同步到 Teams：
  - 跟發話一樣要確認，不自動同步：Teams 那邊是對外的，順手改個錯字不該直接改過去。偵測到編輯就回 ephemeral「Already sent to Teams. Update it there too?」**[Update]** / **[Keep]**，預覽改過的內容；刪除就問「Delete it in Teams too?」。
  - 刪除選 Keep 時，頂層發的那則在 Slack 上沒了、Teams 上還在，bot 把它補貼成一般的「*Eric*: …」，頂層的 Teams 對話才不會缺一句。
  - 同步過去的編輯、刪除 Teams 會再推回來：thread 裡發的照 Phase 1 原地更新 bot 那則；頂層發的要認出是自己剛同步的、跳過（bot 改不了人發的訊息）。
  - 要先做 spike 確認 Teams 上改、刪的 API，gateway 再加對應的端點。
  - 要偵測 Slack 上的編輯、刪除，relay 得改訂 `message.channels`（`app_mention` 收不到刪除，編輯也不保證會再送），只理會帶 bot 已送出表情的那幾則。這會改掉上面「只訂 `app_mention`」的決定，到時在那句後面用括號補充。
- 📤 批准別人擬的草稿：同樣要訂 `message.channels`，等真的需要再說。
- 重啟回補人發的那幾則：讀頻道歷史時，帶 bot 已送出表情的人發訊息也算（history 本來就附 reactions），拿 gateway 近期訊息的 `clientMessageId` 比對補回對應，也算進續接點。頂層發話只會出現在只對到一個對話的頻道，要讀哪個對話是確定的。

## 背景

- 目標是只開 Slack 就能收發（relay README）。Phase 1 只轉進來；要能回，又不能讓頻道裡隨口講的話被送出去：頻道之後是討論怎麼回的地方。
- 考慮過的觸發方式：
  - 頂層打字就送：最直覺，但頻道就不能討論了。
  - 在草稿上按 📤：適合「別人擬、我批准」，但要讀整個頻道，而且按下去沒有再看一眼的機會。
  - message shortcut 開 modal：可以改、可以挑目標，但步驟多，手機上麻煩。
  - **開頭 @bot + ephemeral 確認**：寫的時候就決定要送，確認畫面讓你看到送去哪、會送出什麼；relay 只收得到 @ 它的訊息。
- 確認用 ephemeral 而不是 modal：留在對話裡、手機上也順，別人看不到，不會干擾討論。
- 推回來的分兩種處理：thread 裡的話不搬到頂層，只看頂層就拼不出 Teams 上的對話；頂層的話再貼一次，同一句會連著出現兩次。
- 狀態用表情不用留言：留言會讓每則發話底下都開一個 thread。表情掛不了 metadata，所以用 `clientMessageId` = Slack `ts` 讓兩邊不用另外存對照，跟 Phase 1「狀態都放在 Slack 上」一致。
- 自訂表情不能由 bot 自動加：只有 Enterprise Grid 的 `admin.emoji.add` 能加，一般 workspace 的 bot token 沒有這個權限。
- Teams 上改到頂層發的那則時不假設「沒人會直接用 Teams 改」：帳號怎麼用不是 relay 能保證的，貼進 thread 不管哪種情況都成立。
- 發話的細節先 spike 過（`spike/send_variants.py`，結論在 `spike/FINDINGS.md`）：201 回應帶新訊息的 Teams id、16 碼的 `clientmessageid` 原樣保存、同一個 id 不去重、`RichText/Html` 的格式與表格畫得出來、引用回覆要 blockquote 與 `qtdMsgs` 都有。原本打算等推回來才標已送出，再加 2 分鐘逾時與「未確認」狀態；API 直接回 id 之後就不需要了。引用回覆與格式也因此從〈之後〉提前到這一版。

## 代價與注意

- 發話用的是 gateway 綁的那個 Teams 帳號。白名單管的是「誰能用這個帳號說話」，不要隨便加人。
- ephemeral 不保存：重新整理、換裝置就不見。不見就是沒送，要送再 @bot 一次；不會在不知情的情況下送出。（只限還沒按 Send 的確認。按了之後不見——重新整理、換裝置，或在結果那則按 Dismiss——不代表沒送：那則還帶著 bot 的 ⏳ 或 ⚠️ 就可能已經送出，先到 Teams 看或等 📤，再決定要不要重新 @bot。）
- 送出後在 Slack 編輯、刪除，Teams 不會跟著變，relay 也看不到（沒訂 `message.channels`），不會提醒。
- 反過來，在 Teams 那邊編輯、刪除從頂層發的那則：bot 不能改人發的訊息（`chat.update` 回 `cant_update_message`），所以不走 Phase 1 的原地更新，改在那則的 thread 貼「_Edited in Teams_」加新版（刪除是「_Deleted in Teams_」），Slack 上的原文不動。對應表記著那則不是 bot 的（`MessageLinks` 的 `ByBot`），才分得出要走哪條。thread 裡發的推回來時是 bot 貼的，照 Phase 1 原地更新。「Edited / Deleted in Teams」那則夾著 Teams id 與版本（metadata），對應表沒記版本時（剛送出、重啟後從表情補回來）先讀那則 thread：重啟後 gateway 重送之前的編輯，貼過的版本不再貼。
- 這一版不送附件、圖片。Slack 的格式大多轉得過去，Teams 沒有對應的（例如 `@usergroup`）照字面送。
- 結果不明時，「經 gateway 找有沒有這個 `clientMessageId`」是唯一擋重送的地方：Teams 不去重。找的時候訊息剛好還沒出現，會誤判成沒送，按 Retry 就多一則。找的時候使用者在等：從現在往前翻到那則 Slack 訊息寫下的時間，通常一兩頁；確認之後隔很久才按 Send、對話又很熱鬧時頁數會多，最多 40 頁，超過就當查不完。
- `clientMessageId` 換回 `ts` 後查不到東西時，當一般的 Teams 訊息處理。誤判要 Teams 自己的 client 產生的 16 碼亂數剛好落在時間範圍內、又剛好對到那個頻道某則帶 bot 狀態表情的訊息，不另外防。
- 重啟後的處理多打 Slack API（`reactions.get`、讀歷史），只發生在推回來的是自己發的訊息、記憶體裡又沒有的時候。
- 從 Slack 發的和經其他路徑發的（別的 API key、`/mcp` 的 `send_message`），推回來時 `From` 都是帳號自己。後者照 Phase 1 由 bot 轉送，兩者只靠 `clientMessageId` 分得出來。
