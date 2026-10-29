# ADR 0013：送到 Teams 之後在 Slack 上改、刪：問過才同步

## 決定

從 Slack 送到 Teams 的那則（ADR 0012），之後在 Slack 上改了、刪了，relay 回一則只有本人看得到的確認，按了才同步到 Teams。**不自動同步**：Teams 那邊是對外的，順手改個錯字不該直接改過去。

### 怎麼問

| Slack 上 | 確認 | 按鈕 |
|---|---|---|
| 改了已送出的那則 | 「Already sent to Teams. Update it there too?」加改過的預覽 | **Update**：Teams 上那則整則換掉；**Keep**：只改 Slack |
| 刪了已送出的那則 | 「Deleted here, but it's still in Teams. Delete it there too?」加刪之前的預覽 | **Delete**：刪 Teams 上那則；**Keep**：留在 Teams |

- 只理會轉送的頻道、`Relay:Slack:Senders` 裡的人、已經送到 Teams 的那幾則（bot 按了已送出表情）。還沒按 Send 就改了不問：按 Send 時本來就讀最新的。
- 確認沒按等於 Keep。ephemeral 不保存，不見了就是沒同步；要同步再改一次。
- 刪除選 Keep 只是收掉確認：Teams 上留著，Slack 上那句沒了，不補貼回頂層（理由見〈背景〉）。
- 按 Update 時才重讀那則，確認前又改了就送最後那版。開頭的 @bot 照發話時的規則拿掉；拿掉之後沒內容就不改，請改用刪除。

### 怎麼偵測

- relay 改訂 `message.channels`（scope 本來就有 `channels:history`）：`app_mention` 收不到刪除，編輯也不保證會再送。頻道裡每一則都會送來，只處理 `message_changed` / `message_deleted`，其他的連事件去重都不進。
- 編輯：`message_changed` 新版帶 `edited`、而且文字跟舊版不同才算。沒有 `edited` 的是 Slack 自己補的（連結預覽之類）。事件裡的那份不保證帶表情，重讀一次看送過了沒。
- 刪除：`message_deleted`；有回覆的頂層訊息被刪時，Slack 送的是 `message_changed`、新版是 `tombstone`，一樣當成刪除。之後那則 thread 有動靜時 Slack 還會再送 `message_changed`（舊版也是 `tombstone`），不再問。刪掉的讀不到了，只能看事件裡刪之前那份（作者、在不在 thread、表情）。那份沒附表情時，開頭不是 @bot 的一定沒送過；是的話到 Teams 查一次，送過才問。

### 同步過去

- Teams 上是哪一則：用那則的 `clientMessageId`（= Slack `ts`，ADR 0012）經 gateway 到對話裡找，跟發話結果不明時同一套。relay 重啟過也找得到，不用另外記對照。對話照發話時的規則決定（thread 上層的 metadata、或頻道只對到的那個對話）。
- 原本是引用回覆的，改的時候引用看 Teams 上那則現在引用誰，不從 Slack 的 thread 上層推：上層可能已經在 Slack 上刪了，推不回來。
- gateway 加兩個端點，只改 / 刪綁定帳號自己發的：
  - `PUT /v1/chats/{id}/messages/{messageId}`：`text` 或 `html`，整則換掉。原本是引用回覆的要再帶 `replyToId`：Teams 的編輯不留原本的引用，不帶就變成普通訊息。回 204；沒有這則 404、已經刪了 409。
  - `DELETE /v1/chats/{id}/messages/{messageId}`：回 204，刪過的再刪也是 204；沒有這則 404。
  - 編輯的 body 不帶 `clientmessageid`：沒帶時 Teams 留著原本的，relay 之後還認得出這則是從 Slack 發的。
- 改、刪重送都安全（整則換掉、刪過的再刪也成功），所以不像發話要先查有沒有送到：連不上、5xx 給 **Retry**。gateway 回的 404（`ApiError`「沒有這則」）/ 409 是 Teams 上已經沒有了，直接說明；其他 4xx（例如 gateway 還沒有這兩個端點、引用的那則 gateway 沒收過）重試也沒用，只說明被拒。

### 推回來的編輯、刪除

- 同步過去的 Teams 會再推回來。thread 裡發的照 Phase 1 原地更新 bot 那則（舊版進 thread 留證）。
- 頂層發的那則平常在 Teams 上被改時，bot 會在它的 thread 貼「_Edited / Deleted in Teams_」（ADR 0012）。改成先看 Slack 上那則現在的樣子：已經跟 Teams 推回來的一樣就不貼，記下版本。一樣是指：刪除時 Slack 上也刪了；編輯時把 Slack 上那則照發話的規則轉成 HTML，跟 Teams 的內容比看得到的文字。從 Slack 同步過去的推回來一定一樣；relay 重啟後 gateway 重送同一個編輯也一樣認得出來。
- Slack 上那則已經刪了、Teams 上只是改了，也不貼：刪掉的訊息底下沒地方貼，那是本人刪的。

## 背景

- ADR 0012 把這件事放在〈之後〉：送出後在 Slack 編輯、刪除，Teams 不會跟著變，relay 也看不到。實際用的時候第一個想試的就是改錯字。
- Teams 上改、刪自己的訊息先 spike 過（`spike/edit_delete.py`，結論在 `spike/FINDINGS.md`〈編輯與刪除〉）：`PUT` / `DELETE` 那則訊息；`PUT` 不回新版本，id 與 `clientmessageid` 不變；引用回覆不重帶引用就變成普通訊息；刪掉的再改是 403 `MessageAlreadyDeleted`，不存在的是 404。推播一律是 `MessageUpdate`，gateway 原本就認得編輯與刪除。
- 確認一樣用 ephemeral：跟發話同一種操作，留在對話裡、別人看不到。
- 推回來時認「是不是自己改的」：
  - 原本打算同步前在記憶體記下那則，推回來時消掉。但 relay 重啟後 gateway 會從續接點重送事件，同步過的編輯還在重送範圍裡時，記憶體裡沒有，就會多貼一則；`PUT` 沒造成看得到的變化時 gateway 不推，記下的那筆又會留著、吃掉之後真的在 Teams 上改的那次。
  - 改成比 Slack 上現在的內容。Teams 存回來的 HTML 會多 `\r\n`、`<tbody>`，所以只比看得到的文字；兩邊都從同一份 Slack 訊息轉出來，比得準。比錯了只是多貼一則 note。這也剛好是 note 該不該貼的本意：Slack 上已經是這樣了，就沒有新東西要給人看。
- 刪除選 Keep 不補貼回頂層（ADR 0012〈之後〉原本這樣打算）：補貼的那則在 Slack 上是新的，對到的卻是舊的 Teams 訊息；relay 重啟時從 Slack 上最新的 bot 訊息決定從哪裡續接，會從那則舊訊息之後整段重送，超過回補範圍的就重貼一次。要做得在 metadata 標記補貼的、從續接點和原地更新一路帶著。換來的只是「自己刪掉的那句在 Slack 頂層還看得到」，先不做。

## 代價與注意

- 訂了 `message.channels`，頻道裡每一則都會經 Socket Mode 送到 relay。轉送的頻道量不大，收到就丟；換成熱鬧的頻道要再看。改 manifest 要重新 Install。
- private 頻道要另外訂 `message.groups`（加 `groups:history`）才收得到；現在轉送的都是 public，沒加。發話靠 `app_mention`，private 頻道照樣能發，只是改、刪不會問。
- 推回來時要讀一次 Slack 上那則（頂層發的那則在 Teams 上被改、刪才會），@人 換名字也可能要查 `users.info`。
- 刪掉的讀不到，判斷靠事件裡刪之前那份。那份沒有作者就不處理（不知道該問誰）。
- 按了 Send、還停在 ⏳ 或 ⚠️（結果不明）時改的不會問：那時還不知道送到了沒。Teams 上是改之前的版本，要同步就等換成 📤 後再改一次。
- 別人的訊息改不改得了沒驗過（測試群組只有自己）；relay 只改自己發的，不會走到。
- 在 Slack 上刪了、選 Keep 的那則：Teams 上之後有人引用、按表情，接不回 Slack（原本那則沒了），引用退回畫成引用格式。
