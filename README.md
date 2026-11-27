# TicketHub — API bán vé sự kiện chịu tải cao

Backend bán vé viết bằng **.NET 10**, Clean Architecture + CQRS, PostgreSQL, Redis, RabbitMQ.

Bài toán cốt lõi: **mở bán 1.000 vé, 10.000 người bấm mua cùng lúc** — không bán vượt số vé, không trừ tiền hai lần khi khách bấm lại hay cổng thanh toán gọi lại, không phát vé cho đơn đã hết hạn giữ chỗ, không mất email vé khi hệ thống gửi tin đang sập.

Mỗi vấn đề dưới đây được **chứng minh bằng test trước khi sửa** (test đỏ trên code cũ, xanh sau khi sửa), chạy trên PostgreSQL thật trong container.

---

## Điểm nổi bật

| Vấn đề | Cách giải | Bằng chứng (integration test) |
|---|---|---|
| **Bán vượt số vé** khi nhiều người mua cùng lúc (lost update) | Trừ vé bằng `UPDATE ... WHERE available_quantity >= @q` trong transaction, khoá dòng theo thứ tự `ticket_type_id` để tránh deadlock | 50 người tranh 10 vé: trước khi sửa **50 đơn được tạo, kho vẫn còn 8–9**; sau khi sửa **đúng 10 đơn, 40 người nhận `SoldOut`** |
| Chọn cách chống bán vượt | Làm và đo cả ba cách: khoá bi quan (`FOR UPDATE`), khoá lạc quan (`xmin` + thử lại), UPDATE nguyên tử | Khoá lạc quan cần **~280 lần thử lại** cho 50 request; giới hạn 3 lần thử thì **bán thiếu** (chỉ 4–6/10 vé). Chọn UPDATE nguyên tử: 0 lần thử lại |
| **App tự gửi lại request đặt vé** khi mạng chập chờn | Header `Idempotency-Key`, unique index `(user_id, idempotency_key)`; gửi lại cùng key → trả lại đơn cũ (201 + `Idempotent-Replayed: true`), khác nội dung → 422 | 20 request cùng key cùng lúc: trước khi sửa **5 đơn**; sau khi sửa **đúng 1 đơn**, cả 20 request nhận cùng một đơn |
| **Webhook thanh toán bị gọi lặp** (kể cả cùng lúc) | Chữ ký HMAC-SHA256 trên body thô + timestamp (≤ 5 phút), so sánh thời gian không đổi; chốt lượt thanh toán bằng UPDATE có điều kiện **trước** khi đụng tới đơn | 10 webhook giống nhau cùng lúc → xử lý **đúng một lần, đúng 1 vé**. Bỏ điều kiện chốt → test đỏ: khách vừa có vé vừa bị đánh dấu hoàn tiền |
| **Tiền về sau khi đơn đã hết hạn** (job trả vé về kho chạy trước) | Webhook và job hết hạn cùng `UPDATE orders ... WHERE status = 'Pending'`, chỉ một bên thắng; webhook thua → payment `RefundRequired`, không phát vé | Cả hai thứ tự (job trước / webhook trước) đều có test |
| **Admin tăng số vé đúng lúc đang bán** | `xmin` làm concurrency token, xung đột thì đọc lại và áp lại (tối đa 3 lần) | Tắt `xmin`: **bán 22 vé cho tổng 20**; có `xmin`: bán đúng 20 |
| Huỷ đơn / job hết hạn trả vé hai lần | `UPDATE ... WHERE status = 'Pending'` + cộng kho bằng SQL (`available = available + n`) | Huỷ hai lần, huỷ đồng thời, quét hai lần → vé chỉ trả về một lần |
| **Mất email vé khi RabbitMQ sập** (ghi DB và gửi tin không chung transaction) | **Transactional outbox**: tin `OrderPaid` ghi vào `outbox_messages` cùng transaction với đơn `Paid`; job nền đẩy lên RabbitMQ (publisher confirms, `FOR UPDATE SKIP LOCKED`) | Broker chết: đơn vẫn `Paid`, tin nằm chờ; sống lại → thư tới. Đã chạy cả với RabbitMQ thật (tắt container) |
| **Gửi email vé hai lần** khi RabbitMQ giao lặp; máy chủ thư chập chờn | Consumer ack sau khi gửi, ghi `processed_messages` theo `MessageId`; lỗi tạm thời thử lại qua queue chờ có hẹn giờ, hết lượt → dead-letter | Đẩy bản lặp → vẫn 1 thư (tắt chống trùng → test đỏ); SMTP lỗi 2 lần → thử lại, đúng 1 thư; lỗi mãi → nằm ở dead-letter |

Chi tiết từng quyết định, số liệu và giới hạn đã biết: [`docs/notes.md`](docs/notes.md). Kế hoạch theo ticket: [`docs/DESIGN.md`](docs/DESIGN.md).

---

## Luồng mua vé

```mermaid
sequenceDiagram
    actor U as Khách
    participant API as TicketHub API
    participant DB as PostgreSQL
    participant G as Cổng thanh toán
    participant J as Job hết hạn (30s)

    U->>API: POST /orders (Idempotency-Key)
    API->>DB: UPDATE ticket_types SET available -= q WHERE available >= q
    API->>DB: INSERT order Pending (giữ chỗ 10 phút)
    API-->>U: 201 { id }

    U->>API: POST /orders/{id}/pay
    API->>DB: INSERT payment Pending (mỗi đơn tối đa 1)
    API-->>U: checkoutUrl
    U->>G: trả tiền

    G->>API: POST /payments/webhook (ký HMAC)
    API->>DB: UPDATE payment WHERE status = 'Pending'  (chặn gọi lặp)
    API->>DB: UPDATE order SET Paid WHERE status = 'Pending'
    API->>DB: INSERT tickets (mỗi chỗ một mã)
    API->>DB: INSERT outbox_messages (OrderPaid) — cùng transaction
    API-->>G: 200

    J->>DB: đơn Pending quá hạn → Expired, trả vé về kho
    Note over API,J: Webhook và job cùng nhắm đơn Pending: ai UPDATE trước thì thắng.<br/>Job thắng → payment RefundRequired, không phát vé.
```

### Email vé

```mermaid
flowchart LR
    OB[(outbox_messages)] -- "OutboxProcessor (2s)" --> EX{{tickethub.events}}
    EX -- OrderPaid --> Q[ticket-emails]
    Q --> C[TicketEmailConsumer]
    C -- "đã có trong processed_messages" --> ACK([ack, bỏ qua])
    C -- "soạn + gửi SMTP" --> M[Mailpit / SMTP]
    C -- "lỗi tạm thời" --> R[ticket-emails.retry<br/>chờ 30s] -- hết hạn --> Q
    C -- "hỏng hẳn / quá 5 lần" --> DLQ[ticket-emails.dead-letter]
```

Ít nhất một lần, không mất tin: RabbitMQ chết thì tin chờ trong outbox; app sập trước khi ack thì RabbitMQ giao lại; tin lặp bị `processed_messages` chặn. Chi tiết và giới hạn: [`docs/notes.md`](docs/notes.md), mục Ticket 5.

---

## Kiến trúc

```mermaid
flowchart LR
    Api["TicketHub.Api<br/>Controller mỏng, auth, rate limit,<br/>OpenAPI, health, logging"]
    App["TicketHub.Application<br/>Command/Query + Handler (MediatR)<br/>Validator (FluentValidation)"]
    Infra["TicketHub.Infrastructure<br/>EF Core, Dapper, repository,<br/>interceptor, migration, job nền"]
    Domain["TicketHub.Domain<br/>Entity, Result/Error,<br/>interface repository"]
    Api --> Infra --> App --> Domain
    Infra --> PG[(PostgreSQL)]
    Infra --> Redis[(Redis)]
    Infra --> MQ[(RabbitMQ)]
    Infra --> SMTP[(SMTP)]
```

- **Chiều phụ thuộc bị khoá bằng architecture test** (NetArchTest): Domain không phụ thuộc gì; Application không được tham chiếu Infrastructure hay ASP.NET Core; chỉ query handler được đọc DB trực tiếp.
- **Ghi** qua repository + `IUnitOfWork` (EF Core có tracking, interceptor audit và xoá mềm). Các thao tác có tranh chấp dùng **UPDATE có điều kiện** ngay trong SQL thay vì đọc rồi ghi.
- **Đọc** qua `IReadDbContext` (không tracking, `Select` thẳng sang DTO), hoặc Dapper khi LINQ không hợp.
- Lỗi nghiệp vụ trả về bằng `Result`/`Error` (không ném exception), đổi sang ProblemDetails ở một chỗ duy nhất.

**Công nghệ:** .NET 10 · ASP.NET Core · EF Core 10 + Npgsql · Dapper · PostgreSQL 16 · Redis 7 · RabbitMQ 4 (RabbitMQ.Client) · MailKit + Mailpit · MediatR · FluentValidation · Serilog · OpenTelemetry (Aspire Dashboard) · Scalar · xUnit v3 · NSubstitute · Testcontainers · NetArchTest · Docker Compose · GitHub Actions / GitLab CI.

---

## Tính năng

| Nhóm | Endpoint | Quyền |
|---|---|---|
| Sự kiện | `POST /api/v1/events`, `PUT /events/{id}`, `POST /events/{id}/publish` | Admin, Manager |
| | `POST /events/{id}/cancel` | Admin |
| | `GET /events` (phân trang, tìm theo tên, lọc ngày `?from=&to=`), `GET /events/{id}` | ẩn danh |
| Loại vé | `POST /events/{id}/ticket-types`, `PUT /events/{id}/ticket-types/{ticketTypeId}` (sửa được cả khi đang bán) | Admin, Manager |
| Đơn hàng | `POST /api/v1/orders` (+ `Idempotency-Key`), `POST /orders/{id}/cancel` | đăng nhập |
| | `GET /orders/my`, `GET /orders/{id}` (chủ đơn hoặc Admin; người khác 404) | đăng nhập |
| Thanh toán | `POST /orders/{id}/pay` | chủ đơn |
| | `POST /api/v1/payments/webhook` | cổng thanh toán (chữ ký HMAC) |
| Vé | `GET /api/v1/tickets/my` | đăng nhập |
| Tài khoản | `/api/v1/auth/login`, `/refresh`, `/logout`, `/me`, `/sessions`, `/change-password`, `/users` | xem mục [Đăng nhập](#đăng-nhập) |

Chạy nền:
- **`OrderExpiryService`** mỗi 30 giây chuyển đơn quá hạn giữ chỗ sang `Expired` và trả vé về kho (mỗi đơn một transaction, lỗi một đơn không chặn đơn khác).
- **`OutboxProcessor`** mỗi 2 giây đẩy tin chưa gửi trong `outbox_messages` lên RabbitMQ.
- **`TicketEmailConsumer`** nghe queue `ticket-emails`, gửi email mã vé cho người mua.

---

## Chạy thử nhanh

Yêu cầu: Docker Desktop.

```bash
cp .env.example .env          # điền POSTGRES_USER/PASSWORD, JWT_SECRET (>= 32 ký tự)
docker compose up -d --build  # Postgres, Redis, RabbitMQ, Mailpit, Aspire Dashboard, API (tự chạy migration)
```

Mở **http://localhost:8080/scalar** để xem tài liệu và gọi thử API.

**1. Lấy token** (endpoint chỉ có ở Development; `subject` là Id người dùng, phải là Guid):

```bash
# token ban tổ chức
curl -X POST http://localhost:8080/dev/token -H "Content-Type: application/json" \
     -d '{"subject":"11111111-1111-1111-1111-111111111111","roles":["Admin"]}'
# token người mua
curl -X POST http://localhost:8080/dev/token -H "Content-Type: application/json" \
     -d '{"subject":"22222222-2222-2222-2222-222222222222","roles":["User"]}'
```

Dán `accessToken` vào mục Authentication (Bearer) của Scalar.

**2. Đi hết một lần mua vé trên Scalar:**

1. *(Admin)* `POST /events` → `POST /events/{id}/ticket-types` → `POST /events/{id}/publish`.
2. *(User)* `POST /orders` với `{ "lines": [{ "ticketTypeId": "...", "quantity": 2 }] }`.
3. *(User)* `POST /orders/{id}/pay` → nhận `checkoutUrl`.
4. Gọi **POST** tới `checkoutUrl` (cổng thanh toán giả lập, chỉ có ở Development; thêm `?outcome=failed` để giả lập thất bại).
5. *(User)* `GET /orders/{id}` → đơn `Paid` kèm 2 vé; `GET /tickets/my`.
6. Mở **http://localhost:8025** (Mailpit) xem email vé.

> Email chỉ gửi được khi người mua là **tài khoản thật** (consumer tra email trong bảng người dùng). Token `/dev/token` ở trên mang Id tự đặt nên thư sẽ vào dead-letter. Muốn thấy thư thì mua bằng tài khoản có sẵn: `POST /api/v1/auth/login` với `admin@example.com` / `ChangeMe123!`, hoặc tạo tài khoản bằng `POST /api/v1/auth/users`.

**3. Thử email vé khi RabbitMQ sập** (đăng nhập `admin@example.com` như trên, đã có sự kiện đang bán):

```bash
docker compose stop rabbitmq     # 1. RabbitMQ "chết"
# 2. Đặt vé, POST /orders/{id}/pay, POST checkoutUrl → đơn vẫn Paid, Mailpit chưa có thư
docker exec tickethub_postgres psql -U postgres -d tickethub \
  -c "SELECT processed_at, attempts, last_error FROM outbox_messages ORDER BY occurred_at DESC LIMIT 1"
                                 # 3. tin nằm chờ: processed_at trống, attempts tăng dần
docker compose start rabbitmq    # 4. vài giây sau thư tới Mailpit, processed_at có giá trị
```

Xem queue (gồm `ticket-emails.retry`, `ticket-emails.dead-letter`) ở RabbitMQ Management: **http://localhost:15672** (`tickethub` / `tickethub`).

Xem SQL mà EF sinh ra cho từng request: `docker logs -f tickethub_api`.

| Địa chỉ | Nội dung |
|---|---|
| `http://localhost:8080/scalar` | Tài liệu API + gọi thử (Development) |
| `http://localhost:8080/openapi/v1.json` | Tài liệu OpenAPI |
| `http://localhost:8080/health/live`, `/health/ready` | Probe cho k8s |
| `http://localhost:18888` | Aspire Dashboard: trace, metrics |
| `http://localhost:8025` | Mailpit: hộp thư giả, xem email vé |
| `http://localhost:15672` | RabbitMQ Management: queue, tin trong dead-letter |

---

## Phát triển

Yêu cầu: .NET SDK 10, Docker Desktop.

```bash
cp .env.example .env
docker compose up -d postgres redis rabbitmq mailpit aspire-dashboard   # chỉ hạ tầng (Postgres ở cổng host 5433)
dotnet tool restore                                    # dotnet-ef
dotnet run --project src/TicketHub.Api                 # http://localhost:5315/scalar, tự migrate, in SQL ra console
```

> Postgres trong Docker mở ra máy host ở cổng **5433** (đổi bằng `POSTGRES_PORT` trong `.env`) để không đụng PostgreSQL cài sẵn trên máy. Nếu đổi cổng thì sửa luôn `ConnectionStrings:DefaultConnection` trong `appsettings.Development.json`.

### Cấu trúc

```
src/
  TicketHub.Domain          Events, TicketTypes, Orders, Payments, Tickets, Auth; Result/Error
  TicketHub.Application     Command/Query + Handler + Validator theo từng nghiệp vụ
  TicketHub.Infrastructure  EF Core, Dapper, repository, interceptor, migration, job nền,
                            Messaging (outbox → RabbitMQ, consumer email), Email (SMTP)
  TicketHub.Api             Controller, auth, rate limit, OpenAPI, health, cổng thanh toán giả lập
tests/
  TicketHub.UnitTests          Domain + handler + validator (NSubstitute)
  TicketHub.ArchitectureTests  Khoá chiều phụ thuộc giữa các tầng
  TicketHub.IntegrationTests   API thật + Postgres/Redis/RabbitMQ/Mailpit trong container (Testcontainers)
docs/
  DESIGN.md   Thiết kế và kế hoạch theo ticket
  notes.md    Quyết định kỹ thuật: làm gì, vì sao, giới hạn đã biết
```

### Test

**223 test**: 132 unit, 9 architecture, 82 integration. Integration test cần Docker đang chạy.

```bash
dotnet test --solution TicketHub.slnx                          # tất cả
dotnet test --project tests/TicketHub.UnitTests                # chỉ unit test
dotnet test --project tests/TicketHub.IntegrationTests --filter-class "*PaymentEndpointsTests"

# Test bán vượt (50 người, 10 vé) kèm số liệu thời gian: tìm dòng [oversell]
dotnet test --project tests/TicketHub.IntegrationTests --filter-method "*NeverOversell" --output Detailed
```

Các test tranh chấp bắn request song song bằng `Task.WhenAll` qua HTTP thật, rồi kiểm kết quả bằng cách đếm thẳng trong DB (không tin vào cột `available_quantity`).

---

## Quy ước

**Thêm một nghiệp vụ** — làm theo `Orders`:

1. `Domain/<Feature>/`: entity (setter private, factory trả `Result`), lỗi trong `Domain/Shared/<Feature>Errors`, `I<Feature>Repository`.
2. `Application/<Feature>/`: mỗi use case một file gồm command/query + validator + handler (handler `sealed`).
3. `Infrastructure/Persistence/`: `<Feature>Configuration` (EF) và `<Feature>Repository`. Repository tự đăng ký DI theo quy ước tên `XxxRepository` ↔ `IXxxRepository`.
4. `Api/Controllers/<Feature>Controller` kế thừa `BaseApiController`, chỉ gọi `ISender`. DTO HTTP để ở `Api/Contracts`.
5. Tạo migration (xem dưới), viết test.

**Xử lý lỗi — hai đường, không trộn:**

| Loại lỗi | Cách làm | HTTP |
|---|---|---|
| Dữ liệu đầu vào sai định dạng | FluentValidation → `ValidationBehavior` ném `CustomValidationException` | 400 + `errors` theo field |
| Lỗi nghiệp vụ dự kiến được | Handler trả `Result.Failure(Error.NotFound/Conflict/Unprocessable/…)` | 404 / 409 / 422 / 403 … + `code` |
| Lỗi bất ngờ | Để exception bay lên `GlobalExceptionHandler` | 500, không lộ chi tiết |

Mọi lỗi trả về dạng `application/problem+json` có `code` và `traceId` để tra log/trace.

**Tranh chấp dữ liệu:** thao tác nào có thể bị hai request chạm cùng lúc (trừ/cộng vé, đổi trạng thái đơn hay thanh toán) thì điều kiện phải nằm **ngay trong câu UPDATE** (`WHERE status = 'Pending'`, `WHERE available >= q`), kết quả là số dòng bị đổi. Đọc lên kiểm tra rồi mới ghi sẽ có khoảng trống cho request khác chen vào.

**Transaction:** một `SaveChangesAsync()` đã là một transaction. Cần nhiều câu lệnh (trộn `ExecuteUpdate` với `SaveChanges`) thì dùng `IUnitOfWork.ExecuteInTransactionAsync`:

| Tình huống | Hành vi |
|---|---|
| Delegate chạy xong | Commit |
| Ném exception | Rollback, xoá Change Tracker, exception đi lên nguyên kiểu |
| Trả `Result` thất bại | Rollback, trả nguyên `Result` |
| Gọi lồng nhau | Lời gọi trong nhập chung transaction ngoài |
| Lỗi kết nối tạm thời | Chạy lại cả delegate với Change Tracker đã xoá — vì vậy mọi thứ cần lưu (`Add`, tạo entity) phải nằm **trong** delegate |

Không tự gọi `Database.BeginTransaction` (EF đang bật `EnableRetryOnFailure`). Không gọi dịch vụ ngoài (gửi mail, gọi API) trong delegate vì nó có thể bị chạy lại.

**Audit & xoá mềm:** entity kế thừa `Entity` được tự điền `created_at/by`, `updated_at/by` khi `SaveChanges`; `Remove()` chỉ bật `is_deleted`, query EF tự lọc. Lưu ý: `ExecuteUpdate` bỏ qua interceptor nên không điền `updated_at`.

---

## Đăng nhập

Module đăng nhập riêng: schema `auth`, `AuthDbContext` và migration riêng (`src/TicketHub.Infrastructure/Auth`).

| Endpoint | Quyền | Việc |
|---|---|---|
| `POST /api/v1/auth/login` | ẩn danh | Access token (15') + refresh token (14 ngày) |
| `POST /api/v1/auth/refresh` | ẩn danh | Đổi refresh token lấy cặp mới; token cũ hết hiệu lực ngay |
| `POST /api/v1/auth/logout`, `/logout-all` | đăng nhập | Thu hồi phiên hiện tại / mọi phiên |
| `GET /api/v1/auth/me`, `/sessions` | đăng nhập | Thông tin user; danh sách thiết bị đang đăng nhập |
| `POST /api/v1/auth/change-password` | đăng nhập | Đổi mật khẩu, thu hồi mọi phiên khác |
| `POST /api/v1/auth/users` | Admin | Tạo tài khoản |
| `POST /api/v1/auth/register` | ẩn danh | Tự đăng ký — **tắt mặc định** (`Auth:AllowSelfRegistration`) |

- Refresh token cho web nằm trong cookie `HttpOnly; Secure; SameSite=Strict`; DB chỉ lưu **SHA-256** của token. Mobile gửi `X-Client-Type: mobile` để nhận token trong body.
- Khoá 15' sau 5 lần sai mật khẩu; cùng một thông báo cho sai email và sai mật khẩu; rate limit riêng cho login/refresh/register; refresh token dùng lại sau khi đã đổi → **thu hồi cả họ phiên**; `xmin` chặn hai request cùng đổi một phiên.
- Development tự tạo tài khoản `admin@example.com` / `ChangeMe123!` (`Auth:SeedAdmin`).
- **Giới hạn:** access token stateless nên đăng xuất chỉ chặn được access token đã phát khi nó hết hạn (tối đa `Auth:AccessTokenMinutes`).

---

## Migration

Hai DbContext (`ApplicationDbContext` cho nghiệp vụ, `AuthDbContext` cho đăng nhập) nên mọi lệnh phải ghi `--context`:

```bash
dotnet ef migrations add <Ten> --context ApplicationDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure -o Persistence/Migrations
dotnet ef migrations add <Ten> --context AuthDbContext        -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure -o Auth/Migrations

dotnet ef migrations bundle --context ApplicationDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure -o efbundle
```

- **Development:** `Database:MigrateOnStartup=true`, app tự migrate lúc khởi động.
- **Production:** `MigrateOnStartup=false`; migration là bước riêng trong CI/CD (job `migrate` chạy `efbundle`), trước khi deploy app mới. Lý do: app chỉ cần quyền đọc/ghi dữ liệu, migration lâu không làm k8s giết pod giữa chừng, và lỗi migration dừng deploy thay vì để DB dở dang.

---

## Cấu hình

Giá trị bí mật **không** nằm trong `appsettings.json`; đặt qua biến môi trường (`__` thay cho `:`).

| Khoá | Bắt buộc | Ghi chú |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | ✔ | PostgreSQL |
| `ConnectionStrings__Redis` | ✔ | |
| `ConnectionStrings__RabbitMq` | ✔ | `amqp://user:pass@host:5672` |
| `Smtp__Host`, `Smtp__Port` | ✔ | Máy chủ gửi email; dev là Mailpit (1025) |
| `Smtp__UseSsl`, `Smtp__UserName`, `Smtp__Password` | | Máy chủ thư thật (SendGrid, SES...); để trống tên = không đăng nhập |
| `Smtp__FromAddress`, `Smtp__FromName` | | Mặc định `no-reply@tickethub.local`, `TicketHub` |
| `TicketEmailConsumer__MaxRetries`, `__RetryDelay` | | Thử lại email khi lỗi tạm thời, mặc định 5 lần × `00:00:30` |
| `JwtSettings__Secret` | ✔ | ≥ 32 ký tự; thiếu/ngắn → app dừng ngay khi khởi động |
| `Payments__WebhookSecret` | ✔ | Khoá ký webhook chung với cổng thanh toán, ≥ 32 ký tự |
| `Payments__SignatureToleranceSeconds` | | Độ lệch timestamp tối đa của webhook, mặc định 300 |
| `Cors__AllowedOrigins__0..n` | | Rỗng = chặn cross-origin |
| `RateLimiting__PermitLimit`, `__WindowSeconds` | | Mặc định 100 request / 60 s mỗi user hoặc IP |
| `OpenApi__Enabled` | | Bật tài liệu API ngoài Development |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | | Có thì mới bật OpenTelemetry |

---

## Build & CI

- Phiên bản gói khai báo một chỗ ở `Directory.Packages.props`. `Directory.Build.props`: nullable, analyzer mức Recommended, **warning = lỗi**.
- MediatR khoá ở 12.5.0 — bản MIT cuối.
- `.github/workflows/ci.yml` và `.gitlab-ci.yml`: restore → build → quét gói lỗ hổng → test → build image → migrate (job `migrate` tắt mặc định, bật bằng biến `RUN_DB_MIGRATIONS`).

---

## Chưa làm

- Hoàn tiền thật cho payment `RefundRequired` (hiện mới đánh dấu).
- Soát vé (`Valid → Used`), huỷ vé khi huỷ sự kiện.
- Dọn bảng `outbox_messages` / `processed_messages`; công cụ xem và gửi lại tin trong dead-letter (hiện làm tay qua RabbitMQ Management).
- Cache Redis cho trang sự kiện, đo tải bằng k6, deploy (Ticket 6–8 trong [`docs/DESIGN.md`](docs/DESIGN.md)).
