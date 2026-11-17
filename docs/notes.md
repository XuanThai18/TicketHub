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

- `GET /events/{id}`: Admin thấy mọi trạng thái; khách và User thường gọi vào bản nháp nhận **404** (không phải 403, để không lộ việc sự kiện tồn tại).
- `GET /events` (danh sách): **chỉ hiện sự kiện không phải `Draft`**, kể cả với Admin. Sự kiện `Cancelled` vẫn hiện, kèm trạng thái.
- **Việc còn để ngỏ:** Admin chưa có màn hình liệt kê bản nháp. Khi cần thì thêm `ICurrentUser` vào `ListEventsQueryHandler` giống `GetEventById`.

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
