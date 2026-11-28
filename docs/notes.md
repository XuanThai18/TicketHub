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

---

## Ticket 4.1: Idempotency-Key cho đặt vé

### 1. Bài toán và cách client dùng

Client bấm "Đặt vé", mạng chập chờn không nhận được phản hồi, app tự gửi lại. Không có chống gửi lặp thì người dùng có **nhiều đơn và bị trừ vé nhiều lần**.

- Client tự sinh một key (thường là UUID) cho **mỗi lần mua**, gửi trong header `Idempotency-Key`, và gửi lại **đúng key đó** khi thử lại.
- Header không bắt buộc: không gửi thì như trước, luôn tạo đơn mới.
- Key có phạm vi trong từng user (unique index `(user_id, idempotency_key)`): hai user tình cờ trùng key vẫn là hai đơn.

**Chứng minh lỗi trước khi sửa:** 20 request cùng key bắn cùng lúc tạo ra **5 đơn** (5 × 2 vé là hết 10 vé), các request còn lại nhận `SoldOut`.

### 2. Hành vi

| Tình huống | Kết quả |
|---|---|
| Key mới | Tạo đơn, lưu key trên đơn, **201** |
| Cùng key, cùng nội dung | **201** y hệt lần đầu (cùng `id`, cùng `Location`) + header `Idempotent-Replayed: true`; không trừ vé, không tạo đơn |
| Cùng key, khác nội dung | **422** `Orders.IdempotencyKeyReused` |
| Nhiều request cùng key cùng lúc | Đúng **một** đơn; các request còn lại nhận replay, không ai lỗi |
| Lần đầu thất bại (hết vé, chưa mở bán...) | Không lưu gì; gửi lại cùng key thì chạy lại từ đầu |
| Header rỗng, toàn khoảng trắng, hoặc > 100 ký tự | **400** |

- **Vì sao 201 khi replay:** client chỉ cần một nhánh xử lý thành công; header `Idempotent-Replayed` cho biết đây là đơn cũ.
- **Vì sao 422 khi khác nội dung:** theo bản nháp IETF "Idempotency-Key HTTP Header Field". Thêm `ErrorType.Unprocessable` → 422 trong `ToProblem`.
- **So nội dung bằng các dòng đơn** (`Order.HasSameLines`): so tập `(ticketTypeId, quantity)` với `order_items`, không tính thứ tự. Không cần cột lưu hash request.
- **Key giữ vĩnh viễn** vì nằm trên chính đơn hàng: gửi lại key của đơn đã hết hạn/huỷ thì nhận lại đơn đó với trạng thái hiện tại. Không cần job dọn key.
- Lần đầu thất bại thì **không lưu** kết quả lỗi (khác Stripe, vốn lưu cả lỗi). Lý do: không có đơn để gắn key, và thử lại sau khi có người trả vé là hợp lý với người mua.

### 3. Hai đường xử lý

```
0. Có key → tìm đơn theo (user_id, key) → có thì replay/422 ngay          ← gửi lại sau vài giây
1–4. Luồng đặt vé như Ticket 3 (UPDATE trừ vé + INSERT đơn có key, 1 transaction)
5. INSERT vấp ix_orders_user_id_idempotency_key → tìm lại đơn → replay/422  ← nhiều request cùng lúc
```

- **Bước 0 đặt trước mọi kiểm tra khác:** lần gửi lại vẫn nhận đơn cũ dù lúc đó sự kiện đã hết giờ bán.
- **Bước 5 không cần khoá riêng.** Hai request cùng qua bước 0 (chưa ai commit). Request đến sau INSERT cùng `(user_id, key)` thì Postgres bắt nó **chờ** request trước commit rồi báo `23505 unique_violation`. Transaction của nó rollback nên **vé vừa trừ tự trả lại**. `AppDbContextBase` đổi lỗi thành `UniqueConstraintException`, handler đọc đơn của request thắng và trả replay.
- Handler chỉ coi là gửi lặp khi `ConstraintName` đúng là `ix_orders_user_id_idempotency_key` (hằng `PlaceOrderCommandHandler.IdempotencyKeyIndex`). Trùng unique index khác thì để exception đi lên.

### 4. Bẫy: header rỗng bị coi như không gửi

Giá trị header bị cắt khoảng trắng hai đầu, và model binding đổi chuỗi rỗng thành `null`. Kết quả: gửi `Idempotency-Key: "   "` trông y như không gửi header, và đơn được tạo **không có chống gửi lặp** mà client không biết. Controller kiểm `Request.Headers.ContainsKey(...)` để giữ lại chuỗi rỗng, validator trả 400. Integration test `PlaceOrder_InvalidKey_Returns400` bắt được lỗi này.

---

## Ticket 4.2: Thanh toán và webhook

### 1. Luồng

```
User ──POST /orders/{id}/pay──▶ API: tạo payment Pending, trả checkoutUrl
User ──mở checkoutUrl──▶ Cổng thanh toán ──khách trả tiền──
Cổng ──POST /payments/webhook (ký HMAC)──▶ API: kiểm chữ ký → đơn Paid
```

- **Kết quả thanh toán đến từ webhook do cổng gọi, không đến từ client.** Tin client báo "tôi trả rồi" thì ai cũng giả được.
- Số tiền của lượt thanh toán lấy từ `order.total_amount` ở server, không bao giờ nhận từ client.
- **Cổng giả lập** (`POST /api/v1/mock-gateway/checkout/{tx}/complete?outcome=succeeded|failed`, chỉ có ở Development và Testing): dựng webhook, ký bằng khoá chung, rồi đưa vào **đúng handler webhook thật**, nên vẫn đi qua bước kiểm chữ ký. Môi trường khác trả 404.

### 2. Mở lượt thanh toán: mỗi đơn tối đa một lượt đang chờ

- Unique index một phần `ix_payments_order_id_pending` trên `(order_id) WHERE status = 'Pending'`. Bấm "Thanh toán" lần nữa khi lượt trước còn chờ thì nhận lại lượt đó (200); bấm 10 lần cùng lúc thì index chặn, request thua đọc lại lượt của request thắng. Cùng kiểu với Idempotency-Key ở 4.1.
- Lượt trước `Failed` không bị index chặn (chỉ áp cho `Pending`), nên thanh toán lại được.
- Đơn không còn `Pending` hoặc **đã quá `expires_at`** (dù job chưa chạy) → 409 `Orders.NotPayable`.
- **Tên index định nghĩa một chỗ:** `PaymentConfiguration` gọi `.HasDatabaseName(PayOrderCommandHandler.PendingPaymentIndex)`, handler bắt lỗi theo đúng hằng đó. Khác 4.1 (tên index do EF tự đặt, phải dựa vào test để giữ khớp).

### 3. Chữ ký webhook

- `X-Signature: sha256=hex(HMAC-SHA256(khoá, "{timestamp}.{body}"))`, `X-Signature-Timestamp` là giây Unix. Cùng kiểu với Stripe.
- **Ký trên nguyên văn body:** controller đọc body thô thay vì bind sang object. Parse rồi serialize lại có thể đổi thứ tự trường hay khoảng trắng và chữ ký sẽ sai.
- **So bằng `CryptographicOperations.FixedTimeEquals`:** so từng byte rồi dừng ở byte sai đầu tiên thì thời gian phản hồi lộ ra số byte đúng, đo nhiều lần là đoán dần được chữ ký.
- **Timestamp nằm trong phần được ký và phải lệch ≤ 5 phút:** chặn kẻ bắt được một webhook cũ rồi gửi lại. Đổi timestamp để "làm mới" thì chữ ký không khớp nữa.
- Khoá ở `Payments:WebhookSecret` (≥ 32 ký tự, `ValidateOnStart`): thiếu khoá thì app không khởi động, thay vì chạy rồi từ chối mọi webhook. Production đặt qua biến môi trường `Payments__WebhookSecret`.
- Sai chữ ký hoặc quá hạn → 401; không biết mã giao dịch → 404; nhận hợp lệ (kể cả bản lặp) → **200**, để cổng ngừng gửi lại.

### 4. Xử lý webhook: ba UPDATE có điều kiện trong một transaction

```
a) UPDATE payments SET status = @kếtquả, raw_payload, completed_at WHERE id = @id AND status = 'Pending'
   → 0 dòng: webhook này đã xử lý rồi → 200, không làm gì thêm
b) (nếu thành công) UPDATE orders SET status = 'Paid', paid_at = now WHERE id = @orderId AND status = 'Pending'
c) b) đổi 0 dòng (đơn đã Expired/Cancelled) → UPDATE payments SET status = 'RefundRequired'
```

- **Phải chốt lượt thanh toán (a) trước khi đụng tới đơn.** Nếu chỉ dựa vào "đơn còn Pending", cổng gửi lặp thì lần thứ hai thấy đơn đã `Paid` (vì chính lần một vừa đổi) và đánh dấu nhầm `RefundRequired`: khách vừa có vé vừa được hoàn tiền. Đã chứng minh: tạm bỏ điều kiện `status = 'Pending'` ở (a), test gửi lặp 10 lần đỏ với payment `RefundRequired` trong khi đơn `Paid`.
- **Đua với job hết hạn:** cả hai cùng `UPDATE orders ... WHERE status = 'Pending'`, chỉ một bên thắng.
  - Webhook thắng (kể cả khi đã quá `expires_at` vài giây nhưng job chưa chạy): vé vẫn đang giữ cho đơn → nhận tiền, đơn `Paid`, job bỏ qua.
  - Job thắng: vé đã về kho → payment `RefundRequired`, không xuất vé.
- **Huỷ đơn sau khi đã mở thanh toán:** tiền về sau đó cũng rơi vào (c) → `RefundRequired`.
- `ck_orders_paid_at` đòi `(status = 'Paid') = (paid_at IS NOT NULL)` nên (b) đặt cả hai cột trong cùng câu. `ck_payments_completed_at` tương tự cho payment.

### 5. Giới hạn đã biết

- `RefundRequired` mới chỉ là đánh dấu; chưa có màn hình hay job hoàn tiền thật.
- Chưa so `amount` trong webhook với `payments.amount` (cổng giả lập không gửi số tiền). Cổng thật thì phải so, lệch thì không chuyển `Paid`.
- `ExecuteUpdate` bỏ qua interceptor audit nên `updated_at/updated_by` của payment và đơn không được điền (giống huỷ/hết hạn ở Ticket 2).

---

## Ticket 4.3: Sinh vé

### 1. Sinh vé trong cùng transaction với bước đơn → Paid

```
webhook thành công
  a) chốt payment (Pending → Succeeded)
  b) TryMarkPaidAsync(đơn) → true
     → đọc đơn kèm order_items → mỗi chỗ một Ticket.Issue → SaveChanges   ← cùng transaction
```

- **Không bao giờ có đơn Paid mà thiếu vé** (hay vé mà đơn chưa Paid): commit thì có cả hai, lỗi thì rollback cả hai.
- **Webhook gửi lặp không sinh vé lần hai:** vé chỉ được phát khi chính lời gọi này vừa đổi được đơn sang `Paid` (bước b trả `true`). Lần lặp dừng ngay ở bước (a). Integration test bắn 10 webhook cùng lúc cho đơn 1 chỗ → đúng 1 vé.
- **Tiền về khi đơn đã hết hạn:** bước (b) trả `false` → `RefundRequired`, không phát vé (vé đã về kho và có thể đã bán cho người khác).
- Đơn 2 VIP + 1 Thường → 3 dòng `tickets`, mỗi dòng gắn `ticket_type_id` riêng.

### 2. Mã vé

- 16 ký tự lấy từ bảng 31 ký tự `A–Z, 2–9` **bỏ 0/O, 1/I/L** (dễ đọc nhầm khi soát vé bằng tay) → khoảng 79 bit ngẫu nhiên.
- Sinh bằng `RandomNumberGenerator` (ngẫu nhiên mật mã), không dùng `Random`: mã vé là thứ người khác không được đoán ra để vào cửa.
- Unique index `ix_tickets_code` là lưới an toàn cuối. Với 79 bit, trùng gần như không thể; nếu có, `SaveChanges` lỗi → transaction rollback → webhook trả 500 → cổng gửi lại → lần sau sinh mã mới (payment vẫn `Pending` vì bước a cũng rollback).

### 3. Xem vé

- `GET /api/v1/tickets/my`: vé của người đang đăng nhập (join qua `orders.user_id`), phân trang, mới phát trước, kèm tên sự kiện, giờ diễn, tên loại vé.
- `GET /api/v1/orders/{id}`: thêm danh sách `tickets` của đơn (rỗng khi chưa thanh toán).

### 4. Chưa làm

- Soát vé (`Valid → Used`) và huỷ vé (`Cancelled`) khi huỷ sự kiện/hoàn tiền.
- Hoàn tiền thật cho `RefundRequired`: chưa làm (mới chỉ đánh dấu).
- ~~Gửi vé qua email~~: đã làm ở Ticket 5.

---

## Ticket 5: Email vé qua Outbox + RabbitMQ

### 1. Bài toán: ghi DB và gửi tin là hai việc tách rời (dual write)

Đơn chuyển `Paid` thì phải gửi email vé. Cách ngây thơ là commit DB rồi gửi tin lên RabbitMQ ngay trong handler webhook:

| Thứ tự | Sự cố | Hậu quả |
|---|---|---|
| Commit rồi gửi tin | RabbitMQ chết, hoặc app sập ngay sau commit | Đơn `Paid`, vé có, **email không bao giờ đi** |
| Gửi tin rồi commit | Commit lỗi (rollback) | Email báo vé cho một đơn **không hề `Paid`** |

Không có transaction nào bao trùm cả Postgres lẫn RabbitMQ, nên đổi thứ tự cũng không cứu được.

### 2. Outbox: ghi tin vào DB cùng transaction với đơn `Paid`

```
webhook thành công (một transaction)
  a) chốt payment         b) đơn → Paid
  c) phát vé              d) INSERT outbox_messages (OrderPaid { orderId, userId })   ← cùng commit
                                   │
OutboxProcessor (2 giây/lần) ──────┘ đọc tin chưa gửi → RabbitMQ → đánh dấu processed_at
```

- Commit thì có cả đơn `Paid` lẫn tin; rollback thì mất cả hai. Gửi lên RabbitMQ là việc **sau**, của job nền: RabbitMQ chết thì tin chỉ **nằm chờ** trong DB.
- Tin chỉ chứa `orderId`, `userId`. Email, tên sự kiện, mã vé được tra **lúc gửi**: outbox và queue không giữ dữ liệu cá nhân, người mua đổi email trước khi thư đi thì vẫn tới địa chỉ mới.
- `MessageId` của tin RabbitMQ = `id` dòng outbox (UUID v7), không đổi dù gửi lại bao nhiêu lần. Bên nhận dựa vào nó để chống trùng (mục 6).

### 3. `OutboxProcessor`: quét định kỳ, `FOR UPDATE SKIP LOCKED`

```sql
SELECT * FROM outbox_messages WHERE processed_at IS NULL
ORDER BY occurred_at LIMIT 50 FOR UPDATE SKIP LOCKED
```

- Chạy trong transaction: khoá các dòng vừa lấy tới khi commit. `SKIP LOCKED` để khi chạy nhiều instance (scale ngang, rolling deploy) mỗi bên lấy một phần khác nhau, thay vì gửi trùng hoặc đứng chờ nhau.
- Index một phần `ix_outbox_messages_unprocessed` trên `occurred_at WHERE processed_at IS NULL`: tin đã gửi (đa số) không nằm trong index nên index luôn nhỏ.
- Gửi lỗi → `attempts + 1`, ghi `last_error`, **dừng lô**: broker hỏng thì tin sau cũng hỏng; dừng lại để giữ thứ tự, lượt sau thử tiếp.
- Gửi xong nhưng chưa kịp commit `processed_at` (app sập) → lượt sau gửi lại. Outbox đảm bảo **ít nhất một lần**, không phải đúng một lần; phần trùng do bên nhận lo.

### 4. RabbitMQ

```
tickethub.events (direct) ──"OrderPaid"──▶ ticket-emails ──(hỏng hẳn / hết lượt)──▶ tickethub.dead-letter ──▶ ticket-emails.dead-letter
                                              ▲     │
                                (hết hạn chờ) │     │ (lỗi tạm thời)
                                              └── ticket-emails.retry
```

- **Dùng thẳng `RabbitMQ.Client`, không dùng MassTransit:** mục tiêu là hiểu và trình bày được từng mảnh (confirm, ack, dead-letter, retry). MassTransit làm hết những việc này nhưng che đi chính phần đáng học.
- `RabbitMqTopology.DeclareAsync` khai báo exchange/queue ở **một chỗ**, cả bên gửi lẫn bên nhận đều gọi (khai báo là idempotent). Tham số queue (`x-dead-letter-*`) không sửa được sau khi tạo (`PRECONDITION_FAILED`), nên khai báo đủ ngay từ đầu.
- Bên gửi bật **publisher confirms** (chỉ đánh dấu `processed_at` khi broker xác nhận đã nhận), `mandatory: true` (không queue nào nhận thì báo lỗi thay vì vứt tin), `DeliveryMode = Persistent` (RabbitMQ khởi động lại không mất tin). Một kết nối + một channel dùng lại, khoá bằng `SemaphoreSlim` vì `IChannel` không an toàn đa luồng; lỗi thì bỏ kết nối, lần sau mở lại.
- Consumer chạy **cùng process với API** (hosted service) cho đơn giản. Muốn tách thành worker riêng thì chỉ cần đăng ký `TicketEmailConsumer` ở host khác.

### 5. Consumer: ack sau khi gửi thư

- `autoAck: false`, `prefetch 10`. Gửi thư xong mới ack: app sập giữa chừng thì tin chưa ack, RabbitMQ giao lại.
- Soạn thư (`GetTicketEmailQuery`, tầng Application) tách khỏi gửi thư (`IEmailSender`, MailKit + SMTP): query không có tác dụng phụ, mẫu thư (`TicketEmailTemplate.Build`) là hàm thuần nên unit test được; đổi SMTP sang dịch vụ khác không đụng tới handler.
- Giờ diễn in theo giờ Việt Nam; tên sự kiện do người dùng nhập nên được `HtmlEncode` trước khi chèn vào bản HTML.
- Dev/test dùng **Mailpit** (hộp thư giả, SMTP 1025, giao diện 8025).

### 6. Chống gửi trùng: bảng `processed_messages`

| Bước | Việc |
|---|---|
| 1 | Đã có dòng `(message_id, consumer)` → ack, bỏ qua |
| 2 | Soạn thư, gửi |
| 3 | `INSERT processed_messages` → ack |

- **Ghi dấu SAU khi gửi**: sập giữa bước 2 và 3 thì lần sau gửi lại, người mua nhận **thừa một thư**. Ghi dấu trước thì sập giữa chừng là **mất thư**. Thừa vẫn hơn mất. Không thể "đúng một lần" thật sự vì SMTP không nằm chung transaction với DB.
- Khoá chính `(message_id, consumer)`: cùng một tin sau này có thể tới nhiều consumer (email, thông báo...), mỗi bên đánh dấu phần của mình.

### 7. Thử lại và dead-letter

| Lỗi | Ví dụ | Xử lý |
|---|---|---|
| Hỏng hẳn | Thiếu/sai `MessageId`, JSON hỏng, đơn không tồn tại, người mua đã bị xoá | Nack → dead-letter **ngay**, thử lại cũng vô ích |
| Tạm thời | Exception từ SMTP hoặc DB | Hẹn giờ thử lại; quá `MaxRetries` → dead-letter |

- Thử lại = gửi **bản sao** sang `ticket-emails.retry` (header `x-retry-count + 1`, `Expiration` = `RetryDelay`) rồi ack bản gốc. Queue retry không ai đọc; hết hạn thì RabbitMQ dead-letter tin qua exchange mặc định về lại `ticket-emails`.
- **Không dùng `Nack(requeue: true)`**: tin quay lại ngay lập tức, SMTP đang chết thì thành vòng lặp nóng, không có thời gian chờ và không đếm được số lần.
- **Thời gian chờ cố định** (mặc định 30s × 5 lần), không tăng dần: RabbitMQ chỉ cho tin hết hạn khi nó ở **đầu** queue, nên tin chờ lâu đứng đầu sẽ giữ chân các tin chờ ngắn phía sau. Muốn chờ tăng dần thì cần nhiều queue retry (mỗi mức một queue) hoặc plugin delayed-message.
- Bản sao được gửi qua channel có publisher confirms, broker xác nhận xong mới ack bản gốc. Sập giữa hai bước thì có hai bản; bản tới sau thấy dấu ở mục 6 và bỏ qua.

### 8. Chứng minh

| Test | Kiểm |
|---|---|
| `HandlePaymentWebhookTests` (unit) | Webhook thành công ghi tin `OrderPaid` vào outbox; webhook lặp và `RefundRequired` thì không |
| `OutboxTests.OrderPaid_MessageIsMarkedProcessedAfterPublishing` | Tin được gửi lên RabbitMQ thật và đánh dấu `processed_at` |
| `OutboxTests.BrokerDown_...` | Broker "chết": đơn vẫn `Paid`, tin nằm lại outbox, `attempts` tăng; broker sống lại thì tự gửi nốt |
| `TicketEmailTests.OrderPaid_BuyerReceivesOneEmailWithEveryTicketCode` | Thanh toán → thư tới Mailpit, đủ 3 mã vé, đúng một thư |
| `TicketEmailTests.SameMessageDeliveredTwice_...` | Đẩy thêm một bản cùng `MessageId`: không gửi thư lần hai. Tắt bước kiểm dấu → test đỏ (5 lần gửi thay vì 2) |
| `TicketEmailTests.SmtpFailsTwice_...` | SMTP lỗi 2 lần → thử lại, cuối cùng đúng một thư |
| `TicketEmailTests.SmtpKeepsFailing_...` | SMTP lỗi mãi → sau `MaxRetries` lần, tin nằm ở dead-letter với `x-retry-count` đúng |

- "Broker chết" trong test là một lớp bọc publisher ném lỗi (`ControllablePublisher`), không tắt container thật: bật lại container thì Testcontainers thường cấp cổng mới và app không tìm thấy nữa. Thử với RabbitMQ thật: README, mục *Thử email vé*.
- **Đã chạy tay với RabbitMQ thật** (`docker compose stop rabbitmq`): đơn vẫn `Paid`, outbox ghi `attempts = 1` với lỗi *None of the specified endpoints were reachable*, Mailpit 0 thư. `docker compose start rabbitmq` → khoảng 8 giây sau thư tới, `processed_at` có giá trị (4 lần thử). Consumer tự nối lại nhờ automatic recovery của RabbitMQ.Client, không phải khởi động lại app.
- Lỗ bắt được trong chính test: bản đầu của test chống trùng chờ "đủ 2 thư" nên vẫn xanh khi tắt chống trùng (thư thứ hai chính là thư lặp). Đã sửa thành chờ đúng thư của đơn thứ hai theo mã đơn.

### 9. Giới hạn đã biết

- **Ít nhất một lần**, không phải đúng một lần (mục 6): hiếm, nhưng vẫn có thể thừa một thư.
- `outbox_messages` và `processed_messages` chưa có job dọn nên bảng phình dần. Cần job xoá dòng đã xử lý quá N ngày.
- Outbox không giới hạn số lần thử: một tin gửi mãi không được sẽ chặn các tin sau nó (vì dừng lô ở lỗi đầu). Lỗi gửi gần như luôn là lỗi broker nên chặn cả lô là đúng, nhưng chưa có cảnh báo khi `attempts` cao.
- Dead-letter chưa có công cụ xem/gửi lại; hiện phải làm tay qua RabbitMQ Management UI.
- Độ trễ email: tới 2 giây chờ lượt quét outbox, cộng thời gian gửi SMTP.
- Mẫu thư đơn giản (chữ + HTML), chưa có mã QR.

---

## Ticket 6: Cache Redis

### 1. Cache cái gì, không cache cái gì

| Dữ liệu | Cache? | Vì sao |
|---|---|---|
| Chi tiết sự kiện công khai (tên, giờ, loại vé, giá, giới hạn mua) | Có, 60 giây | Đọc nhiều, ít đổi; lệch vài chục giây không gây hại |
| Danh sách sự kiện công khai, theo từng bộ tham số | Có, 60 giây, theo phiên bản | Như trên |
| Số vé còn (`AvailableQuantity`) | **Không** | Đổi theo từng đơn. Cache sẽ hiện "còn vé" khi đã hết, hoặc ngược lại. Đây là chỗ bán vượt đã sửa ở Ticket 3, không đưa lại vào cache. Phần này vẫn là một `SELECT` nhẹ theo `EventId` mỗi lần đọc |
| Bản nháp | Không | Chỉ Admin và Manager được xem. Nếu đưa vào cache công khai thì người ẩn danh thấy được |
| Đơn hàng, vé, thanh toán | Không | Dữ liệu riêng của từng người, đổi theo thao tác. Không đáng cache |

### 2. Cache-aside qua `ICacheService`

- `GetOrCreateAsync(key, factory, ttl)`: đọc Redis trước. Có thì trả về. Không có thì chạy `factory` (truy vấn DB), lưu lại rồi trả về.
- **Giá trị `null` không được lưu.** Sự kiện không tồn tại hoặc là bản nháp đều trả 404 và không vào cache. Nếu lưu `null`, sự kiện vừa được publish sẽ bị 404 cho tới khi hết TTL.
- **Lỗi Redis chỉ ghi log, coi như cache miss.** Lỗi khi đọc thì gọi DB, lỗi khi ghi thì bỏ qua. `OperationCanceledException` được ném tiếp, không bị nuốt.
- Lưu dạng JSON (`JsonSerializerOptions.Web`). Khoá có tiền tố `TicketHub_` (`InstanceName`) để nhiều ứng dụng dùng chung một Redis không đụng khoá của nhau.

### 3. Vì sao không dùng HybridCache

- HybridCache (.NET 9 trở lên) có lớp L1 trong bộ nhớ và chống "cache stampede". Chưa dùng vì app chạy nhiều pod. L1 nằm trong từng pod, nên xoá khoá trong Redis không xoá L1 của pod khác. Pod đó sẽ giữ bản cũ tới hết TTL. Muốn đúng thì phải thêm backplane, phức tạp hơn lợi ích khi TTL chỉ 60 giây.
- Chống stampede chưa làm: khi khoá hết hạn, nhiều request cùng miss rồi cùng truy vấn DB. Mức này chấp nhận được với tải hiện tại. Sẽ kiểm lại ở Ticket 7.

### 4. Khoá chi tiết: xoá khi nào

- Khoá: `event:{id}:public`.
- **Xoá** khi `PublishEvent`, `CancelEvent` (đổi trạng thái hiển thị) và `UpdateTicketType` (đổi giá, giới hạn mua, tên, mô tả).
- **Không xoá** khi `UpdateEvent` và `AddTicketType`: cả hai chỉ chạy được khi còn là bản nháp, mà bản nháp không bao giờ nằm trong cache công khai.
- Xoá **sau khi ghi thành công**, không trước. Nếu xoá trước, một request đọc chen vào có thể lấy dữ liệu cũ từ DB, lưu lại vào cache, rồi transaction mới commit. Khi đó cache giữ bản cũ tới hết TTL.

### 5. Khoá danh sách: phiên bản thay vì xoá từng trang

- Khoá phiên bản `events:list:version` chứa một GUID. Mỗi trang lưu dưới khoá `events:list:{GUID}:s={hash}:p=..:ps=..:f=..:t=..`.
- Xoá khoá phiên bản làm mọi trang cũ hết hiệu lực ngay lập tức, mà không cần liệt kê hay xoá từng trang. Các trang cũ không còn được tra tới, tự hết hạn sau 60 giây.
- **Vì sao GUID, không phải bộ đếm 1, 2, 3.** Nếu khoá phiên bản mất đi (hết hạn, bị xoá, Redis khởi động lại), bộ đếm có thể quay lại từ 1. Khi đó trang cũ `…:1:…` còn sót trong Redis sẽ được coi là hợp lệ. GUID ngẫu nhiên thì không lặp lại.
- **Vì sao băm từ khoá tìm kiếm.** Từ khoá là chuỗi do người dùng nhập, có thể chứa `:` và `=`, làm lệch cấu trúc khoá. Băm SHA-256 sau khi `Trim` và `ToLowerInvariant`, cùng cách chuẩn hoá với truy vấn.
- **Vì sao race với ghi không gây sai.** Người đọc lấy phiên bản cũ trước khi ghi, truy vấn DB trước commit, rồi lưu dưới khoá của phiên bản cũ sau khi đã xoá khoá phiên bản. Khoá đó không còn được tra tới, nên không ảnh hưởng ai. Chi tiết không có cơ chế này, xem giới hạn bên dưới.

### 6. Giới hạn đã biết

- **Chi tiết có thể lệch tới 60 giây khi ghi và đọc chạy đua.** Một request đọc đã lấy bản cũ từ DB trước commit, rồi lưu vào cache sau khi ta đã xoá. Chấp nhận vì TTL ngắn. Muốn triệt để thì áp dụng khoá có phiên bản như danh sách.
- **Redis chết thì mỗi request chờ timeout, không chỉ mất cache.** Đã đo (mục 9): ghi chậm từ ~25 ms lên ~5,9 giây, đọc chi tiết từ ~10 ms lên ~11 giây. Sau khi giảm timeout (mục 10): ~0,9 giây và ~2 giây. Nguyên nhân: StackExchange.Redis chờ `SyncTimeout` (mặc định 5 giây) cho mỗi lệnh; đọc miss rồi ghi lại là hai lệnh, nên ~10–12 giây. Khi Redis sống lại, mọi thứ trở về bình thường ngay.
- **Redis chết thì danh sách không bao giờ hit.** Factory tạo GUID mới mỗi request, nên khoá trang không bao giờ được tra lại. Vẫn đúng, chỉ không có lợi ích.
- **`GetValueOrDefault` trả 0 khi thiếu loại vé** trong truy vấn số vé. Hiện chỉ xảy ra khi loại vé bị xoá giữa hai truy vấn, mà chưa có chức năng xoá loại vé. Nếu xảy ra, người dùng sẽ thấy "hết vé" sai. Có thể bỏ loại vé đó khỏi kết quả, hoặc đổi thành nullable.
- Chưa có metric tỷ lệ hit/miss, nên chưa biết cache có thật sự giảm tải không. Xem Ticket 7.

### 7. Chứng minh

| Test | Kiểm |
|---|---|
| `DistributedCacheServiceTests` (unit) | Lần hai không gọi factory; `null` không lưu; `Remove` làm lần sau đọc lại từ factory; Redis chết vẫn trả giá trị từ factory |
| `CacheServiceTests` (integration, Redis thật) | Giá trị nằm trong Redis tới khi bị xoá |
| `EventDetailCacheTests` | Sửa tên trong DB bằng SQL → chi tiết công khai vẫn ra tên cũ (đã cache); số vé còn luôn mới; bản nháp ẩn với người ẩn danh |
| `ListEventsCacheTests` | Danh sách ra kết quả cũ sau khi sửa DB; hai bộ tham số khác nhau không dùng chung kết quả |
| `EventWriteInvalidationTests` | Publish hiện ngay trong danh sách; Cancel hiện ngay trong chi tiết và danh sách; sửa loại vé hiện ngay trong chi tiết |

Test "đọc cũ sau khi sửa DB" chứng minh có cache. Riêng test invalidate (`EventWriteInvalidationTests`) đã được chạy đỏ trước khi thêm xoá cache, rồi xanh sau khi thêm.

### 8. Chưa đo: tải

- **Hiệu quả cache dưới tải** và chống stampede: Ticket 7 (k6).

### 9. Đo độ trễ khi Redis chết

Đo thủ công trên máy dev: API chạy `dotnet run` (Development), hạ tầng trong Docker Desktop, `docker compose stop redis` rồi `start redis`. Mỗi dòng là 5 lần ghi (PUT loại vé) hoặc 3 lần đọc (chi tiết công khai).

| Trạng thái Redis | Ghi (PUT loại vé) | Đọc (chi tiết) |
|---|---|---|
| Sống | 16–98 ms | 5–29 ms |
| Chết | 5,6–6,0 giây | 10,9–12,0 giây |
| Sống lại | 14–35 ms | 4–9 ms |

- Redis chết, mỗi lệnh chờ hết `SyncTimeout` (mặc định 5 giây). Ghi chỉ có một lệnh `RemoveAsync` nên khoảng 6 giây. Đọc miss có hai lệnh (`GetAsync` rồi `SetAsync`), nên khoảng 11 giây.
- Đây không phải "chỉ mất cache" như mục 6 ban đầu nghĩ. Cần giảm timeout hoặc tắt tạm Redis khi đã lỗi.
- Đây là một lần đo nhỏ, không phải tải. Ticket 7 (k6) sẽ đo lại dưới tải.

### 10. Sau khi giảm timeout xuống 300 ms

`RedisConnectionOptions`: `ConnectTimeout` và `SyncTimeout` = 300 ms, `AbortOnConnectFail = false` (app vẫn khởi động khi Redis chưa lên, cache coi như miss). Đo lại cùng cách với mục 9:

| Trạng thái Redis | Ghi (PUT loại vé) | Đọc (chi tiết) |
|---|---|---|
| Sống | 14–70 ms | ~20 ms |
| Chết, trước khi giảm timeout | 5,6–6,0 giây | 10,9–12,0 giây |
| Chết, sau khi giảm timeout | 0,83–0,95 giây | 1,93–1,98 giây |
| Sống lại | — | 20 ms |

- Giảm được khoảng 6 lần ở ghi và 5–6 lần ở đọc. Chưa đạt ước lượng 0,3 và 0,6 giây: mỗi lệnh Redis chết tốn thêm thời gian ngoài `SyncTimeout`, nên đọc có ba lệnh thay vì hai.
- Bộ ngắt mạch: sau một lỗi Redis, 30 giây kế tiếp mọi thao tác bỏ qua Redis, nên chỉ lỗi đầu tiên trong mỗi cửa sổ còn chậm.
- Test: `RedisConnectionOptionsTests` (3 test: timeout ≤ 500 ms, không abort khi khởi động, giữ đúng host và port). Toàn bộ solution 253/253 xanh.
