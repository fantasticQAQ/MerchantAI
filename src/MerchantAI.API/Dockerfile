# ========== 第一阶段：编译 ==========
# 构建上下文是仓库根目录（本项目不引用主解决方案里的任何项目，只依赖 NuGet 包）
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先只复制 csproj 再 restore：源码改动不会让依赖层失效，重建快很多
COPY src/MerchantAI.API/MerchantAI.API.csproj ./MerchantAI.API/
RUN dotnet restore ./MerchantAI.API/MerchantAI.API.csproj

COPY src/MerchantAI.API/ ./MerchantAI.API/

WORKDIR /src/MerchantAI.API
RUN dotnet publish -c Release -o /app/publish

# ========== 第二阶段：运行 ==========
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

COPY --from=build /app/publish .

# tools.json 已经由 csproj 的 CopyToPublishDirectory 带过来了。
# 但 .swagger-cache.json 没有声明，得手动带上 —— 云上生产环境关掉了 swagger，
# 没有这份缓存的话 tools.json 里的 from 引用解析不了，服务会直接启动失败。
COPY src/MerchantAI.API/.swagger-cache.json /app/.swagger-cache.json

# 容器里固定监听 8080，宿主端口由 compose 映射
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "MerchantAI.API.dll"]
