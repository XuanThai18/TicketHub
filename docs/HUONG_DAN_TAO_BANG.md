# Hướng dẫn tạo một bảng — làm mẫu với `events`

> Quy trình **7 bước, theo thứ tự**, áp dụng cho mọi bảng. Làm `events` theo từng bước bên dưới,
> rồi tự lặp lại cho `ticket_types` (gợi ý ở cuối file).
>
> **Gõ tay, đừng copy-paste** — mục đích là hiểu từng dòng. Code dưới đây đã được build và test thử trên TicketHub.

```
1. Nhánh git → 2. Domain (entity, enum, lỗi) → 3. Interface repository → 4. Cấu hình EF
→ 5. Repository → 6. Migration → 7. Kiểm tra (pgAdmin + unit test) → commit
```

---

## Bước 1 — Tạo nhánh

```bash
git switch -c ticket-1-events
```

---

## Bước 2 — Domain: mô tả dữ liệu và luật

Tạo thư mục `src/TicketHub.Domain/Events/`. Tầng Domain **không biết gì về DB** — chỉ có class C# và luật nghiệp vụ.

### 2.0 `Common/Entity.cs` — lớp cơ sở (làm **một lần**, mọi bảng dùng lại)

Cột audit và xoá mềm giống nhau ở mọi bảng, nên khai báo **một lần** ở lớp cha. Entity nghiệp vụ chỉ cần `: Entity` là có đủ 7 property này — **không khai báo lại** trong class con.

```csharp
namespace TicketHub.Domain.Common;

/// <summary>Lớp cơ sở cho entity nghiệp vụ: có sẵn cột audit và xoá mềm.</summary>
public abstract class Entity : IAuditableEntity, ISoftDeletable
{
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
```

- `abstract` ở **class**: không `new Entity()` được, chỉ dùng làm lớp cha.
- Property **không** có `abstract`: đây là property thật, class con thừa hưởng nguyên. Nếu ghi `public abstract DateTimeOffset CreatedAt` thì class con **bắt buộc** khai báo lại → lỗi CS0534.
- `{ get; set; }` (không `private set`): interceptor của template cần gán các giá trị này từ bên ngoài.

### 2.1 `EventStatus.cs` — các trạng thái

```csharp
namespace TicketHub.Domain.Events;

public enum EventStatus
{
    Draft = 1,
    Published = 2,
    Cancelled = 3
}
```

### 2.2 `EventErrors.cs` — lỗi nghiệp vụ

Mỗi lỗi có **mã** (FE dựa vào để xử lý) + **thông báo** + **loại** (quyết định mã HTTP: Validation → 400, NotFound → 404, Conflict → 409).

```csharp
using TicketHub.Domain.Shared;

namespace TicketHub.Domain.Events;

public static class EventErrors
{
    public static readonly Error InvalidSchedule = Error.Validation(
        "Events.InvalidSchedule",
        "Sale must start before it ends, end no later than the event starts, and the event must start before it ends.");

    public static readonly Error NotEditable = Error.Conflict(
        "Events.NotEditable", "Only draft events can be edited.");

    public static readonly Error NoTicketTypes = Error.Validation(
        "Events.NoTicketTypes", "An event needs at least one ticket type before it can be published.");

    public static readonly Error AlreadyCancelled = Error.Conflict(
        "Events.AlreadyCancelled", "The event is already cancelled.");

    public static Error NotFound(Guid id) => Error.NotFound("Events.NotFound", $"Event '{id}' was not found.");
}
```

### 2.3 `Event.cs` — entity

```csharp
using TicketHub.Domain.Common;
using TicketHub.Domain.Shared;

namespace TicketHub.Domain.Events;

public sealed class Event : Entity
{
    public const int NameMaxLength = 200;
    public const int VenueMaxLength = 300;

    private Event()
    {
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string Venue { get; private set; } = string.Empty;
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public DateTimeOffset SaleStartsAt { get; private set; }
    public DateTimeOffset SaleEndsAt { get; private set; }
    public EventStatus Status { get; private set; }

    // CreatedAt, IsDeleted… đã có sẵn từ Entity — không khai báo lại.

    public static Result<Event> Create(
        string name, string? description, string venue,
        DateTimeOffset startsAt, DateTimeOffset endsAt,
        DateTimeOffset saleStartsAt, DateTimeOffset saleEndsAt)
    {
        if (!IsValidSchedule(startsAt, endsAt, saleStartsAt, saleEndsAt))
            return EventErrors.InvalidSchedule;

        return new Event
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Description = description?.Trim(),
            Venue = venue.Trim(),
            StartsAt = startsAt,
            EndsAt = endsAt,
            SaleStartsAt = saleStartsAt,
            SaleEndsAt = saleEndsAt,
            Status = EventStatus.Draft
        };
    }

    public Result Update(
        string name, string? description, string venue,
        DateTimeOffset startsAt, DateTimeOffset endsAt,
        DateTimeOffset saleStartsAt, DateTimeOffset saleEndsAt)
    {
        if (Status != EventStatus.Draft)
            return EventErrors.NotEditable;

        if (!IsValidSchedule(startsAt, endsAt, saleStartsAt, saleEndsAt))
            return EventErrors.InvalidSchedule;

        Name = name.Trim();
        Description = description?.Trim();
        Venue = venue.Trim();
        StartsAt = startsAt;
        EndsAt = endsAt;
        SaleStartsAt = saleStartsAt;
        SaleEndsAt = saleEndsAt;
        return Result.Success();
    }

    // Số loại vé truyền từ ngoài vào: Event không giữ danh sách TicketType, handler đếm rồi đưa vào.
    public Result Publish(int ticketTypeCount)
    {
        if (Status != EventStatus.Draft)
            return EventErrors.NotEditable;

        if (ticketTypeCount == 0)
            return EventErrors.NoTicketTypes;

        Status = EventStatus.Published;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == EventStatus.Cancelled)
            return EventErrors.AlreadyCancelled;

        Status = EventStatus.Cancelled;
        return Result.Success();
    }

    private static bool IsValidSchedule(
        DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset saleStartsAt, DateTimeOffset saleEndsAt) =>
        saleStartsAt < saleEndsAt && saleEndsAt <= startsAt && startsAt < endsAt;
}
```

**Giải thích các quyết định — phải hiểu được trước khi đi tiếp:**

| Chỗ | Vì sao |
|---|---|
| `private Event()` | Không ai `new Event()` tuỳ tiện ngoài class. Tạo **chỉ qua** `Create(...)`, nên mọi Event sinh ra đều đã qua kiểm tra luật. EF vẫn dùng được constructor private để đọc từ DB |
| `private set` | Code bên ngoài không gán thẳng `ev.Status = ...` được; muốn đổi trạng thái phải gọi `Publish()` / `Cancel()` — luật luôn được kiểm |
| `Create` trả `Result<Event>` | Dữ liệu sai là **chuyện bình thường** (người dùng nhập sai) → trả lỗi, **không ném exception** |
| `return EventErrors.InvalidSchedule;` trong hàm trả `Result<Event>` | Template có chuyển đổi ngầm `Error → Result<Event>`, viết gọn như vậy được |
| `Guid.CreateVersion7()` | UUID có thời gian ở đầu → index không phân mảnh như `Guid.NewGuid()` |
| `: Entity` | Thừa hưởng 7 cột audit + xoá mềm (mục 2.0). Interceptor của template tự điền `created_at/by`…; `Remove()` thành xoá mềm. Không phải tự gán |
| `DateTimeOffset` | Lưu thành `timestamptz` trong Postgres — có múi giờ, không lẫn giờ VN với UTC |
| `const NameMaxLength` | Dùng chung cho cấu hình EF (độ dài cột) và validator (Ticket 1) — sửa một chỗ |

---

## Bước 3 — Interface repository (vẫn ở Domain)

`src/TicketHub.Domain/Events/IEventRepository.cs` — chỉ phục vụ **phía ghi**. Phía đọc (danh sách, chi tiết) dùng `IReadDbContext` ở Application.

```csharp
namespace TicketHub.Domain.Events;

public interface IEventRepository
{
    Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(Event @event);
}
```

> `@event`: `event` là từ khoá của C#, thêm `@` phía trước để dùng làm tên biến.

---

## Bước 4 — Cấu hình EF: thay cho câu `CREATE TABLE`

Tạo thư mục `src/TicketHub.Infrastructure/Persistence/Configurations/` và file `EventConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Domain.Events;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events", table => table.HasCheckConstraint(
            "ck_events_schedule",
            "sale_starts_at < sale_ends_at AND sale_ends_at <= starts_at AND starts_at < ends_at"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(Event.NameMaxLength).IsRequired();
        builder.Property(e => e.Venue).HasMaxLength(Event.VenueMaxLength).IsRequired();

        // Lưu enum dạng chữ ("Draft") thay vì số (1): đọc DB dễ, thêm giá trị mới không lệch số.
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

        // Trang danh sách lọc theo trạng thái rồi sắp theo ngày diễn ra.
        builder.HasIndex(e => new { e.Status, e.StartsAt });
    }
}
```

| Dòng | Sinh ra trong SQL |
|---|---|
| `ToTable("events", ...HasCheckConstraint(...))` | `CREATE TABLE events (...)` + `CONSTRAINT ck_events_schedule CHECK (...)` — luật thời gian được DB chặn thêm một lớp |
| `HasMaxLength(200).IsRequired()` | `name character varying(200) NOT NULL` |
| `HasConversion<string>()` | `status character varying(20)`, giá trị `'Draft'` |
| `HasIndex(...)` | `CREATE INDEX ix_events_status_starts_at ON events (status, starts_at)` |

> **Không phải đăng ký file này ở đâu cả.** `ApplicationDbContext` tự quét mọi class `IEntityTypeConfiguration` trong project.
> Tên cột snake_case (`starts_at`) cũng tự đổi nhờ cấu hình có sẵn. Câu SQL trong `HasCheckConstraint` là SQL thật nên phải viết tên cột dạng snake_case.

---

## Bước 5 — Repository

Tạo thư mục `src/TicketHub.Infrastructure/Persistence/Repositories/` và file `EventRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using TicketHub.Domain.Events;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Persistence.Repositories;

internal sealed class EventRepository(ApplicationDbContext dbContext) : IEventRepository
{
    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<Event>().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Add(Event @event) => dbContext.Set<Event>().Add(@event);
}
```

> **Cũng không phải đăng ký DI.** Template tự đăng ký class tên `XxxRepository` cho interface `IXxxRepository`.
> `Add` chỉ đánh dấu "sẽ thêm" — chưa ghi DB. Ghi thật khi handler gọi `IUnitOfWork.SaveChangesAsync()`.

**Build để chắc chưa có lỗi:** `dotnet build TicketHub.slnx` (hoặc Ctrl+Shift+B trong VS).

---

## Bước 6 — Migration

Chạy ở thư mục gốc TicketHub. **Dừng API trước** nếu đang chạy (file DLL bị khoá thì lệnh build của EF sẽ lỗi).

```bash
dotnet ef migrations add AddEvents --context ApplicationDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure -o Persistence/Migrations
```

Sinh ra 3 file trong `src/TicketHub.Infrastructure/Persistence/Migrations/`:

| File | Là gì |
|---|---|
| `<thời gian>_AddEvents.cs` | **Đọc file này**: `Up()` tạo bảng, `Down()` xoá bảng |
| `<thời gian>_AddEvents.Designer.cs` | EF dùng nội bộ, không sửa |
| `ApplicationDbContextModelSnapshot.cs` | "Ảnh chụp" model hiện tại để lần sau so sánh, không sửa |

**Xem câu SQL thật** EF sẽ chạy:

```bash
dotnet ef migrations script --context ApplicationDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure
```

Phải thấy:

```sql
CREATE TABLE events (
    id uuid NOT NULL,
    name character varying(200) NOT NULL,
    description text,
    venue character varying(300) NOT NULL,
    starts_at timestamp with time zone NOT NULL,
    ...
    status character varying(20) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    ...
    is_deleted boolean NOT NULL,
    CONSTRAINT pk_events PRIMARY KEY (id),
    CONSTRAINT ck_events_schedule CHECK (sale_starts_at < sale_ends_at AND sale_ends_at <= starts_at AND starts_at < ends_at)
);
CREATE INDEX ix_events_status_starts_at ON events (status, starts_at);
```

> **Tạo sai thì sao?** Khi migration **chưa áp vào DB**: `dotnet ef migrations remove --context ApplicationDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure`,
> sửa code, tạo lại. Đã áp rồi thì **tạo migration mới để sửa**, đừng sửa file migration cũ.

---

## Bước 7 — Kiểm tra

### 7.1 Áp migration và xem bảng

1. Chạy API (F5 trong VS, profile `http`). Log có dòng `Applying migration '..._AddEvents'`.
2. pgAdmin → server **TicketHub (Docker)** (port 5433) → `tickethub` → Schemas → **public** → Tables → `events`.
3. Thử luật CHECK của DB — chạy trong Query Tool, **phải báo lỗi** `violates check constraint "ck_events_schedule"`:

```sql
INSERT INTO events (id, name, venue, starts_at, ends_at, sale_starts_at, sale_ends_at, status, created_at, is_deleted)
VALUES (gen_random_uuid(), 'Test', 'X', now(), now() + interval '1 hour',
        now() + interval '2 hour', now() + interval '3 hour', 'Draft', now(), false);
```

### 7.2 Unit test cho luật trong entity

`tests/TicketHub.UnitTests/Domain/EventTests.cs`:

```csharp
using TicketHub.Domain.Events;

namespace TicketHub.UnitTests.Domain;

public sealed class EventTests
{
    private static readonly DateTimeOffset SaleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SaleEnd = SaleStart.AddDays(10);
    private static readonly DateTimeOffset Start = SaleEnd.AddDays(1);
    private static readonly DateTimeOffset End = Start.AddHours(3);

    private static Event NewDraft() =>
        Event.Create(" Concert ", null, "Nhà hát", Start, End, SaleStart, SaleEnd).Value;

    [Fact]
    public void Create_ValidSchedule_IsDraftAndTrimsName()
    {
        var created = NewDraft();

        Assert.Equal(EventStatus.Draft, created.Status);
        Assert.Equal("Concert", created.Name);
    }

    [Fact]
    public void Create_SaleEndsAfterEventStarts_Fails()
    {
        var result = Event.Create("Concert", null, "Nhà hát", Start, End, SaleStart, Start.AddHours(1));

        Assert.Equal(EventErrors.InvalidSchedule, result.Error);
    }

    [Fact]
    public void Publish_WithoutTicketTypes_Fails()
    {
        var created = NewDraft();

        Assert.Equal(EventErrors.NoTicketTypes, created.Publish(ticketTypeCount: 0).Error);
        Assert.Equal(EventStatus.Draft, created.Status);
    }

    [Fact]
    public void Update_AfterPublish_IsRejected()
    {
        var created = NewDraft();
        created.Publish(ticketTypeCount: 1);

        var result = created.Update("New", null, "X", Start, End, SaleStart, SaleEnd);

        Assert.Equal(EventErrors.NotEditable, result.Error);
    }
}
```

```bash
dotnet test --project tests/TicketHub.UnitTests
```

> Tên test theo mẫu `HàmĐượcGọi_TìnhHuống_KếtQuảMongĐợi`. Đọc tên là biết test kiểm cái gì.

### 7.3 Commit

```bash
git add -A
git commit -m "Add events table (entity, EF config, migration, domain tests)"
```

---

## Tự làm: bảng `ticket_types`

Lặp lại **đúng 7 bước** trên. Thiết kế ở `docs/DESIGN.md` mục 2.4. Gợi ý theo mức — **thử tự làm trước**, bí mới đọc mức tiếp theo.

<details>
<summary><b>Gợi ý mức 1 — cần những gì</b></summary>

- Domain: `TicketType` (đặt cùng thư mục `Events/` vì luôn đi kèm sự kiện), `TicketTypeErrors`.
- Luật trong `Create`: `price >= 0`, `totalQuantity > 0`, `1 <= maxPerOrder <= totalQuantity`. Lúc tạo `AvailableQuantity = TotalQuantity`.
- Có cột `EventId` (khoá ngoại).
- Repository: thêm vào `IEventRepository` hoặc tạo `ITicketTypeRepository` — cần ít nhất `Add`, `CountByEventAsync` (để `Publish` biết số loại vé), `NameExistsAsync`.

</details>

<details>
<summary><b>Gợi ý mức 2 — cấu hình EF</b></summary>

- Khoá ngoại: `builder.HasOne<Event>().WithMany().HasForeignKey(t => t.EventId);`
- 2 CHECK: `available_quantity >= 0` và `available_quantity <= total_quantity`.
- Unique: `builder.HasIndex(t => new { t.EventId, t.Name }).IsUnique();`
- Giá: `builder.Property(t => t.Price).HasPrecision(18, 2);`
- Concurrency token cho Ticket 3: `builder.Property<uint>("Version").IsRowVersion();` (cột ẩn `xmin` của Postgres — tham khảo `UserSessionConfiguration` trong module Auth).

</details>

<details>
<summary><b>Gợi ý mức 3 — kiểm tra</b></summary>

- Migration tên `AddTicketTypes`. Trong SQL phải thấy `REFERENCES events (id)`, 2 CHECK, unique index `(event_id, name)`.
- pgAdmin: thử `UPDATE ticket_types SET available_quantity = -1 ...` → phải bị CHECK chặn.
- Unit test: số lượng âm → lỗi; `maxPerOrder > totalQuantity` → lỗi; tạo hợp lệ → `AvailableQuantity == TotalQuantity`.

</details>

Xong `ticket_types` → gửi code để review → tiếp phần Application + Api của Ticket 1 (tạo/sửa/công bố sự kiện qua endpoint).

---

## Checklist cho mọi bảng sau này

- [ ] Entity: constructor `private`, setter `private`, tạo qua `Create` trả `Result`, luật nằm trong entity
- [ ] Lỗi nghiệp vụ trong `XxxErrors` với mã dạng `Xxx.TenLoi`
- [ ] Cấu hình EF: tên bảng snake_case số nhiều, độ dài chuỗi, `IsRequired`, CHECK cho luật quan trọng, index theo câu truy vấn
- [ ] Repository: interface ở Domain, cài đặt ở Infrastructure, tên `XxxRepository`
- [ ] Migration: **đọc file sinh ra** + xem SQL bằng `migrations script`
- [ ] pgAdmin: thấy bảng, thử vi phạm CHECK
- [ ] Unit test cho từng luật
- [ ] Build không warning, toàn bộ test đạt → commit
