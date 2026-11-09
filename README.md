# TicketHub — .NET 10 Clean Architecture API template

Template Web API cho dự án mới: Clean Architecture, CQRS với MediatR, EF Core (ghi) + Dapper (đọc) trên PostgreSQL, Redis, JWT, OpenTelemetry, test đầy đủ với Testcontainers.

## Dùng template

```bash
dotnet new install .                       # cài từ thư mục này (chạy lại sau khi sửa template)
dotnet new ca-api -n Acme.Shop             # tạo solution mới, đổi mọi "TicketHub" thành "Acme.Shop"
dotnet new ca-api -n Acme.Shop --IncludeSample false   # bỏ feature mẫu Products
dotnet new ca-api -n Acme.Shop --auth none # không có module đăng nhập (dùng IdP ngoài)
dotnet new uninstall .                     # gỡ template
```

| Tuỳ chọn | Mặc định | Ý nghĩa |
|---|---|---|
| `--IncludeSample` | `true` | Kèm feature mẫu `Products` (entity, command/query, controller, migration, test) |
| `--auth` | `local` | `local`: module đăng nhập riêng (xem mục Đăng nhập). `none`: chỉ kiểm JWT, token do Keycloak/Entra/Auth0 cấp |
| `--SkipRestore` | `false` | Không tự `dotnet restore` sau khi tạo |

Cổng `launchSettings.json` được sinh ngẫu nhiên cho mỗi project để không đụng nhau.

## Chạy local

Yêu cầu: .NET SDK 10, Docker Desktop.

```bash
cp .env.example .env                                   # điền mật khẩu DB, JWT secret
docker compose up -d postgres redis aspire-dashboard   # hạ tầng (Postgres ở cổng host 5433)
dotnet tool restore                                    # dotnet-ef
dotnet run --project src/TicketHub.Api                   # API (Development: tự migrate)
```

Hoặc chạy cả API trong container: `docker compose up --build`.

> Postgres trong Docker mở ra máy host ở cổng **5433** (đổi bằng `POSTGRES_PORT` trong `.env`) để không đụng PostgreSQL cài sẵn trên máy, thường chiếm 5432. Nếu đổi cổng thì sửa luôn `ConnectionStrings:DefaultConnection` trong `appsettings.Development.json`.

| Địa chỉ | Nội dung |
|---|---|
| `http://localhost:<port>/scalar` | Tài liệu API + gọi thử (chỉ Development) |
| `http://localhost:<port>/openapi/v1.json` | Tài liệu OpenAPI |
| `http://localhost:<port>/health/live`, `/health/ready` | Probe cho k8s |
| `http://localhost:18888` | Aspire Dashboard: trace, metrics |

**Lấy token để gọi thử** (chỉ có ở Development):

```bash
curl -X POST http://localhost:<port>/dev/token -H "Content-Type: application/json" \
     -d '{"subject":"dev-user","roles":["Admin"]}'
```

Với `--auth local`, Development tự tạo tài khoản `admin@example.com` / `ChangeMe123!` (cấu hình `Auth:SeedAdmin` trong `appsettings.Development.json`) để đăng nhập thật qua `/api/v1/auth/login`.

## Đăng nhập (`--auth local`)

Bảng nằm trong schema `auth`, `AuthDbContext` và migration riêng (`src/TicketHub.Infrastructure/Auth`), tách khỏi dữ liệu nghiệp vụ:

| Bảng | Nội dung |
|---|---|
| `auth.users` | Email (chữ thường, unique), mật khẩu băm PBKDF2, khoá tạm, `security_stamp` |
| `auth.user_roles` | Vai trò: `Admin`, `Manager`, `User` (hằng `Roles`) |
| `auth.user_sessions` | Mỗi dòng một thiết bị đang đăng nhập; chỉ lưu **SHA-256** của refresh token |

| Endpoint | Quyền | Việc |
|---|---|---|
| `POST /api/v1/auth/login` | ẩn danh | Trả access token (15') + refresh token (14 ngày) |
| `POST /api/v1/auth/refresh` | ẩn danh | Đổi refresh token lấy cặp mới; token cũ hết hiệu lực ngay |
| `POST /api/v1/auth/logout`, `/logout-all` | đăng nhập | Thu hồi phiên hiện tại / mọi phiên |
| `GET /api/v1/auth/me`, `/sessions` | đăng nhập | Thông tin user; danh sách thiết bị đang đăng nhập |
| `POST /api/v1/auth/change-password` | đăng nhập | Đổi mật khẩu, thu hồi mọi phiên khác |
| `POST /api/v1/auth/users` | Admin | Tạo tài khoản |
| `POST /api/v1/auth/register` | ẩn danh | Tự đăng ký — **tắt mặc định** (`Auth:AllowSelfRegistration`) |

**Cách FE giữ refresh token:**

- **Web (mặc định):** refresh token nằm trong cookie `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` — JavaScript không đọc được, lỗi XSS không lấy cắp được. Body login chỉ có access token: giữ trong **bộ nhớ** (biến JS), không ghi `localStorage`. Khi access token hết hạn: `POST /api/v1/auth/refresh` **không body**, kèm header `X-CSRF: 1` và `credentials: "include"`. Đường dẫn gọi phải viết thường (`/auth`) vì `Path` của cookie phân biệt hoa thường.
- **Mobile / hệ thống khác:** gửi header `X-Client-Type: mobile` khi login → refresh token trả trong body; refresh bằng body `{ "refreshToken": "..." }`. Muốn mọi client đều dùng body: `Auth:RefreshToken:DefaultTransport = Body`.
- FE khác origin dùng cookie: `Cors:AllowCredentials = true` và liệt kê `Cors:AllowedOrigins`. FE khác **site** (khác domain gốc) thì `Auth:RefreshToken:SameSite = None`.

**Bảo vệ đi kèm:** khoá 15' sau 5 lần sai mật khẩu; cùng một thông báo lỗi cho sai email và sai mật khẩu (kèm băm giả để thời gian phản hồi như nhau); rate limit riêng 100 request/phút/IP cho login, refresh, register (`RateLimiting:SensitivePermitLimit`; để rộng vì cả văn phòng thường chung một IP qua NAT — chống dò mật khẩu một tài khoản là việc của cơ chế khoá tài khoản); refresh token dùng lại sau khi đã đổi → **thu hồi cả họ phiên** (trừ 10 giây ân hạn cho hai tab refresh cùng lúc); concurrency token `xmin` chặn hai request cùng đổi một phiên.

**Giới hạn đã biết:** access token không kiểm DB ở mỗi request (stateless) nên đăng xuất / đổi mật khẩu chỉ chặn được access token đã phát khi nó hết hạn (tối đa `Auth:AccessTokenMinutes`). Cần chặn tức thì thì thêm kiểm `sid` với danh sách phiên bị thu hồi trong Redis ở `OnTokenValidated` của JwtBearer.

**Production:** đặt `Auth__SeedAdmin__Email` / `Auth__SeedAdmin__Password` cho lần chạy đầu (chỉ tạo khi bảng users trống), đăng nhập rồi đổi mật khẩu ngay, sau đó gỡ biến môi trường.


## Cấu trúc

```
src/
  TicketHub.Domain          Entity, Result/Error, interface repository. Không phụ thuộc gì.
  TicketHub.Application     Command/Query + Handler + Validator (MediatR), abstraction (IUnitOfWork, IReadDbContext, ICurrentUser…)
  TicketHub.Infrastructure  EF Core, Dapper, Redis, repository, interceptor audit/soft-delete, migration
  TicketHub.Api             Controller, auth, rate limit, OpenAPI, health, logging/tracing
tests/
  TicketHub.UnitTests          Domain + handler + validator (NSubstitute)
  TicketHub.ArchitectureTests  Khoá chiều phụ thuộc giữa các tầng (NetArchTest)
  TicketHub.IntegrationTests   API thật + Postgres/Redis trong container (Testcontainers)
```

Chiều phụ thuộc: `Api → Infrastructure → Application → Domain`. Application được dùng EF Core (chỉ trong query handler) nhưng không được tham chiếu provider Npgsql hay Infrastructure. Architecture test sẽ đỏ nếu vi phạm.

## Quy ước

**Thêm một feature** — làm theo `Products`:

1. `Domain/<Feature>/`: entity (setter private, factory trả `Result`), `<Feature>Errors`, `I<Feature>Repository`.
2. `Application/<Feature>/`: mỗi use case một file gồm command/query + validator + handler (handler `sealed`).
3. `Infrastructure/Persistence/`: `<Feature>Configuration` (EF) và `<Feature>Repository`. Repository tự đăng ký DI theo quy ước tên `XxxRepository` ↔ `IXxxRepository`.
4. `Api/Controllers/<Feature>Controller` kế thừa `BaseApiController`, chỉ gọi `ISender`. DTO HTTP để ở `Api/Contracts`.
5. Tạo migration (xem dưới), viết test.

**Xử lý lỗi — hai đường, không trộn:**

| Loại lỗi | Cách làm | HTTP |
|---|---|---|
| Dữ liệu đầu vào sai định dạng | FluentValidation → `ValidationBehavior` ném `CustomValidationException` | 400 + `errors` theo field |
| Lỗi nghiệp vụ dự kiến được | Handler trả `Result.Failure(Error.NotFound/Conflict/…)` | 404 / 409 / 403 … + `code` |
| Lỗi bất ngờ | Cứ để exception bay lên | 500, không lộ chi tiết |

Mọi lỗi trả về dạng `application/problem+json` có `traceId` để tra log/trace.

**Đọc/ghi tách đôi:**

- **Ghi (command):** repository + EF Core có tracking, interceptor audit/soft-delete, `IUnitOfWork`. Command handler **không** được dùng EF trực tiếp (architecture test kiểm).
- **Đọc (query) — mặc định LINQ:** `IReadDbContext.Query<T>()` trả `IQueryable` không tracking, đã lọc bản ghi xoá mềm. Luôn kết thúc bằng `Select` sang DTO để chỉ lấy cột cần (xem `GetProductById`, `ListProducts`, phép chiếu dùng chung `ProductResponse.Projection`).
- **Đọc — SQL tay (Dapper) khi LINQ không hợp:** qua `ISqlConnectionFactory` (xem `GetProductPriceStats`). Dapper **không** đi qua query filter của EF → SQL phải tự lọc `is_deleted = false`.

| Truy vấn | Viết bằng |
|---|---|
| Chi tiết theo id, danh sách lọc/sắp xếp/phân trang, join 2–3 bảng | LINQ + `Select` |
| Báo cáo: `GROUP BY` nhiều tầng, window function, CTE, `percentile_cont`… | SQL tay (Dapper) |
| Tính năng riêng Postgres: JSONB, full-text, `DISTINCT ON`, `LATERAL` | SQL tay (Dapper) |
| Hot path đã đo thấy chậm và không chỉnh được qua LINQ | SQL tay (Dapper) |

Dấu hiệu chuyển sang SQL: phải "vật lộn" để LINQ sinh đúng câu SQL mình muốn. Xem SQL EF sinh ra bằng cách hạ `Microsoft.EntityFrameworkCore.Database.Command` xuống `Information` trong `Serilog:MinimumLevel:Override`.

**Audit & xoá mềm:** entity cài `IAuditableEntity` được tự điền `created_at/by`, `updated_at/by`. Entity cài `ISoftDeletable` thì `Remove()` chỉ bật `is_deleted`; query EF tự lọc (muốn thấy hết dùng `IgnoreQueryFilters()`).

**Transaction:**

- Mặc định chỉ cần **một** `unitOfWork.SaveChangesAsync()` cuối handler — EF tự gói mọi INSERT/UPDATE/DELETE đang track vào một transaction.
- Cần transaction tường minh (gọi `SaveChanges` nhiều lần, trộn `ExecuteUpdate`/SQL thô, đổi isolation level) thì dùng `IUnitOfWork.ExecuteInTransactionAsync`:

```csharp
return await unitOfWork.ExecuteInTransactionAsync(async ct =>
{
    var order = Order.Create(...);              // tạo/nạp entity BÊN TRONG delegate
    orders.Add(order);
    await unitOfWork.SaveChangesAsync(ct);
    var reserved = await inventory.ReserveAsync(order.Id, ct);
    if (reserved.IsFailure) return reserved;    // Result thất bại → tự rollback, không cần ném exception
    await unitOfWork.SaveChangesAsync(ct);
    return Result.Success();
}, cancellationToken: cancellationToken);
```

| Tình huống | Hành vi |
|---|---|
| Delegate chạy xong | Commit |
| Ném exception | Rollback, exception đi lên nguyên kiểu |
| Trả `Result` thất bại | Rollback, trả nguyên `Result` |
| Gọi lồng nhau | Lời gọi trong nhập chung transaction ngoài; chỉ lớp ngoài cùng commit/rollback |
| Lỗi kết nối tạm thời | Chạy lại cả delegate với Change Tracker đã xoá (vì vậy entity phải tạo/nạp trong delegate) |

Không tự gọi `Database.BeginTransaction` trong code: EF đang bật `EnableRetryOnFailure`, transaction tự mở ngoài execution strategy sẽ bị EF từ chối. Delegate có thể bị chạy lại khi retry nên đừng gọi dịch vụ ngoài (gửi mail, gọi API…) bên trong — làm sau khi commit.

## Migration

Dữ liệu nghiệp vụ dùng `ApplicationDbContext`; module đăng nhập (`--auth local`) có `AuthDbContext` riêng. Có hơn một context thì mọi lệnh phải ghi `--context`:

```bash
# Dữ liệu nghiệp vụ
dotnet ef migrations add <Ten> --context ApplicationDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure -o Persistence/Migrations
# Module đăng nhập
dotnet ef migrations add <Ten> --context AuthDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure -o Auth/Migrations

dotnet ef migrations script --idempotent --context ApplicationDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure -o migrate.sql
dotnet ef migrations bundle --context ApplicationDbContext -p src/TicketHub.Infrastructure -s src/TicketHub.Infrastructure -o efbundle
```

**Development:** `Database:MigrateOnStartup=true` — app tự chạy migration lúc khởi động, kéo code mới về là DB đúng cấu trúc.

**Production:** `MigrateOnStartup=false`; migration là **một bước riêng trong CI/CD, chạy một lần, trước khi deploy bản app mới** (job `migrate` có sẵn, xem mục CI). Lý do:

1. **Quyền DB tối thiểu** — tự migrate thì tài khoản DB của app phải có quyền `CREATE/ALTER/DROP`; app bị khai thác (vd SQL injection) là lộ luôn quyền đó. Tách ra: app chỉ cần đọc/ghi dữ liệu, quyền sửa cấu trúc chỉ cấp cho bước migration.
2. **Migration lâu làm pod bị giết** — thêm index cho bảng lớn có thể mất vài phút; app chưa trả lời health check nên k8s khởi động lại pod, migration bị cắt ngang và deploy lặp mãi.
3. **Kiểm soát lỗi** — bước riêng thì lỗi là dừng deploy (bản cũ vẫn chạy), log rõ ràng, DBA duyệt được SQL trước; lẫn vào lúc khởi động thì khó biết DB đang dở ở đâu.

(EF Core 9+ đã khoá DB khi migrate nên nhiều pod không còn chạy chồng lên nhau — đó không còn là lý do.)

Hai cách đóng gói migration để chạy ở bước đó:

| | `efbundle` (job CI dùng cách này) | `migrate.sql` |
|---|---|---|
| Sinh bằng | `dotnet ef migrations bundle` | `dotnet ef migrations script --idempotent` |
| Là gì | File chạy được chứa sẵn mọi migration | File SQL thuần, đọc/sửa được |
| Chạy | `./efbundle --connection "<chuỗi kết nối>"` | `psql -f migrate.sql` |
| Hợp với | CI/CD tự động hoàn toàn | Có DBA duyệt SQL trước khi chạy |

Cả hai đều chỉ chạy migration còn thiếu (theo bảng `__EFMigrationsHistory`), chạy lại không sao. Có `AuthDbContext` thì phải đóng gói và chạy **cho cả hai context**.

## Cấu hình

Giá trị bí mật **không** nằm trong `appsettings.json`; đặt qua biến môi trường (`__` thay cho `:`).

| Khoá | Bắt buộc | Ghi chú |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | ✔ | PostgreSQL |
| `ConnectionStrings__Redis` | ✔ | |
| `JwtSettings__Secret` | ✔ | ≥ 32 ký tự; thiếu/ngắn → app dừng ngay khi khởi động |
| `JwtSettings__Issuer`, `__Audience` | | Mặc định `TicketHub` |
| `Cors__AllowedOrigins__0..n` | | Rỗng = chặn cross-origin |
| `RateLimiting__PermitLimit`, `__WindowSeconds` | | Mặc định 100 request / 60 s mỗi user hoặc IP |
| `Security__KnownNetworks__0..n` | | CIDR của reverse proxy tin cậy |
| `Security__UseHttpsRedirection` | | Mặc định tắt (TLS kết thúc ở ingress) |
| `OpenApi__Enabled` | | Bật tài liệu API ngoài Development |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | | Có thì mới bật OpenTelemetry |

## Test

```bash
dotnet test --solution TicketHub.slnx                         # tất cả (integration test cần Docker đang chạy)
dotnet test --project tests/TicketHub.UnitTests               # chỉ unit test
dotnet test --solution TicketHub.slnx --filter-class "*ProductsEndpointsTests"
```

## Build & quản lý gói

- Phiên bản gói khai báo **một chỗ** ở `Directory.Packages.props` (Central Package Management); `.csproj` chỉ ghi tên gói.
- `Directory.Build.props`: `net10.0`, nullable, analyzer mức Recommended, **warning = lỗi**. Tinh chỉnh luật trong `.editorconfig`.
- MediatR khoá ở 12.5.0 — bản MIT cuối; từ 13.x cần license thương mại.

## CI

Có sẵn `.github/workflows/ci.yml` và `.gitlab-ci.yml`: restore → build → quét gói lỗ hổng → test → build image → **migrate**. Giữ file của nền tảng bạn dùng, xoá file còn lại. GitLab cần runner bật `privileged` cho Docker-in-Docker (Testcontainers).

**Job `migrate`** (chỉ chạy trên `main`, sau khi build image thành công): đóng gói migration thành `efbundle` cho từng DbContext rồi chạy lên DB production. Mặc định **tắt** để dự án chưa có DB production không bị CI đỏ. Bật:

| | GitHub Actions | GitLab CI |
|---|---|---|
| Công tắc | Variable `RUN_DB_MIGRATIONS = true` | Variable `RUN_DB_MIGRATIONS = true` |
| Chuỗi kết nối | Secret `PRODUCTION_DB_CONNECTION` trong environment `production` | Variable `PRODUCTION_DB_CONNECTION` (Masked + Protected) |
| Duyệt tay trước khi chạy | Environment `production` → Required reviewers | Có sẵn (`when: manual`) |

Dùng tài khoản DB riêng có quyền sửa cấu trúc bảng cho job này; app chạy bằng tài khoản chỉ đọc/ghi dữ liệu. Job deploy (đẩy image, cập nhật manifest k8s) đặt **sau** `migrate` — khung mẫu có sẵn dạng comment trong cả hai file.
