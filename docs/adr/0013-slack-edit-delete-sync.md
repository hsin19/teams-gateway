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
- 刪除選 Keep：頂層發的那則在 Slack 上沒了、Teams 上還在，bot 照一般轉送補貼回頂層（「*名字*: …」，夾 Teams id 的 metadata），只看頂層才不會缺一句；之後 Teams 上的編輯照 Phase 1 原地更新這則。thread 裡發的頂層本來就有 bot 那則，Keep 只是收掉確認。
- 按 Update 時才重讀那則，確認前又改了就送最後那版。開頭的 @bot 照發話時的規則拿掉；拿掉之後沒內容就不改，請改用刪除。

### 怎麼偵測

- relay 改訂 `message.channels`（scope 本來就有 `channels:history`）：`app_mention` 收不到刪除，編輯也不保證會再送。頻道裡每一則都會送來，只處理 `message_changed` / `message_deleted`，其他的連事件去重都不進。
- 編輯：`message_changed` 新版帶 `edited`、而且文字跟舊版不同才算。沒有 `edited` 的是 Slack 自己補的（連結預覽之類）。事件裡的那份不保證帶表情，重讀一次看送過了沒。
- 刪除：`message_deleted`；有回覆的頂層訊息被刪時，Slack 送的是 `message_changed`、新版是 `tombstone`，一樣當成刪除。刪掉的讀不到了，只能看事件裡刪之前那份（作者、在不在 thread、表情）。那份沒附表情時，開頭不是 @bot 的一定沒送過；是的話到 Teams 查一次，送過才問。

### 同步過去

- Teams 上是哪一則：用那則的 `clientMessageId`（= Slack `ts`，ADR 0012）經 gateway 到對話裡找，跟發話結果不明時同一套。relay 重啟過也找得到，不用另外記對照。對話照發話時的規則決定（thread 上層的 metadata、或頻道只對到的那個對話）。
- gateway 加兩個端點，只改 / 刪綁定帳號自己發的：
  - `PUT /v1/chats/{id}/messages/{messageId}`：`text` 或 `html`，整則換掉。原本是引用回覆的要再帶 `replyToId`：Teams 的編輯不留原本的引用，不帶就變成普通訊息。回 204；沒有這則 404、已經刪了 409。
  - `DELETE /v1/chats/{id}/messages/{messageId}`：回 204，刪過的再刪也是 204；沒有這則 404。
  - 編輯的 body 不帶 `clientmessageid`：沒帶時 Teams 留著原本的，relay 之後還認得出這則是從 Slack 發的。
- 改、刪重送都安全（整則換掉、刪過的再刪也成功），所以不像發話要先查有沒有送到：失敗一律給 **Retry**。404 / 409 重試也沒用，直接說明。

### 推回來的編輯、刪除

- 同步過去的 Teams 會再推回來。thread 裡發的照 Phase 1 原地更新 bot 那則（舊版進 thread 留證）。
- 頂層發的那則平常在 Teams 上被改時，bot 會在它的 thread 貼「_Edited / Deleted in Teams_」（ADR 0012）。從 Slack 同步過去的不貼：Slack 上本來就是這樣了。relay 同步前在記憶體記下那則 Teams id（10 分鐘），推回來時消掉這筆、記下版本（重送才不會當成新的編輯）；沒改成就撤掉。只抵那一次，之後在 Teams 上再改照常貼。

## 背景

- ADR 0012 把這件事放在〈之後〉：送出後在 Slack 編輯、刪除，Teams 不會跟著變，relay 也看不到。實際用的時候第一個想試的就是改錯字。
- Teams 上改、刪自己的訊息先 spike 過（`spike/edit_delete.py`，結論在 `spike/FINDINGS.md`〈編輯與刪除〉）：`PUT` / `DELETE` 那則訊息；`PUT` 不回新版本，id 與 `clientmessageid` 不變；引用回覆不重帶引用就變成普通訊息；刪掉的再改是 403 `MessageAlreadyDeleted`，不存在的是 404。推播一律是 `MessageUpdate`，gateway 原本就認得編輯與刪除。
- 確認一樣用 ephemeral：跟發話同一種操作，留在對話裡、別人看不到。
- 推回來時認「是不是自己剛改的」用記憶體，不用比內容：Teams 存回來的 HTML 會被改寫（多 `\r\n`、引用照原訊息重寫），比不準；`PUT` 也不回新版本，沒辦法先記版本。

## 代價與注意

- 訂了 `message.channels`，頻道裡每一則都會經 Socket Mode 送到 relay。轉送的頻道量不大，收到就丟；換成熱鬧的頻道要再看。改 manifest 要重新 Install。
- 記憶體裡的「剛同步過去」只活在這個 relay：按了 Update 之後、推回來之前 relay 剛好重啟，頂層那則會多貼一則「_Edited in Teams_」。不另外防。
- 刪掉的讀不到，判斷靠事件裡刪之前那份。那份沒有作者就不處理（不知道該問誰）。
- 別人的訊息改不改得了沒驗過（測試群組只有自己）；relay 只改自己發的，不會走到。
- Keep 補貼回頂層的那則是 bot 貼的一般轉送，時間是補貼的時間，排在之後的訊息後面。
