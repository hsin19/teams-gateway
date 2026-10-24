# ADR 0006：事件出入口抽象化，傳輸 infra 可替換

## 決定

- **事件的產生跟傳輸分開。** gateway 只負責把事件可靠地寫進內部 log；送去哪裡（SSE / webhook / message broker…）是可抽換、可同時多個的 sink。
- **訂閱端只依賴抽象的事件來源。** relay 透過 `IEventSource` 收事件，背後是 SSE 還是 broker 由設定決定。
- 換 / 加一種傳輸 = 多一個 sink 或 source 實作 + 設定；變更偵測、Slack 轉送這些核心邏輯不動。
- 現在只做抽象跟既有的實作（SSE、webhook），不預先接任何 broker。下面用 Kafka 當例子，檢驗抽象夠不夠用。
- 修正 ADR 0002 的事件匯流排：行程內 bus 降為「有新事件」的通知，可靠性改靠內部 log + 各 sink 自己的游標。

## 背景

- 事件是這個服務最有價值的產出，下游需求會變：多個 consumer 各自的進度、從頭回放（例如用新格式把 Slack 重轉一次）、餵 ES 做歷史搜尋。適合的傳輸不一樣——SSE 適合輕量即時，broker 適合多 consumer 跟回放——不該綁死在其中一種。
- 現在綁死的地方：
  - gateway：sink 直接訂閱行程內的 `GatewayEventBus`，它滿了丟最舊的。SSE 看得出序號斷洞會回 DB 補；webhook 沒有游標，被丟或對方剛好掛了，那筆就沒了。再加一種傳輸會有一樣的問題。
  - relay：`RelayWorker` 自己處理 SSE 的細節（`lastId`、從 Slack 歷史推的 `resumeAfter`、重連、退避），換傳輸就得改 worker。
- 可以換的是傳輸，不是 gateway 的 DB：gateway 做的是變更偵測。Trouter / 輪詢 / 慢更新會重複看到同一則，要跟上一版比才知道是新訊息、編輯還是有人按表情；輪詢每 20 秒重抓 15 分鐘內的訊息，不去重直接往外送會很吵。DB 留著當比對狀態 + 內部 log。

## gateway：內部 log + 各 sink 自己的游標

```
Trouter / 輪詢 / 回補
        │  跟訊息同一個交易
        ▼
  PublishedEvents（內部 log，Seq = offset）
        │  bus 只發「有新的」通知
        ├─ SSE 端點      游標在 client（Last-Event-ID）
        ├─ webhook sink  游標存 DB
        └─ broker sink   游標存 DB（例：Kafka，需要時才加）
```

- 每個 sink 同一個介面：從自己的游標往後批次讀 → 送出 → 成功才推進游標。開哪些由設定決定（例如 `Gateway:Sinks:<名稱>:Enabled`）。
- pruner 只清「所有啟用中的 sink 都送過」而且超過保留天數的事件：下游掛幾天也不掉。
- webhook 換到這套上，順便變可靠（失敗重試、不再被丟）。

## relay：`IEventSource`

```
RelayWorker ── IEventSource ──┬─ SseEventSource     （Last-Event-ID / afterMessage）
                              └─ broker source      （例：Kafka consumer group，需要時才加）
```

- `lastId`、`resumeAfter`、重連、退避從 `RelayWorker` 搬進 source。
- 介面就是 `IAsyncEnumerable`，約定「拿下一筆 = 上一筆處理完」：SSE source 這時更新 `lastId`，broker source 這時 commit。worker 本來就逐筆處理，不用改。（後來多了一個 `Connected` 屬性給 relay 的 `/health`，broker source 也要回報自己連著沒，見 relay README〈健康檢查〉。）
- 設定切換：`Relay:Source = Sse | …`。

## 事件契約：跟傳輸無關

抽象要能換 infra，契約就只能承諾每種傳輸都做得到的事：

- **所有事件種類同一個有序串流**（訊息、編輯、刪除、表情），用 `kind` 區分，不認得的 `kind` 跳過。表情一定要跟它的訊息在同一個串流，順序才保證得了。
- **順序只保證在同一個對話內，分區鍵 = `conversationId`。** consumer 不能依賴跨對話的先後——現在 SSE 剛好是全域有序，但分區式的 broker 做不到，這是讓它們接得上的最低承諾。
- **去重 / 游標分對話記。** 例如表情事件沒有版本號，重送會在 Slack 多一則「reacted」，要靠 seq 去重；記的是「每個對話處理到的 seq」，不能只記全域最大值——分區後，全域最大值會把別的對話還沒處理的事件當成處理過。
- **內容一份，各傳輸自己決定放哪**：`seq`、`kind`、`conversationId`、`schemaVersion` + payload。SSE 放在 `id` / `event` / `data`。
- **payload 要帶 Teams 原始 JSON**：DTO 是挑過的欄位，之後用新格式回放時，新邏輯要的欄位不一定在裡面。

## 例：接 Kafka

檢驗上面的抽象：接 Kafka 只需要一個 sink 跟一個 source，契約不用改。

- **對應**：sink 照 seq 送進一個 topic（例如 `teams.events`），key = `conversationId`，`kind` / `seq` / `schemaVersion` 放 header；source 用 consumer group，「拿下一筆」時 commit 上一筆的 offset。
- **為什麼是一個 topic、key 用對話**：
  - Kafka 只保證同一個 partition 內有序。表情分到別的 topic，就可能先收到表情、找不到訊息可掛。
  - 不一個對話開一個 topic：順序保證一樣，卻要每訂閱一個對話就去建 topic、開權限。
  - 不用 `messageId` 當 key：會丟掉同一對話裡訊息之間的順序（Slack 顯示順序、回覆跑到被回覆的前面）。
- **實作時要先定的**：
  - partition 數一開始定好：之後加 partition，key 會對到別的 partition，切換期間順序會亂。
  - producer 照 seq 順序送，開 idempotent producer，重試才不會打亂順序。
  - retention 要夠長才能從頭回放；不開 compaction（每個 key 只會留最後一筆，舊版本就沒了）。
  - schema 格式（JSON + `schemaVersion`，或 Avro / Protobuf + Schema Registry）、ACL（內容是群組對話，誰能讀要管）。
  - gateway 連不到 broker 時（例如睡眠、斷網），事件先留在內部 log，連上再補送。
- **換了傳輸也不會解耦的**：
  - 圖片還是要靠 gateway 的 `/v1/media` 帶登入去抓。回放時圖還抓不抓得到要另外評估，必要時另存一份。
  - 回放到 Slack：新 group 從頭讀會把每一則重發一次——發到新頻道，或用 Teams id ↔ Slack ts 對照做 `chat.update`。Slack 每頻道約每秒一則，量大要跑一陣子。

## 不選：在 gateway 外面接轉送服務（SSE → broker）

好處是 gateway 完全不用改。但多一個要部署、監控的程式，游標要自己找地方存；而且 gateway 不知道有它，它停超過保留天數（預設 7 天）的那段會被 pruner 清掉、補不回來。可靠性的機制本來就在 gateway 裡，放外面等於再做一次。

## 順序

1. gateway：內部 log + 各 sink 游標，webhook 換上去。
2. relay：抽出 `IEventSource`，先把 SSE 搬進去。
3. 需要時才加 broker 的 sink / source（例如 Kafka）。

## 實作（2026-10-23，1、2 完成）

- bus 只帶「最新序號」的通知、會合併；SSE 與 sink 被叫醒後一律從 `PublishedEvents` 讀，不再有「佇列滿了丟事件、看序號斷洞回 DB 補」那段。
- sink 介面是 `IEventSink`（`OldestPendingAsync` 給 pruner 算還不能清的序號）。webhook 的游標記在每個 `WebhookSubscription`（`DeliveredSeq`）：一個目標掛了只卡它自己，不擋其他 webhook。登記時從當下最新的序號開始。
- 補充決定：webhook 落後超過 `EventRetentionDays` 還送不出去就停用。「下游掛幾天也不掉」以保留天數為限，不讓一個死掉的目標撐著 log 無限長大。
- 契約：`schemaVersion` 與 `raw`（Teams 原始訊息 JSON）加在事件 payload（SSE 的 `data`、webhook 的 body）裡，DTO 型別不變；webhook 另外放 `X-TeamsGateway-Seq`、`X-TeamsGateway-Schema-Version` header，log 也記 `SchemaVersion` 欄位。
- relay：`SseEventSource` 收進 `Last-Event-ID`、`afterMessage` 續接、重連與退避；`Relay:Source` 目前只有 `Sse`。worker 不依賴跨對話的順序（訊息照版本、表情照序號去重）。
