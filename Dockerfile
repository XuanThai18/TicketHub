# syntax=docker/dockerfile:1

# Stage 1: build + publish bằng SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy file cấu hình dùng chung + các .csproj rồi restore, để tận dụng layer cache khi chỉ đổi code
COPY ["global.json", "Directory.Build.props", "Directory.Packages.props", ".editorconfig", "./"]
COPY ["src/TicketHub.Api/TicketHub.Api.csproj", "src/TicketHub.Api/"]
COPY ["src/TicketHub.Application/TicketHub.Application.csproj", "src/TicketHub.Application/"]
COPY ["src/TicketHub.Domain/TicketHub.Domain.csproj", "src/TicketHub.Domain/"]
COPY ["src/TicketHub.Infrastructure/TicketHub.Infrastructure.csproj", "src/TicketHub.Infrastructure/"]
RUN dotnet restore "src/TicketHub.Api/TicketHub.Api.csproj"

COPY src/ src/
RUN dotnet publish "src/TicketHub.Api/TicketHub.Api.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Stage 2: runtime tối giản, chạy bằng user không phải root
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
# Image aspnet không có curl; kiểm sống bằng probe của k8s (/health/live, /health/ready).
ENTRYPOINT ["dotnet", "TicketHub.Api.dll"]
