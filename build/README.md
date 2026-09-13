# AI 助手 —— Docker 部署

这个目录只负责**部署 AI 助手本身**。商家后台的业务服务（Identity / Merchant / Payment / SQL Server / RabbitMQ）
已经独立部署在云上，AI 是它们的调用方，不打包在一起、也不需要改它们。

起来之后是三个容器：

| 容器 | 作用 | 对外 |
| --- | --- | --- |
| `ai-frontend` | Vue 静态页 + 反代 `/api/`、`/mcp` 到后端 | **唯一的入口**，宿主机 `HTTP_PORT`（默认 8090） |
| `ai-api` | .NET 8 后端，接模型、管会话、调业务接口 | 不对外，只在 `ai-network` 内 |
| `ai-redis` | AI 自己的会话 / 审计存储 | 不对外 |

> 业务侧的 Redis 和这个**不是同一个**。AI 的会话历史和审计轨迹不该混进业务库，
> 所以这里单独起一个；数据落在 `ai_redis_data` 卷里，`down` 不会删（只有 `clean` 会）。

---

## 一、前置条件

- 云服务器上装了 Docker（含 compose 插件）：`docker compose version` 能打印版本即可
- 云服务器能访问业务网关（默认 `http://1.14.205.214:8080`）
- 一个 DeepSeek API Key

---

## 二、第一次部署

### 1. 生成配置

```powershell
cd AI\build
.\deploy.ps1 init          # 从 .env.example 复制出 .env
```

然后编辑 `.env`，**只有这几项必须填**：

```ini
MERCHANT_SERVICE_TOKEN=    # 先留空，下一步自动填
DEEPSEEK_API_KEY=sk-...    # 模型密钥
SERVICE_PASSWORD=...       # 服务账号密码，用来换 token
```

其余默认值已经对上了当前云环境（网关地址、JWT issuer 等），不用动。

### 2. 换一枚业务 token

```powershell
.\deploy.ps1 token
```

它会用服务账号登录云上 Identity，把拿到的 JWT 写进 `.env`，然后重启后端。
**这枚 token 有有效期（实测 2 天）**，过期后 AI 查不到数据、工具全部报错 ——
重跑一次这条命令就行，不用改代码。

### 3. 起服务

```powershell
.\deploy.ps1 up
```

首次会构建两个镜像（后端 restore + 前端 npm ci，几分钟）。完成后：

```
http://<服务器地址>:8090
```

用商家后台的账号密码登录即可。

---

## 三、要放到别的机器上跑

如果不想把源码搬过去，在本机构建好、导出镜像再传：

```powershell
.\deploy.ps1 save          # 生成 ai-images.tar
```

把这三个文件拷到服务器同一个目录：

```
ai-images.tar
docker-compose.yml
.env
```

然后在服务器上：

```bash
docker load -i ai-images.tar
docker compose up -d
```

`.env` 里有密钥，**用 scp 传、别走公开渠道**。

---

## 四、常用命令

| 命令 | 作用 |
| --- | --- |
| `.\deploy.ps1 ps` | 看容器状态和访问地址 |
| `.\deploy.ps1 logs ai-api` | 跟踪后端日志（不加服务名则看全部） |
| `.\deploy.ps1 token` | ServiceToken 过期了，重新登录并热更新 |
| `.\deploy.ps1 up` | 重新构建并启动（改了代码后用这个） |
| `.\deploy.ps1 restart` | 只重启，不重建 |
| `.\deploy.ps1 down` | 停掉容器，**数据卷保留** |
| `.\deploy.ps1 sh ai-api` | 进容器排查 |
| `.\deploy.ps1 clean` | 停掉并**删除数据卷**（会话和轨迹全没，需输入 yes 确认） |

---

## 五、和业务网关的关系

AI 后端调业务接口，走的是云上那台 Nginx：

```
ai-api  →  http://<网关>/api/merchant/Products
                    ↓ nginx 把 /api/merchant 前缀换成 /api
           http://merchant-api:8080/api/Products
```

所以 `.env` 里 `MERCHANT_GATEWAY_PREFIX=/api/merchant` 不能省 —— 少了它请求会打到
网关根路径上，返回前端 SPA 的 `index.html`，表现为「工具报 JSON 解析失败」。

本地直连业务服务调试时才把它留空。

### 关于 swagger 缓存

云上生产环境关掉了 swagger，`tools.json` 里用 `from` 声明的工具解析不出参数骨架，
**服务会直接启动失败**。所以镜像里带了 `src/MerchantAI.API/.swagger-cache.json`。

注意这个文件在 `.gitignore` 里（当初把它当成了运行时产物）。如果你换成「在服务器上
`git clone` 再构建」的部署方式，它不会跟着源码过去，构建出来的镜像启动会失败 ——
那时把它一起拷过去，或者从 `.gitignore` 里去掉这条：

```
src/MerchantAI.API/**/.swagger-cache.json
```

用 `.\deploy.ps1 save` 导出镜像的方式部署则没有这个问题，文件已经烤进镜像了。

---

## 六、排查

**登录后所有接口 401**
`.env` 里的 `JWT_ISSUER` 必须和云上 Identity 实际签出来的 `iss` 完全一致。
实测云上签的是容器内地址 `http://localhost:5001`，不是公网地址。

**AI 说查不到数据 / 工具全报错**
大概率是 ServiceToken 过期了：`.\deploy.ps1 token`。

**前端打得开但接口 502**
`ai-api` 没起来或者还在启动中：`.\deploy.ps1 ps`、`.\deploy.ps1 logs ai-api`。

**改了 `tools.json` 不生效**
工具目录支持热加载，但容器里改文件需要先 `docker cp` 进去；改了仓库里的版本则要
`.\deploy.ps1 up` 重建。

---

## 七、端口冲突

服务器上已经有 nginx 占了 80/443 的话，别改 `docker-compose.yml`，改 `.env`：

```ini
HTTP_PORT=8090
PUBLIC_ORIGIN=https://ai.example.com
```

再让外层 nginx 反代到 `http://127.0.0.1:8090`。注意把超时时间也放大：

```nginx
location / {
    proxy_pass http://127.0.0.1:8090;
    proxy_read_timeout 3600s;
    proxy_buffering off;
}
```

批量任务（比如「建 1000 个商品」）会跑很久，默认 60 秒超时会让前端报「网络错误」，
而后端其实还在正常干活。
