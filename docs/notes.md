# Ghi chú quyết định kỹ thuật

Mỗi mục ghi **làm gì**, **vì sao**, và **giới hạn đã biết**. Mục đích là để người đọc sau (kể cả mình) không phải đoán lại lý do, và không "sửa" nhầm một chỗ cố ý.

---

## Ticket 1: Sự kiện và loại vé

### 1. Luật nghiệp vụ nằm ở đâu: 3 lớp

| Lớp | Kiểm gì | Ví dụ |
|---|---|---|
| Validator (FluentValidation) | Định dạng đầu vào | `Name` không rỗng, dài tối đa 200; `Page >= 1` |
| Entity (`Event`, `TicketType`) | Luật nghiệp vụ | Lịch bán vé phải trước giờ diễn; chỉ sửa khi còn `Draft`; publish cần ít nhất một loại vé |
| Database (`CHECK`, unique index) | Lưới an toàn cuối cùng | `price >= 0`, `0 <= available_quantity <= total_quantity` |

- Độ dài tối đa khai báo **một lần** bằng hằng trong entity (`Event.NameMaxLength`…), validator và cấu hình EF đều dùng lại hằng đó.
- Entity không có setter public. Muốn đổi trạng thái thì gọi method (`Publish`, `Cancel`, `Update`), method trả `Result` thay vì ném exception.
- Lỗi nghiệp vụ là `Error` có `Type`, được đổi ra mã HTTP ở một chỗ (`ToProblem`): NotFound → 404, Conflict → 409, Validation → 400.

### 2. Không dùng navigation property giữa Event và TicketType

`TicketType` chỉ giữ `EventId`. Cấu hình EF khai báo khoá ngoại bằng `HasOne<Event>().WithMany()`, không có thuộc tính điều hướng.

- **Vì sao:** mỗi aggregate tự bảo vệ luật của mình và tham chiếu aggregate khác bằng Id. Không có navigation thì không thể vô tình load cả cây đối tượng, cũng không bị lazy load sinh N+1.
- **Hệ quả:** cần đếm loại vé thì gọi repository (`CountByEventIdAsync`); cần chi tiết sự kiện kèm vé thì `GetEventById` chạy 2 truy vấn gọn, mỗi truy vấn chỉ `Select` cột cần dùng.

### 3. Khoá ngoại `OnDelete(Restrict)`

Không cho xoá cứng sự kiện khi còn loại vé. Sự kiện không bị xoá mà chuyển sang `Cancelled`; bảng dùng soft-delete (`is_deleted`). Nếu dùng `Cascade`, một lệnh xoá nhầm sẽ kéo mất dữ liệu vé (và sau này cả đơn hàng).

### 4. Tên loại vé không trùng trong một sự kiện, không phân biệt hoa thường

`VIP`, `vip`, ` Vip ` được coi là trùng nhau.

- **Cách làm:** collation ICU không tất định `case_insensitive` (`und-u-ks-level2`) gắn vào cột `ticket_types.name`, cộng unique index `(event_id, name)` có điều kiện `is_deleted = false`.
- **Vì sao collation mà không dùng `lower(name)`:** so sánh `=` và unique index tự hiểu hoa/thường bằng nhau; code C# không phải nhớ gọi `ToLower` ở mọi chỗ.
- **Vì sao index có điều kiện:** loại vé đã xoá mềm không được chiếm tên, nên tạo lại tên cũ vẫn được.
- **Giới hạn:** Postgres **không hỗ trợ `LIKE`** trên cột dùng collation không tất định. Nếu sau này cần tìm loại vé theo một phần tên, phải ép collation khác trong câu truy vấn hoặc dùng `lower()`.
- Handler `Trim()` tên trước khi lưu, vì collation không bỏ khoảng trắng hai đầu.

### 5. Kiểm trùng tên 2 lớp

1. `NameExistsAsync` trước khi thêm: trả lỗi 409 có thông điệp rõ ràng trong trường hợp thường gặp.
2. Bắt `UniqueConstraintException` lúc `SaveChanges`: chặn trường hợp hai request cùng tên đến gần như đồng thời, cả hai đều qua bước 1 (race condition). Unique index của DB là nơi duy nhất đảm bảo tuyệt đối.

Chỉ có bước 1 thì thiếu an toàn. Chỉ có bước 2 thì vẫn đúng nhưng phải dựa vào exception cho một tình huống bình thường.

### 6. Command ghi qua repository, query đọc qua `IReadDbContext`

- Command handler dùng repository + `IUnitOfWork` (có change tracking). Architecture test `Application_OnlyQueryHandlersQueryTheDatabaseDirectly` chặn command đọc DB trực tiếp.
- Query handler (tên kết thúc bằng `QueryHandler`) dùng `IReadDbContext.Query<T>()`: không tracking, đã áp query filter soft-delete, luôn kết thúc bằng `Select` sang DTO.

### 7. Ai thấy bản nháp

- `GET /events/{id}`: Admin và Manager (ban tổ chức) thấy mọi trạng thái; khách và User thường gọi vào bản nháp nhận **404** (không phải 403, để không lộ việc sự kiện tồn tại).
- `GET /events` (danh sách): **chỉ hiện sự kiện không phải `Draft`**, kể cả với Admin, Manager. Sự kiện `Cancelled` vẫn hiện, kèm trạng thái.
- **Việc còn để ngỏ:** ban tổ chức chưa có màn hình liệt kê bản nháp. Khi cần thì thêm `ICurrentUser` vào `ListEventsQueryHandler` giống `GetEventById`.

### 8. Phân trang danh sách sự kiện

- Sắp `OrderBy(StartsAt).ThenBy(Id)`. `Id` là khoá phụ để thứ tự **duy nhất**: thiếu nó, hai sự kiện cùng giờ có thể đổi chỗ giữa hai lần truy vấn, làm trang sau lặp hoặc mất dòng.
- Validator chặn `Page < 1` và `PageSize` ngoài khoảng 1–100: `Page = 0` sẽ thành `Skip(-n)` gây lỗi 500; `PageSize` quá lớn thì kéo cả bảng về.
- Đếm `TotalCount` trên cùng điều kiện lọc, trước khi `Skip/Take`.
- **Giới hạn:** phân trang kiểu offset chậm dần khi trang sâu (DB vẫn phải đi qua các dòng bị bỏ). Với vài nghìn sự kiện thì không đáng kể; nếu lớn thì chuyển sang keyset pagination (`WHERE (starts_at, id) > (@last_starts_at, @last_id)`).

### 9. Tìm kiếm sự kiện theo tên: `ToLower().Contains()`

```csharp
var search = request.Search?.Trim().ToLowerInvariant();
query = query.Where(e => e.Name.ToLower().Contains(search));   // EF dịch thành lower(name) trong SQL
```

- **Vì sao không dùng `EF.Functions.ILike`:** đó là hàm riêng của Npgsql. Project `Application` cố ý chỉ tham chiếu EF Core chung, không phụ thuộc provider Postgres. Đừng thêm package Npgsql vào Application.
- **Vì sao `ToLowerInvariant()` bên ngoài nhưng `ToLower()` bên trong lambda:** chuỗi bên ngoài chạy trong C#, phải độc lập với văn hoá của máy (ví dụ văn hoá Thổ Nhĩ Kỳ đổi `I` thành `ı`). Biểu thức trong lambda không chạy trong C# mà được dịch thành `lower()` của Postgres; Npgsql 10 chỉ dịch được `ToLower()` không tham số, dùng `ToLowerInvariant()` sẽ lỗi lúc chạy.
- Vì vậy dòng trong lambda tắt cảnh báo `CA1304, CA1311, CA1862` bằng `#pragma` **đúng một dòng**, kèm comment. Không tắt cho cả project vì cảnh báo này bắt bug thật ở code chạy trong C# (ví dụ `User.NormalizeEmail`).
- `Contains` không coi `%` và `_` là ký tự đại diện: người dùng gõ ký tự đó sẽ được tìm đúng ký tự (khác với `EF.Functions.Like` tự ghép chuỗi).
- **Giới hạn:** tìm chuỗi con không dùng được index B-tree, nên phải quét bảng. Khi dữ liệu lớn thì dùng `pg_trgm` (index GIN) hoặc full-text search, viết bằng Dapper trong query handler.
- Integration test `List_SearchIsCaseInsensitive_AndHidesDrafts` chạy thật trên Postgres để chứng minh EF dịch được biểu thức này.

### 10. Contract của API

- Request có body (POST/PUT) có record riêng ở `Api/Contracts`, không nhận thẳng command. Lý do: `Id` chỉ được lấy từ URL; body không có chỗ chứa `Id` nên không thể đè; command có thể thêm field nội bộ mà client không được gửi.
- GET chỉ có vài tham số query string thì nhận trực tiếp bằng `[FromQuery]` rồi dựng query, không cần record request. Khi số tham số lọc nhiều lên thì gom vào một record cho dễ đọc.
- Response dùng thẳng DTO của handler (`EventDetailResponse`, `ListEventsResponse`), không copy sang record giống hệt ở tầng Api.
- Tạo mới trả **201** kèm header `Location` và body `{ id }`.
- Endpoint đọc công khai phải ghi `[AllowAnonymous]`, vì fallback policy của app bắt buộc đăng nhập.

### 11. Test

- **Unit test** (xUnit + NSubstitute): luật của entity và từng nhánh của handler (không tìm thấy, sai trạng thái, trùng tên, lỗi lúc lưu…). Kiểm cả việc **không** gọi `SaveChanges` khi thất bại.
- **Integration test** (Testcontainers, Postgres + Redis thật): đi hết đường HTTP → DB, gồm phân quyền 401/403, mã lỗi trong ProblemDetails, collation, phân trang.
- DB dùng chung giữa các integration test, nên test cần đếm số dòng phải gắn mã riêng (`Guid`) vào tên rồi lọc theo mã đó.

### 12. Lọc theo ngày diễn

- `GET /events?from=&to=` lọc theo `starts_at`: `from ≤ starts_at < to` (nửa mở, để hai khoảng liền nhau như "tuần này" và "tuần sau" không đếm trùng sự kiện ở mốc giữa). Bỏ trống một đầu thì không giới hạn đầu đó. `from` phải nhỏ hơn `to`, sai thì 400.
- **Đổi mốc về UTC trước khi truy vấn:** client gửi giờ Việt Nam (`+07:00`) là bình thường, nhưng Npgsql chỉ nhận `DateTimeOffset` có offset 0 cho cột `timestamptz` và báo lỗi (API trả 500). `ToUniversalTime()` giữ nguyên thời điểm, chỉ đổi cách viết. Integration test gửi mốc `+07:00` để giữ chỗ này; bỏ `ToUniversalTime()` thì test đỏ.
- Index `(status, starts_at)` đã có từ đầu phục vụ đúng truy vấn này.

### 13. Quyền ban tổ chức: Admin và Manager

- Tạo, sửa, công bố sự kiện, thêm và sửa loại vé dùng policy `RequireManagerRole` (Admin hoặc Manager). Manager cũng thấy bản nháp, vì họ là người tạo và sửa nó.
- **Huỷ sự kiện chỉ Admin** (`RequireAdminRole`): ảnh hưởng mọi người đã mua và sau này kéo theo hoàn tiền.
- Integration test: Manager đi hết luồng tạo → xem nháp → sửa → thêm loại vé → công bố → sửa loại vé; Manager huỷ sự kiện nhận 403.

---

## Ticket 2: Đặt vé, giữ chỗ, huỷ, hết hạn

### 1. Mỗi đơn chỉ chứa vé của một sự kiện

`Order.Create` từ chối đơn có loại vé thuộc nhiều sự kiện (`Orders.MixedEvents`), và lưu `EventId` ngay trên đơn.

- **Vì sao:** thanh toán, hoàn tiền và huỷ sự kiện đều xử lý theo đơn. Một đơn trộn hai sự kiện thì khi một sự kiện bị huỷ phải tách đơn hoặc hoàn tiền một phần, phức tạp mà không có lợi gì cho người mua.
- Các luật khác trong `Order.Create`: đơn không rỗng, không lặp loại vé (`DuplicateTicketType`), số lượng > 0 và ≤ `max_per_order`.
- `unit_price` chụp giá lúc đặt; `TotalAmount` tính từ các dòng.

### 2. Giữ chỗ: trừ vé ngay lúc đặt, hết hạn sau 10 phút

- Đặt vé trừ `available_quantity` **ngay**, đơn ở `Pending`, `ExpiresAt = now + Order.ReservationMinutes` (10 phút). `now` lấy từ `TimeProvider` để test được.
- **Vì sao trừ ngay:** người đã đặt chắc chắn còn vé trong lúc đi thanh toán. Cái giá là vé bị giữ bởi người bỏ ngang, nên phải có job trả vé khi hết hạn (mục 6).
- Sự kiện phải `Published` và đang trong khung `[SaleStartsAt, SaleEndsAt)`, sai thì `Orders.NotOnSale` (409).

### 3. `PlaceOrder` bản đầu là bản đơn giản và bán vượt *(đã sửa ở Ticket 3)*

Luồng của Ticket 2: đọc loại vé → `Reserve` trừ trong bộ nhớ → `SaveChanges`.

- `SaveChanges` tự mở một transaction ngầm, nên đơn và số vé cùng lưu hoặc cùng không. Nhưng transaction đó **không** chặn được hai request cùng đọc `available = 1`, cùng trừ, cùng lưu: lần ghi sau đè lần ghi trước (lost update) và vé bị bán quá.
- **Cố ý để vậy:** Ticket 3 viết test chứng minh lỗi rồi so sánh ba cách sửa. Xem phần Ticket 3 bên dưới.

### 4. Huỷ đơn: UPDATE có điều kiện, không đọc rồi mới ghi

```csharp
// OrderRepository.TryCancelPendingAsync
UPDATE orders SET status = 'Cancelled' WHERE id = @id AND status = 'Pending'   -- trả true khi đổi được đúng 1 dòng

// TicketTypeRepository.TryReturnStockAsync
UPDATE ticket_types SET available_quantity = available_quantity + @n
WHERE id = @id AND available_quantity + @n <= total_quantity
```

Hai câu chạy trong **một** transaction (`IUnitOfWork.ExecuteInTransactionAsync`). Cộng vé lỗi thì rollback luôn câu đổi trạng thái.

- **Vì sao lo, khi chỉ một người huỷ đơn của mình?** Vẫn có hai luồng cùng chạm một đơn: người dùng bấm hai lần hoặc app tự retry, hoặc người dùng huỷ đúng lúc job hết hạn chạy. Nếu đọc `Status == Pending` trong C# rồi mới ghi, cả hai luồng đều thấy `Pending` và cùng cộng vé → kho bị cộng hai lần.
- **Vì sao UPDATE có điều kiện thì đúng:** điều kiện nằm trong chính câu ghi. Ở mức `READ COMMITTED`, UPDATE thứ hai phải chờ UPDATE thứ nhất commit, rồi kiểm lại `WHERE` trên dòng mới, thấy `Cancelled` nên đổi 0 dòng → trả `Orders.NotCancellable` (409) và không cộng vé.
- **Vì sao cộng vé bằng SQL:** `available_quantity + @n` tính trong DB, không đọc giá trị lên C# rồi ghi lại, nên không bị lost update khi có người đặt vé cùng lúc.
- Đơn của người khác trả **404** như đơn không tồn tại, để không lộ việc đơn đó có tồn tại.
- Thành công trả **204** (không có body).
- **Lưu ý khi sửa code:** chép `(TicketTypeId, Quantity)` ra biến thường **trước** khi vào transaction. Khi execution strategy chạy lại, `ChangeTracker` bị xoá nên entity đã load không còn dùng được.
- **Giới hạn:** `ExecuteUpdate` bỏ qua `AuditableEntityInterceptor`, nên `updated_at/updated_by` không được điền khi huỷ hoặc hết hạn. Cần thì thêm `SetProperty` cho hai cột đó.
- Lỗi `TicketTypes.UnreserveExceedsTotal` lẽ ra không bao giờ xảy ra; nếu có thì dữ liệu kho đã lệch từ trước.

### 5. Bỏ `Order.Cancel`, `Order.Expire`, `TicketType.Unreserve`

Khi luật trạng thái đã nằm trong `WHERE` của câu UPDATE, các method domain kiểm cùng luật đó không còn ai gọi, lại dễ lệch với SQL nếu sau này sửa một bên. Nên xoá.

- **Đánh đổi:** luật "chỉ huỷ / hết hạn khi `Pending`" giờ nằm ở repository (SQL), không ở entity. Vì vậy nó được chứng minh bằng integration test chạy trên Postgres thật, không phải unit test entity.

### 6. Job hết hạn đơn

```
OrderExpiryService (BackgroundService, PeriodicTimer 30s)
  → ExpireDueOrdersCommand(BatchSize: 100)
      → GetExpiredPendingIdsAsync: Pending AND expires_at <= now, cũ nhất trước, tối đa 100
      → mỗi Id: ExpireOrderCommand (transaction riêng, cùng kiểu với huỷ đơn)
```

- `ExpireOrderCommand` giống huỷ đơn, chỉ khác điều kiện: `status = 'Pending' AND expires_at <= @now`. Đơn vừa bị huỷ, vừa được thanh toán, hay chưa tới hạn đều đổi 0 dòng → `Orders.NotExpirable`, không trả vé.
- **Chạy lặp không sao:** quét hai lần, hay hai instance cùng quét, vẫn chỉ trả vé một lần nhờ UPDATE có điều kiện (chỉ tốn công chứ không sai).
- **Mỗi đơn một transaction:** một đơn lỗi được ghi log rồi bỏ qua, không làm rollback hay chặn các đơn khác.
- **Lỗi cả lượt quét không làm sập app:** từ .NET 6, exception thoát khỏi `BackgroundService` mặc định **dừng cả host**. Vì vậy vòng lặp bắt lỗi (trừ `OperationCanceledException` lúc tắt app), ghi log, rồi chờ lượt sau.
- **Cũ nhất trước:** khi tồn hơn 100 đơn, đơn chờ lâu nhất được trả vé trước và mỗi lượt lấy đúng nhóm đó. Index `(status, expires_at)` phục vụ đúng truy vấn này.
- Phần quét nằm ở Application (`ExpireDueOrdersCommand`), service ở Infrastructure chỉ hẹn giờ. Nhờ vậy integration test gọi thẳng qua `ISender`, không phải đợi timer.
- **Giới hạn:**
  - Đơn được trả vé chậm nhất khoảng 30 giây sau `ExpiresAt`.
  - Tối đa 100 đơn mỗi 30 giây. Nếu tồn nhiều hơn thì tăng `BatchSize` hoặc lặp lại trong cùng một lượt cho tới khi hết.
  - Tiền về sau khi đơn đã `Expired` (vé đã trả kho) để Ticket 4 xử lý.

### 7. Đọc đơn

- `GET /orders/my`: chỉ đơn của người gọi, phân trang (`PageSize` 1–100), sắp `CreatedAt` giảm dần rồi `Id` giảm dần. `Id` là Guid v7 tăng theo thời gian nên phá hoà đúng thứ tự.
- `GET /orders/{id}`: chủ đơn hoặc Admin; người khác **404**.
- Tên sự kiện và tên loại vé lấy bằng **truy vấn con** chứ không `JOIN`. Nếu sự kiện hoặc loại vé đã bị xoá mềm, query filter loại nó ra: `JOIN` sẽ làm **mất cả đơn**, còn truy vấn con chỉ cho tên `null` và đơn vẫn hiện.

### 8. Test

- **Unit:** handler huỷ và hết hạn mock `IUnitOfWork.ExecuteInTransactionAsync` để chạy thẳng delegate, rồi kiểm nhánh: không phải chủ đơn, UPDATE đổi 0 dòng (không được cộng vé), cộng vé lỗi.
- **Integration:**
  - `Cancel_ConcurrentRequests_...` bắn hai request huỷ cùng lúc bằng `Task.WhenAll`: đúng một 204, một 409, kho chỉ cộng một lần.
  - Test hết hạn đẩy `expires_at` về quá khứ bằng SQL thay vì thay `TimeProvider` của cả app. Đồng hồ giả dùng chung sẽ ảnh hưởng cả JWT và phiên đăng nhập của các test khác.
- **Bẫy:** `HttpStatusCode` sắp theo số (204 < 409). Assert danh sách mã trạng thái sau `OrderBy` phải viết `[NoContent, Conflict]`.

---

## Ticket 3: Chống bán vượt

### 1. Chứng minh lỗi trước

Test `PlaceOrder_ConcurrentBuyers_NeverOversell`: một loại vé có 10 vé, 50 user khác nhau cùng lúc mỗi người đặt 1 vé (`Task.WhenAll`). Test so một lúc bốn số: đơn tạo được, đơn bị từ chối, vé thật sự đã bán (đếm thẳng `order_items`, không tin cột `available_quantity`), vé còn lại.

Trên code Ticket 2, test đỏ cả 3 lần chạy: **50/50 đơn được tạo, 50 vé bán ra, kho vẫn còn 8–9**.

- Hầu hết request đọc được `available = 10` trước khi ai kịp lưu, cùng trừ thành 9 rồi ghi đè lên nhau. Bán 50 mà kho chỉ giảm 1–2.
- `CHECK (available_quantity >= 0)` không chặn được: không request nào ghi số âm. Ràng buộc chỉ bắt được lỗi trừ quá tay, không bắt được ghi đè.

### 2. Ba cách sửa và số liệu

Mỗi cách một nhánh thử nghiệm, giữ lại để đối chiếu (không merge): `ticket-3a-pessimistic`, `ticket-3b-optimistic`, `ticket-3c-atomic`.

| Cách | Làm thế nào | Kết quả (10 vé, 50 người, 5 lần chạy) | Thời gian 50 request |
|---|---|---|---|
| Chưa sửa | Đọc → trừ trong bộ nhớ → lưu | **Sai:** 50 đơn, kho còn 8–9 | 680–780 ms |
| **A. Khoá bi quan** | Cả luồng trong một transaction; `SELECT ... FOR UPDATE` loại vé, sắp theo `id` | Đúng: 10 đơn, 40 `SoldOut` | 630–780 ms |
| **B. Khoá lạc quan** | `xmin` làm concurrency token; xung đột thì chạy lại cả transaction, tối đa 10 lần, hết lượt trả 409 `Orders.Busy` | Đúng: 10 đơn, 40 `SoldOut`, nhưng **257–310 lần thử lại** | 1.070–1.200 ms |
| B, giới hạn 3 lần | Như trên | **Bán thiếu:** chỉ 4–6 đơn, kho còn 4–6, 44–46 người nhận `Orders.Busy` dù còn vé | 800–940 ms |
| **C. UPDATE nguyên tử** | `UPDATE ... SET available = available - @q WHERE id = @id AND available >= @q`, 0 dòng = hết vé | Đúng: 10 đơn, 40 `SoldOut`, 0 lần thử lại | 570–800 ms |

**Cách đọc số liệu:**
- Cả 50 request chạy qua `TestServer` trong cùng một process, nên phần lớn thời gian là chi phí HTTP và tạo đơn. Vì vậy A và C chênh nhau không rõ ở quy mô này. Khác biệt thật về thời gian chờ khoá sẽ đo bằng k6 ở Ticket 7.
- Con số nói lên nhiều nhất là **số lần thử lại của B**. Mỗi vé bán ra kéo theo khoảng 27 lần làm lại cả transaction: đọc, kiểm, ghi, rồi bị từ chối. Tranh chấp càng cao thì số lần thử lại càng tăng.
- Giới hạn số lần thử lại của B là một lựa chọn khó. Giới hạn thấp thì từ chối oan người mua khi vẫn còn vé (bán thiếu, cũng là lỗi nghiệp vụ). Giới hạn cao thì request treo lâu và tốn DB.

### 3. Chọn C

- Kiểm tra và trừ nằm trong một câu lệnh, nên không còn khoảng trống giữa đọc và ghi. Postgres khoá dòng trong lúc UPDATE, request sau chờ rồi kiểm lại `WHERE` trên số mới (`READ COMMITTED`).
- Không đọc dòng lên C# để trừ, không thử lại, khoá giữ ngắn nhất. Cùng kiểu với huỷ đơn và hết hạn đơn ở Ticket 2, nên code nhất quán.
- **Đơn nhiều loại vé:** mỗi dòng một UPDATE, tất cả trong một transaction (`ExecuteInTransactionAsync`). Một dòng hết vé thì trả `SoldOut` từ trong transaction, UnitOfWork rollback cả các dòng đã trừ. Các dòng được trừ **theo thứ tự `TicketTypeId`** để hai đơn cùng chứa VIP và Thường luôn khoá theo một thứ tự, không khoá chéo nhau (deadlock).
- `orderRepository.Add(order)` nằm **trong** delegate của transaction: execution strategy chạy lại thì tracker bị xoá, đơn phải được thêm lại.
- **Đánh đổi:** luật "không trừ quá số còn" chuyển từ `TicketType.Reserve` xuống SQL, nên `Reserve` và test của nó bị xoá (giống `Cancel`, `Expire` ở Ticket 2). Luật được chứng minh bằng integration test trên Postgres thật.

### 4. Khi nào dùng A hoặc B

| Cách | Hợp khi |
|---|---|
| A. Khoá bi quan | Phải đọc dữ liệu lên, chạy logic phức tạp trong C# rồi mới ghi, và logic đó không viết gọn được thành một câu SQL. Khoá nhiều dòng thì luôn khoá theo một thứ tự cố định. |
| B. Khoá lạc quan | Ít tranh chấp, ví dụ admin sửa thông tin, và muốn giữ luật trong entity. Xung đột hiếm nên thử lại rẻ. |

### 5. Việc còn để ngỏ

- **Sửa loại vé khi đang bán:** đã làm bằng `xmin`, xem phần "Sửa loại vé khi đang bán" bên dưới.
- Migration cho `xmin` sinh ra `AddColumn("xmin")` nhưng Npgsql bỏ qua cột hệ thống khi tạo SQL, nên không có cột mới nào được tạo. Module Auth cũng dựa vào hành vi này.
- Xem số liệu của test: `dotnet test tests/TicketHub.IntegrationTests --filter-method "*NeverOversell" --output Detailed`, rồi tìm dòng `[oversell]`.

---

## Sửa loại vé khi đang bán

`PUT /api/v1/events/{id}/ticket-types/{ticketTypeId}` cho admin sửa tên, mô tả, giá, tổng số vé, số tối đa mỗi đơn, **kể cả khi sự kiện đang `Published`**. Ví dụ VIP bán chạy, admin tăng tổng từ 200 lên 300.

### 1. Gửi giá trị mới, không gửi phần thêm

- Body mang giá trị tuyệt đối: `totalQuantity = 300`, không phải `+100`. Số vé còn được tính lại: `available = tổng mới − đã bán`.
- Luật trong `TicketType.Update`: tổng mới không được nhỏ hơn số đã bán (`TotalBelowSold`). Đổi giá chỉ áp cho đơn mới, đơn cũ giữ `unit_price` đã chụp.
- Sự kiện `Cancelled` thì không sửa được (`TicketTypes.EventCancelled`). Loại vé không thuộc sự kiện trên URL thì **404**, giống loại vé không tồn tại.
- **Vì sao gửi giá trị mới chứ không gửi phần thêm:** tổng số vé thường bị chốt bởi sức chứa thật của địa điểm, nên ý định của admin là "tổng phải bằng 300", không phải "cộng thêm 100". Hai admin cùng muốn nâng lên 300 thì `PUT` cho ra 300 (đúng), còn kiểu "thêm 100" sẽ ra 400 và bán vượt sức chứa. Chỉ khi mỗi admin được giao mở thêm một phần riêng thì mới cần endpoint "thêm N vé" (`SET total = total + N`).
- **Giới hạn:** `xmin` chỉ bảo vệ khoảng đọc → lưu trong handler, không bảo vệ lúc admin đang nhìn form với số cũ (mở form lúc 200, người khác đã sửa thành 250, admin vẫn gõ 300). Nếu cần chặn trường hợp đó thì gửi kèm phiên bản admin đã thấy (header `If-Match`), để sau.

### 2. Vì sao cần `xmin`

Handler đọc loại vé → `Update` tính lại `AvailableQuantity` trong bộ nhớ → lưu. Nếu có người mua chen vào giữa lúc đọc và lúc lưu, bản admin đang giữ vẫn là số vé cũ, lưu xuống sẽ **ghi đè mất vé vừa bị trừ** (lost update, cùng loại lỗi với Ticket 3).

- `xmin` là cột hệ thống của Postgres, chứa mã transaction đã ghi dòng gần nhất. Mọi câu ghi vào dòng đều đổi nó, **kể cả các `ExecuteUpdate` của đặt vé, huỷ, hết hạn** vốn không đi qua EF tracker. Vì vậy không tự thêm cột `version`: phải nhớ tăng nó ở từng câu SQL, quên một chỗ là mất tác dụng.
- EF nhớ `xmin` lúc đọc, lúc lưu thêm `AND xmin = @cũ` vào `WHERE`. Khớp 0 dòng thì ném `DbUpdateConcurrencyException`, `AppDbContextBase` đổi thành `ConcurrencyConflictException`.
- Cấu hình: `builder.Property<uint>("Version").IsRowVersion()`. Migration `AddTicketTypeVersion` có `AddColumn("xmin")` nhưng Npgsql bỏ qua cột hệ thống khi sinh SQL, nên không có cột mới.

### 3. Xung đột thì tự thử lại 3 lần, rồi mới trả 409

- Ý của admin là giá trị tuyệt đối, nên đọc lại số mới rồi áp lại là đúng ý họ. Mỗi lần thử đều kiểm lại `TotalBelowSold` trên số đã bán mới nhất.
- Handler gọi `ExecuteInTransactionAsync`: khi có exception, UnitOfWork xoá tracker, nhờ vậy lần sau đọc được bản mới thay vì bản cũ đã track.
- Hết 3 lần thì trả 409 `TicketTypes.ConcurrentUpdate` để admin bấm lưu lại.
- **Số liệu:** trong test đua (50 người mua cùng lúc admin lưu), 2 trên 3 lần chạy handler hết 3 lượt và admin phải bấm lại 1 lần. Trong lúc bán rất chạy, 3 lần là hơi ít. Có thể tăng, hoặc chuyển phần đổi tổng sang UPDATE nguyên tử (không bao giờ xung đột).

### 4. Chứng minh

| Test | Có `xmin` | Tạm tắt `xmin` |
|---|---|---|
| `SaveAfterConcurrentPurchase_...`: admin đọc → 1 người mua → admin lưu | Ném `ConcurrencyConflictException`, kho giữ đúng 9 | Lưu thành công, **ghi đè** vé vừa bán |
| `RaiseTotalWhileBuyersRace_...`: 50 người mua cùng lúc admin tăng 10 → 20, sau đó 20 người nữa | Bán đúng 20, kho 0 | **Bán 22 vé cho tổng 20** (cả 2 lần chạy) |

Khi tắt `xmin` để thử phải tạm bỏ qua cảnh báo `PendingModelChangesWarning` (model lệch snapshot), nếu không `Migrate` lúc khởi động sẽ báo lỗi.

### 5. Kiểm tên trùng sau khi kiểm loại vé thuộc sự kiện

Bản đầu kiểm tên trước: gọi URL của sự kiện khác (cũng có loại vé tên "VIP") thì nhận **409 trùng tên** thay vì 404. Integration test bắt được, nay kiểm tên sau bước tìm loại vé.
