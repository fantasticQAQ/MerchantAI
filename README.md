# MerchantAdmin AI 助手（独立项目）

给现有微服务接一个独立的 AI 层：**不动业务服务一行代码**，通过 HTTP + JWT 只依赖接口契约。

```
AI 前端 Vue3 (:5174，聊天 + 待确认卡片)
        ↓  HTTP
AI 后端 MerchantAI.API (:5100，Swagger + MCP)
   ├── Controllers/ChatController   POST /api/ai/chat、/confirm、/cancel
   ├── Harness/
   │     ├── StoreAgent              ChatCompletionAgent + 系统提示词 + 会话历史
   │     ├── StoreGuardFilter        IFunctionInvocationFilter：按 access 分级、审计、循环护栏
   │     ├── HumanApprovalService    写操作登记 / 确认后统一重放
   │     ├── ConversationStore       sessionId → ChatHistory（真实多轮，内存 / Redis 两种实现）
   │     ├── ChatHistorySerializer   ChatHistory 持久化（往返保真，见下）
   │     ├── PendingActionStore      待确认操作（存的是可重放的 HTTP 请求）
   │     ├── ToolCatalogReloader     tools.json 热加载
   │     └── AgentTraceService       JSONL 追加式轨迹，可回放
   ├── Ai/Tools/                     通用工具引擎
   │     ├── ToolCatalog             tools.json 加载 + 校验 + 与 swagger 合并
   │     ├── OpenApiToolSource       从 swagger 提取路径/参数骨架
   │     ├── ToolFunctionFactory     声明 → KernelFunction（动态生成）
   │     ├── HttpToolInvoker         参数 → HTTP → 解包 → 可读错误
   │     └── JsonSchemaLite          参数校验（把坏调用挡在审批之前）
   └── Mcp/StoreMcpServer            同一份工具目录的 MCP 入口（/mcp）
        ↓  HTTP + JWT
   现有微服务（完全不动）Identity.API :5001 / MerchantAdmin.API :5002 / Payment.API :5003
```

## 一、核心原则：一处声明，多处消费

**`tools.json` 是「存在哪些工具、叫什么、干什么、是读还是写、怎么落到 HTTP」的唯一事实来源。**
六个消费方都从这里取，谁都不再硬编码工具名：

| 消费方 | 取什么 |
|---|---|
| `ToolFunctionFactory` | 工具名 / 描述 / 参数 Schema → 注册给模型的函数 |
| `StoreGuardFilter` | `access` → 放行 / 拦截审批 / 禁用 |
| `PendingSummaryRegistry` | `summaryFormatter` → 待确认卡片的业务摘要 |
| `HttpToolInvoker` | `http` + `request` → 实际发出的请求 |
| `ToolsController`（dev） | 全量清单 + 还没暴露的后端接口 |
| `StoreMcpServer` | 同一份清单 → MCP tools |

**加一个业务能力 = 在 `tools.json` 里加一条**。过滤器、控制器、审批服务、审计、MCP 一行都不用改。

其中的关键约束，都是实测踩出来的：

- `access` 是**必填**且没有默认值。缺了直接启动失败——因为「新加写工具忘了加拦截」是安全漏洞，而字符串比较漏改既不报错也不编译失败。
- `forbidden` 的工具**不注册给模型**（模型看不到也调不到），而不是注册了再拦。
- 参数描述必须内联在 `schema` 里。`KernelParameterMetadata.Description` 会被 OpenAI 连接器忽略（已实测），所以 `ToolCatalog` 加载时把参数级 `description`/`default` 自动注入 schema。

## 二、tools.json 怎么写

```jsonc
{
  "defaults": {
    "baseUrl": "http://localhost:5002",
    "timeoutSeconds": 30,
    "response": {                      // 业务接口统一包装 { code, message, data, success }
      "successPath": "success",
      "messagePath": "message",
      "codePath": "code",
      "dataPath": "data"
    }
  },
  "discovery": {
    "swaggerUrl": "http://localhost:5002/swagger/v1/swagger.json"
  },
  "tools": [
    {
      "name": "list_products",
      "description": "查询商品列表……",   // 这段就是发给模型的工具描述，值得认真写
      "access": "read",                   // read | write | forbidden（必填）
      "from": "GET /api/Products",        // 引用 swagger 的一条操作，路径/参数骨架自动取
      "parameters": [                     // overlay：只写想覆盖/补充的部分
        { "name": "page", "description": "页码，默认1", "default": 1 }
      ]
    },
    {
      "name": "create_order",
      "description": "……（写操作，需人工确认）",
      "access": "write",
      "from": "POST /api/Orders/create",
      "summaryFormatter": "orderItems",   // 待确认卡片怎么显示
      "parameters": [
        {
          "name": "orderItems",
          "required": true,
          "description": "订单项数组，每项必须包含 productId 与 quantity",
          "schemaMode": "replace",        // 丢掉 swagger 里不想要的字段
          "schema": {
            "type": "array", "minItems": 1,
            "items": {
              "type": "object",
              "properties": { "productId": { "type": "integer" }, "quantity": { "type": "number" } },
              "required": ["productId", "quantity"]
            }
          }
        }
      ],
      "request": {
        "body": {                         // $map 把模型传的数组投影成后端契约要求的形状
          "orderItems": {
            "$map": "orderItems",
            "item": { "productId": "$productId", "quantity": "$quantity", "productName": "", "price": 0 }
          }
        }
      }
    }
  ]
}
```

字段速查：

| 字段 | 说明 |
|---|---|
| `name` | 模型看到的工具名。字母/数字/下划线/连字符 |
| `access` | `read` 直接执行；`write` 登记待确认；`forbidden` 不注册 |
| `from` | 引用 swagger 操作（`"GET /api/Products"`）。路径和参数骨架自动取 |
| `http` | 不走 `from` 时手写路径：`{ "method": "GET", "path": "/api/x" }` |
| `parameters[].schema` | JSON Schema。`{param}` 插值成字符串，`$param` 取原始值 |
| `parameters[].schemaMode` | `merge`（默认，与 swagger 深合并）/ `replace`（整份替换） |
| `request.query` | 哪些参数进 querystring；不写且无 body 时默认全部 |
| `request.body` | body 模板。`"$param"` 整块替换、`"{param}"` 插值、`{"$map": ...}` 数组投影 |
| `summaryTemplate` | 待确认卡片摘要的模板，如 `"取消订单 #{orderId}"`。简单写操作写一句就行，不用写 C# |
| `summaryFormatter` | 摘要格式化器的名字（需要复杂排版时才用） |

### 数组投影 `$map`

模型只负责传它知道的字段，其余由模板补齐：

```jsonc
"orderItems": {
  "$map": "orderItems",                        // 源数组参数
  "item": { "productId": "$productId", ... }   // 逐元素模板，{field}/$field 指向当前元素
}
```

> 为什么需要它：`CreateOrderCommand` 里的 `OrderItemDto` 是 `record(int ProductId, string ProductName, decimal Price, decimal Quantity)`，
> `string ProductName` 在 `<Nullable>enable</Nullable>` 下被 ASP.NET Core 当成**隐式必填**，不传就 400。
> 而 `CreateOrderCommandHandler` 其实完全忽略 `ProductName`/`Price`（它用 `order.AddOrderItem(product, quantity)` 从库里取）。
> 业务服务不能动，所以在模板层用 `$map` 把这两个字段补成空值。

### 当前工具清单

**给模型用的（12 个）** —— 全部由 `tools.json` 声明，代码里没有任何工具名硬编码：

| 工具 | 分级 | 对应接口 | 说明 |
|---|---|---|---|
| `list_products` | read | `GET /api/Products` | 查商品 |
| `list_orders` | read | `GET /api/Orders` | 查订单 |
| `get_dashboard` | read | `GET /api/Dashboard` | 经营统计 |
| `list_logs` | read | `GET /api/Logs` | 操作日志 |
| `create_product` | write | `POST /api/Products` | 新建商品 |
| `update_product` | write | `PUT /api/Products/{productId}` | 改名/改价/调库存/上下架 |
| `delete_product` | write | `DELETE /api/Products/{productId}` | **物理删除，不可恢复** |
| `create_order` | write | `POST /api/Orders/create` | 下单（扣库存） |
| `cancel_order` | write | `POST /api/Orders/{orderId}/cancel` | 取消（回补库存） |
| `pay_order` | write | `POST /api/Orders/{orderId}/pay` | 发起支付 |
| `refund_order` | write | `POST /api/Orders/{orderId}/refund` | 退款（回补库存） |
| `delete_order` | write | `DELETE /api/Orders/{orderId}` | 软删除，仅终态可删 |

**刻意不暴露的（4 个）**：

| 接口 | 为什么不给模型 |
|---|---|
| `GET /api/Orders/export` | 返回的是 CSV **文件流**，不是结构化数据。当工具用只会把 CSV 灌进上下文、还会被截断；数据需求用 `list_orders` 更合适。 |
| `POST /api/Test/setRedisKey` | 自测用接口（能直接写 Redis） |
| `GET /api/Test/getRedisKey` | 自测用接口 |
| `POST /api/Test/log` | 自测用接口 |

这份清单不用人工维护：启动日志会打印「尚未暴露的后端接口」，配完对着看即可。

### 加一个新工具要改几处

**一处**（`tools.json`）。需要复杂摘要排版时再加一个 `IPendingSummaryFormatter`（会被自动发现，不用改 DI 注册）。

> 新增工具时的两个经验：
> 1. **参数名要管**：swagger 推导出来的名字不一定适合模型。比如 `POST /api/Products` 的请求体只有一个 `productDto` 属性，
>    摊平后参数就叫 `productDto` —— 对模型很不友好。这种情况就**不要用 `from`**，改成手写模式，
>    把路径、参数、body 模板都自己声明，参数名就叫 `name`/`price`/`stock`。手写模式是完全受支持的一等公民。
> 2. **别给模型两件看起来一样的功能**：`delete_product`（物理删）和 `update_product`（下架）在语义上都能让商品消失，
>    所以两者的描述里都写明了「该用哪个、不该用哪个」，避免模型选错。

## 三、Harness 四件套

### 1. 分级与拦截（`StoreGuardFilter`）

所有工具调用的唯一收口。按 `access` 分派：`forbidden` 直接拒绝；`write` 交给 `HumanApprovalService` 登记并**短路**（绝不在这里执行）；`read` 放行。

### 2. 人工确认（`HumanApprovalService`）

**待确认操作里存的是一份可直接重放的 `HttpRequestPlan`（方法/路径/query/body），不是「工具名 + 弱类型字典」。**
所以确认执行时不需要任何 `if (FunctionName == "xxx")` 分支，加写工具也不用改审批代码。

几个刻意的取舍：

- 参数校验不通过时**绝不登记**，直接把错误回给模型。否则会生成一张内容为空的确认卡片，用户点了才报错（这正是最初的 bug 之一）。
- 业务接口「已送达」就消费掉待确认记录，避免用户重复点击造成重复下单；只有服务未启动/超时（没送达）才保留，让用户恢复后能重试。
- 待确认记录 30 分钟过期清理。

### 3. 审计轨迹（`AgentTraceService`）

append-only，记录 `user_input` / `tool_pre` / `tool_post` / `approval_pending` / `approval_invalid` / `loop_guard` / `answer`。出问题可以直接回放「模型当时传了什么、系统怎么处理的」。

**两处同时落**，各自可关：

| 目标 | 键 / 路径 | 过期 |
|---|---|---|
| Redis | LIST `merchant-ai:trace:{yyyyMMdd}` + SET `merchant-ai:trace:dates` | **无（永久保留）** |
| 本地文件 | `traces/trace-yyyyMMdd.jsonl` | 不变 |

读取优先走 Redis；某一天在 Redis 里没有内容时自动回退到文件——覆盖「切到 Redis 之前写下的老轨迹」和「本机根本没连 Redis」两种情况。
`AvailableDates` 是两者的并集。关掉某一侧：`Ai:Trace:Redis` / `Ai:Trace:File`。

> 两个踩过的坑：
> 1. 文件路径原本在**构造时**算一次。服务是单例，跑过午夜之后第二天、第三天的轨迹会一直追加到启动那天的文件里，
>    按日期筛选就再也对不上了（实测 `trace-20260910.jsonl` 里躺着 09-11 的记录）。现在每次写入现算。
> 2. 写轨迹用的是 `JsonText.Relaxed`。默认的 `System.Text.Json` 会把中文转义成 `\uXXXX` ——
>    审计是给人看的，转义了就白记了。

### 4. 会话历史（`ConversationStore` + `StoreAgent`）

之前每轮都是全新的单轮调用，所谓「多轮」全靠模型每轮重新查一遍数据撑着——用户说「给上面的商品下单」时，「上面」其实并不在上下文里。

现在 `sessionId → ChatHistory`，同一会话串行执行（防止用户连点两次发送写乱历史）。

**历史必须压缩，而且压法很讲究**（`ChatHistoryCompactor`）：批量任务每轮产生「1 条带 `tool_calls` 的助手消息 + N 条工具结果」，
60 次调用就是 62 条消息。最早的裁剪逻辑是「裁到 40 条，再把开头的孤儿 tool 消息删掉」——
裁剪点落在一批工具结果中间时会切出孤儿 tool 消息，紧接着被**整段删光**。
实测把一个 12631 字符 / 31 条消息的会话裁到只剩 **1 条**，标题退化成「（空会话）」，
模型彻底丢失进度，于是出现编号跳号、「补上测试商品120」这类胡编。

现在的做法是**先丢工具脚手架、再按条数裁剪**：

1. 丢掉所有 `Tool` 消息；助手消息上挂的 `FunctionCallContent` 也摘掉（只留文字）。摘完变成空壳的助手消息整条丢掉——空消息会被 OpenAI 拒绝。
2. 此时历史里只剩「用户消息 / 助手文本」，再怎么裁都不可能产生孤儿工具结果。

代价是**工具明细不再进会话历史**（它留在审计轨迹里），所以模型跨轮只能看到自己写的总结。
这直接决定了系统提示词的第 12 条：每轮收尾必须写成「目标总数 / 已完成 / 还差多少 / 本轮编号区间 / 下一轮从哪接着做」，
否则续跑的那一轮必然重号或漏号。

## 四、MCP 入口

同一个工具目录再开一个 MCP server（Streamable HTTP，端点 `/mcp`）：

- 只读工具直接执行
- 写工具**只登记待确认**并返回登记编号（`actionId`）
- 额外提供 `confirm_pending_action`，用户明确同意后再提交登记编号

两条入口（聊天 / MCP）共用同一套 `HumanApprovalService` 和审计轨迹，HITL 语义一致。用 `Ai:Mcp:Enabled` 关掉。

## 五、安全边界

`OpenApiToolSource` **只生成骨架，绝不自动注册工具**。真实 swagger 里有 16 个接口，其中包括：

```
DELETE /api/Products/{productId}   DELETE /api/Orders/{orderId}
POST   /api/Products               POST   /api/Orders/{orderId}/refund
POST   /api/Test/setRedisKey       POST   /api/Test/log
```

如果按「自动把 swagger 全部暴露成工具」来做，这些会直接变成模型可调用的工具。

**哪些接口允许模型调用、叫什么名字、是读还是写，必须由人写进 `tools.json`。**
配置错误一律**启动即失败**并给出可读报错：

- 缺 `access` → 报错（不允许默认放行）
- `from` 引用不存在的操作 → 报错，并提示你是不是想要某几个
- `/api/Orders/create` 的参数从 `orderItems` 改名成了 `items` → 报错列出该操作真实参数，直接指出契约漂移
- swagger 取不到（业务服务没启动）→ 退回上次的 `.swagger-cache.json`，避免 AI 服务起不来

启动日志会列出「有哪些后端接口还没暴露给模型」，对着它补 `tools.json` 即可。开发环境还可以 `GET /api/ai/tools` 看完整清单。

### 鉴权与用户隔离

`/api/ai/*` 与 `/mcp` 全部要求登录。JWT 参数与业务服务完全一致（同一 issuer/key），
浏览器端由 AI 后端做登录代理（`POST /api/ai/login`）——因为直连 Identity 会被它的 CORS 挡掉，而改业务服务的 CORS 又违反「业务服务不动」。

**四层，缺一不可**：

| 层 | 做法 | 不做的后果 |
|---|---|---|
| 1. 认证 | `[Authorize]` + JWT 校验；MCP 端点同样 `RequireAuthorization()` | 任何人都能调 |
| 2. 会话隔离 | 会话按 `{userId}:{sessionId}` 分命名空间；列表/详情只返回自己的 | 能看到别人的对话 |
| 3. 审批归属 | 待确认操作记 `UserId`，确认/取消时校验 | 能替别人点确认，执行别人登记的下单/删除 |
| 4. **工具角色** | `tools.json` 的 `roles` 限制哪些角色能用 | **任何登录用户都能借 AI 拿到管理员权限** |

第 4 层是最容易被忽略的：**AI 后端调业务接口时用的是配置里的服务账号 token**（当前是 Admin），
不是调用者本人的。不做角色限制的话，一个 Operator 只要跟 AI 说「删掉商品 5」，
AI 就会用管理员身份替他删掉。所以 `tools.json` 里：

```jsonc
{
  "name": "delete_product",
  "access": "write",
  "roles": ["Admin", "SuperAdmin"],   // 与后端控制器的 [Authorize(Roles=...)] 保持一致
  ...
}
```

`roles` 不写 = 任何登录用户都能用（只读工具都是这样）。

**实测结果**（`fantastic` = 管理员，`operator1` = Operator）：

```
不带 token                    → 401
MCP 不带 token                → 401
非管理员调 delete_product      → "工具 delete_product 需要 Admin/SuperAdmin 权限，当前账号没有"
管理员调 delete_product        → 正常登记待确认
非管理员确认管理员的待确认操作  → "无权确认其他用户登记的操作"
非管理员看到别人的会话          → 看不到（连"存在但不属于你"都不透露，直接当作不存在）
非管理员的审计轨迹             → scope=mine；管理员的 → scope=all
```

两个刻意的取舍：

- **同一个 `sessionId` 在不同用户下是两个会话**。`sessionId` 是客户端生成的，同一个浏览器换账号登录时
  localStorage 里还留着上一个人的 id —— 按用户分命名空间就不会撞车，也不用给用户抛一个莫名其妙的 403。
- **加鉴权之前的旧会话/旧轨迹不再对普通用户可见**。它们没有归属信息，无法判定是谁的 ——
  宁可看不见，也不能让所有人看见。管理员仍可在审计里看到旧轨迹。
- 会话详情/删除对别人的会话返回 **404 而不是 403**，不泄露「这个会话存在但不属于你」。

> ⚠️ 本实现**没有**做业务服务那套 SecurityStamp 校验（改密码/停用后旧 token 立即失效）。
> 原因是复用那套逻辑就要引用 `MerchantAdmin.Shared.Authentication`，会把 AI 项目绑到主解决方案上，
> 破坏「独立项目、只依赖接口契约」。要补的话：按接口契约去调 Identity 的
> `/api/internal/users/{id}/security-info`（带 `X-Internal-Key`），拿 SecurityStamp 比对即可。

### AI 权限级别（输入框左下角的下拉框）

思路借鉴 DSH 的沙箱模式，三级：

| 级别 | 写操作 | 适用 |
|---|---|---|
| **仅可查看** `readonly` | 一律拒绝，连卡片都不生成 | 只想问数据、演示、给别人看 |
| **需确认** `approve`（默认） | 登记为待人工确认 | 日常使用 |
| **完全权限** `full` | **直接执行，不弹确认** | 批量操作（一次建 50 个商品这种） |

由前端下拉框决定，随每次请求传给 `/api/ai/chat`（`mode` 字段），默认 `approve` ——
**漏传参数或写了认不出来的值都会退回 `approve`，不会变成无确认执行**。

> **前端用的是 Element Plus**（和商户前端 `src/MerchantAdmin.Frontend` 保持一致）：
> `el-segmented` 做页签、`el-select` 做权限下拉、`ElMessageBox` 做确认框、`el-timeline` 做轨迹时间线、
> `el-scrollbar` / `el-tag` / `el-empty` / `el-alert` 各管一摊。
>
> 一开始是纯手写 CSS 的。手写那版有两个绕不过去的问题：
> 1. **原生 `<select>` 的弹出列表是操作系统画的**，CSS 完全管不到，永远是直角 + 系统默认字体的白底方框，和界面割裂；
> 2. 确认框、时间线、滚动条都要自己画一遍，风格很难统一。
>
> 现在只有聊天气泡和 Markdown 渲染还是手写的（没有组件库管这个）。
>
> 代价是包体从 59 KB gzip 涨到 367 KB —— 全量引入了 Element Plus。
> 这是个跑在本机的内部工具，没必要为省几百 KB 去配 `unplugin-vue-components` 按需引入；
> 真要优化的话，装上那两个 unplugin 就行。

**分层很重要**：权限级别只决定「写操作要不要人工点确认」，
**不决定「这个人有没有资格用这个工具」**。后者由 `tools.json` 的 `roles` 单独把关，
所以在「完全权限」下，普通操作员依然调不动 Admin 专属的工具。两者是正交的。

几个刻意的处理：

- **切到「完全权限」会弹一次确认框**（前端），对话区顶部常驻一条红色提示条 —— 避免忘了自己开着什么。
- **每次直接执行都记审计**：`tool_auto_executed` 事件，带参数。事后能查「哪些操作没经过确认」。
- **必须告诉模型当前是什么权限**（`AgentPermission.Describe`）。不给的话，它在完全权限下
  照样会按系统提示词里的说法，把**已经执行成功**的操作汇报成「已登记，待人工确认，尚未执行」——
  那就是在说假话，而这个项目存在的意义之一正是防这个。这段说明是**本轮临时注入**的，
  用完就摘掉，不会残留在历史里误导下一轮。

### 批量任务的上限

原来一轮只能登记 10 条、调用 12 次工具，于是「创建 100 个商品」被劈成 9 轮，用户还得反复打「继续」。
现在的数字：

| 配置 | 值 | 说明 |
|---|---|---|
| `Ai:Agent:MaxToolCallsPerTurn` | **60** | **这才是批量任务的真正瓶颈**。审批上限提到 50 而这里还是 12 的话，第 12 个就会被砍掉。60 = 50 次写 + 若干只读查询 |
| `Ai:Agent:MaxApprovalsPerTurn` | **50** | 与服务端分页上限一致 |
| 只读工具默认 `pageSize` | **50** | 与业务接口分页上限一致 |
| `Ai:Agent:MaxContinuationRounds` | **3** | 一次 HTTP 请求内，后端最多自动接着跑几轮 |
| `Ai:Agent:TurnTimeoutSeconds` | **300** | 单轮总超时。必须装得下整整一批 60 次写（实测一批约 90 秒），120 秒会在半路砍断 |

**上限必须告诉模型。** 之前没告诉它，用户问「上限是多少」时它只能瞎猜——
实测原话是「我这边并没有一个可精确报给你的固定数字……大约 70 上下，不敢当成准确答案」。
典型的「因为不知道所以过度谨慎」。现在每轮会明确告诉它具体数字（`StoreAgent.BuildTurnContext`）。

### 「完全权限」下的自动续跑：为什么必须两层

完全权限没有确认环节，没有任何东西能把被截断的任务推起来——之前 100 个商品卡在第 60 个就只能等用户说「继续」。

**第一层（后端）**：`/chat` 检测到本轮被截断就自动接着跑，直到做完或跑满 `MaxContinuationRounds`。

> ⚠️ 这里踩过一个坑：最初判断「被截断」只看 `AgentLoopState.TruncatedKey`，
> 而那个标志**只在第 61 次调用被拒时才置位**。模型如果自己数着次数、做完第 60 个就直接总结收尾
> （实测它就是这么干的），标志永远不会置位，自动续跑**静默失效**。
> 现在按实际用量判断：`TruncatedKey == true || 本轮工具调用数 >= MaxToolCallsPerTurn`。

**第二层（前端）**：`MaxContinuationRounds` 再大也不够——「建 1000 个商品」≈ 17 轮 × 60 次调用，
全塞进一个 HTTP 请求里会跑到浏览器/代理超时，而且中间没有任何反馈。
所以后端跑满预算后返回 `hasMore: true`，前端拿同一个会话自动再发一次（`autoContinue: true`，后端改用续跑指令、不留用户消息），
直到 `hasMore: false` 或出现待确认卡片。

- 每轮都是独立的短请求，不会把单个请求拖到超时；
- 界面上有「自动继续 · 第 N 轮」的提示，用户看得见进度；
- 前端另有 `MAX_AUTO_ROUNDS = 40` 兜底，防止后端逻辑出错时无限循环。

**实测**（删掉 237 个测试商品）：第 1 轮 46 秒完成 228 个后返回 `hasMore=true`，第 2 轮 6 秒收尾，
模型还自己做了分页复核、修正了中途报错的累计数，最终 237 个全部删净、复核剩余 0，**用户 0 次「继续」**。

### 一个模型侧的坑：列清单会掉项

实测出现过「列出 65 个目标，却只发出 64 条删除」——工具全执行成功了，是模型自己漏了一个。
这类问题系统侧拦不住，只能在提示词里要求**批量操作后重新查询核对**：

> 批量操作（建/删/改 N 条）做完之后要重新查询一次核对：数一数实际结果和目标是 N 是否一致。
> 对不上就如实说明差在哪，不要直接宣布「全部完成」。

加了之后实测生效：模型会自己补一句「删除后复核查询，匹配的商品总数 = 0，核对一致」。

## 六、循环护栏与热加载

### AgentLoop 护栏

**Semantic Kernel 1.80 的 `FunctionChoiceBehavior.Auto` 没有任何「最多自动调用几轮工具」的上限**
（`FunctionChoiceBehaviorOptions` 里没有对应项），模型一旦陷进工具循环就会一直烧 token。
所以护栏只能在 `StoreGuardFilter` 里做——好在那里正好是所有工具调用的唯一收口。

| 配置 | 默认 | 作用 |
|---|---|---|
| `Ai:Agent:MaxToolCallsPerTurn` | 60 | 本轮工具调用次数上限，超过后回一条「请立刻基于现有信息作答」并拒绝继续调用 |
| `Ai:Agent:MaxApprovalsPerTurn` | 50 | 本轮最多登记多少条待确认操作。批量任务（「删掉全部订单」一次就 15 条）很常见，定得太死会逼出「分好几批 + 用户反复点继续」 |
| `Ai:Agent:TurnTimeoutSeconds` | 300 | 单轮对话总超时。超时后**已经执行完的写操作是真实生效的**，所以提示语不会再劝用户「把问题拆小一点」，而是告诉他说一句「继续」即可 |
| `Ai:Agent:AutoContinueAfterConfirm` | true | 见下 |
| `Ai:Agent:MaxContinuationRounds` | 3 | 单次 HTTP 请求内后端自动续跑的上限，见「批量任务的上限」 |

计数挂在 `HttpContext.Items` 上，按「一轮对话」隔离。

### 确认后自动续跑

**没有这个机制时**，多步任务是这样的：模型登记一批 → 用户点确认 → **模型停下** → 用户得再打一句「继续」，
才轮到下一步。15 条删除 + 5 条建单 + 状态调整，用户要打好几次「继续」。

现在：`POST /api/ai/confirm` 执行完之后，会**自动把模型推一步**——
把执行结果作为助手消息写进历史，再用一条合成指令（`AgentContinuation.Message`，带 `[自动继续]` 标记）
让模型接着做下一步，返回：

```jsonc
{
  "reply": "已成功执行 10 项：…",          // 执行结果
  "continuation": "全部完成，汇总如下…",     // 模型自动接着说的
  "pendingActions": [ … ]                  // 续跑中新登记的卡片
}
```

前端把两段都显示出来，新卡片直接可点。**少掉的只是中间的「继续」对话，人工确认这道闸门一个都没少。**

合成的那条消息在会话记录里会被过滤掉（`AgentContinuation.IsContinuation`），
免得看起来像是用户自己说的。

实测（就是上面那个任务）：**15 个写操作，用户只打了 2 句话，0 次「继续」。**

关掉它：`Ai:Agent:AutoContinueAfterConfirm=false`，或在单次请求里传 `"continue": false`。

> 为什么把 `MaxApprovalsPerTurn` 从 10 放宽到 50：原来是 10，结果「删掉 15 张订单」被劈成两批、
> 中间还要用户说一句「继续」——限制本身防的是模型失控，但真正的闸门是人工确认，
> 这个数字定得太紧只会制造摩擦。

### tools.json 热加载

`FileSystemWatcher` + 600ms 防抖，改完保存即生效，**不用重启进程**：

- `ToolCatalog` 用不可变快照 + volatile 字段，热加载整体替换，读方永远看到自洽的一份视图。
- 消费方持有的是同一个 `IToolCatalog` 引用，所以过滤器/审批/执行器/MCP 自动看到新工具。
- Kernel 里的插件会同步换掉（`Plugins.Remove` + `Add`），否则模型还看着旧工具。
- **改坏了不会把服务带走**：新配置校验不过就保留旧快照并打错误日志，修好再保存即可。
- `Ai:Tools:HotReload=false` 可关掉。

## 七、会话与待确认操作的持久化

默认是内存实现（重启即丢、多实例不共享）。配一行 `Ai:Redis:ConnectionString` 即可切到 Redis：

```json
"Ai": { "Redis": { "ConnectionString": "localhost:6379,abortConnect=false" } }
```

切过去之后：

- **重启不丢上下文** —— 关掉进程再起来，继续说「上面那几个商品」模型依然接得住
- **多实例可确认** —— 模型在 A 实例登记、用户点确认打到 B 实例也能执行（内存实现会报「待确认操作不存在或已失效」）
- **会话默认永久保留**（`Ai:History:TtlMinutes = 0`，即 `SET` 不带过期时间）。审计轨迹同理，写进 Redis 且不设 TTL。
  想恢复自动过期的老行为，把 `TtlMinutes` 配成正数即可 —— 下次写入会用新的过期时间覆盖掉旧 TTL
  （Redis 的 `SET` 不带 `KEEPTTL` 时会清掉原有 TTL）
- 待确认操作仍然是短 TTL（`Ai:Approval:TtlMinutes`，默认 30 分钟）：它本来就只该活到用户点完为止
- 跨实例并发改同一会话时有 Redis 锁保护（同进程内另有信号量）

**连不上就自动退回内存**，并在启动日志里明确说明用的是哪种 —— 地址写错不该让服务起不来，但也不能让人以为在用 Redis。
（注意判断可用性必须真的 ping 一次：为了「Redis 没起来也不崩」，连接配了 `AbortOnConnectFail=false`，这种情况下 `Database` 一样拿得到，只看它是否为 null 会误判。）

### 为什么这件事需要先验证再动手

`ChatHistory` 里混着 SK 的专有类型（`FunctionCallContent` / `FunctionResultContent`），
**序列化还原不完整会丢掉工具调用序列，OpenAI 会直接拒绝整个请求**。有两个坑：

1. SK 的 `FunctionCallContent.Arguments` 是**解析视图**，会把 JSON 数字变成字符串（模型传的 `{"page":1}` 在里面是 `"1"`）。
   照它写盘，还原后发出去的 `tool_calls` 就变成 `{"page":"1"}` —— 与原始请求不再等价。
   真正的原始 JSON 在 provider 对象里（OpenAI SDK 的 `ChatToolCall.FunctionArguments`）。
2. 直接 `JsonSerializer.Serialize(history)` 也不安全：`Metadata` 里混着 OpenAI SDK 的 `Usage`/`FinishReason`，
   反序列化后只剩 `JsonElement`，语义丢失。

所以有了 `ChatHistorySerializer`：显式的 wire format，只持久化 role + 三种 item（文本 / 函数调用 / 函数结果），
带版本号，不认识的内容类型一律丢弃并计数。

它的正确性由 `ChatHistorySerializerTests` 锁住，而且**比对的是实际发出的 HTTP 请求体是否逐字节一致**，
不是比对象也不是比字段 —— 这是唯一能证明「还原后模型看到的上下文没变」的方法。

## 八、会话记录与审计（怎么看）

数据一直是写进去的，但**没有任何地方能看** —— 会话在 Redis 里、轨迹在 JSONL 里，全靠 CLI 或者翻文件。
现在补了读取接口，前端也重做成了「左栏 + 两个页签」。

### 界面结构

```
┌──────────────────────────────────────────────────────────────┐
│ 商户 AI 助手      [ 会话 | 轨迹 ]            fantastic·Admin  │  ← el-segmented 页签
├───────────────────┬──────────────────────────────────────────┤
│ 会话        ＋新建 │  对话 / 轨迹（由上面的页签决定）           │
│ ─────────────────  │                                          │
│ ▸ 有哪些商品？     │   …                                      │
│   09-11 01:19·4条  │  ┌────────────────────────────────────┐  │
│   12 轨迹          │  │ 输入你的问题…                       │  │
│ ▸ （会话已删除）   │  │ [● 需确认 ▾]  Enter 发送    [发送]  │  │  ← 权限下拉在输入框左下角
│   仅剩轨迹 · 9 轨迹│  └────────────────────────────────────┘  │
└───────────────────┴──────────────────────────────────────────┘
```

- **左栏永远是会话列表**，两个页签共用 —— 因为轨迹也是**按会话分**的。
  每条会显示消息数 + 轨迹条数 + 未处理的待确认数。
- **顶部两个页签**（`el-segmented`）切换右侧显示这个会话的「对话」还是「轨迹」。
- 轨迹是 **`el-timeline` 时间线**（从早到晚），每条带颜色区分类型（输入 / 工具 / 审批 / 告警 / 回答），
  点 payload 可以展开看完整 JSON。
- **整页铺满，没有 max-width**。这是个后台工具，宽屏上就该把对话和表格铺开，
  而不是缩在中间一条 800px 的窄条里。

### 接口

| 接口 | 作用 |
|---|---|
| `GET /api/ai/sessions?limit=50` | 会话列表：标题、消息数、**轨迹条数**、待确认数、最后活跃时间。**只返回自己的**，隐藏过的不返回 |
| `GET /api/ai/sessions/{id}` | 某个会话的完整记录：对话内容 + **未处理的确认卡片**。别人的 / 隐藏过的都返回 404 |
| `DELETE /api/ai/sessions/{id}` | 删除会话（**连同它的轨迹**）。软删除，Redis 里一条不删 |
| `GET /api/ai/traces?sessionId=&limit=` | **某个会话自己的轨迹**（跨天合并），**按时间正序**。前端轨迹页用它 |
| `GET /api/ai/traces?date=` | 某一天的全部轨迹，倒序。运维视角 |
| `GET /api/ai/traces/sessions?date=` | 按会话聚合的轨迹概览（事件数 / 工具调用数 / 审批数 / 被拦截数） |

### 「删除」是软删除（`IVisibilityStore`）

**界面上不显示 ≠ 真的删掉。** 会话历史和审计轨迹都要求永久保留在 Redis 里，删掉就再也追溯不了；
但界面上总得能清理，不然列表越堆越长。所以删除动作只在 `IVisibilityStore` 里记一笔「别再展示」：

> **轨迹不能单独删**，只能跟着会话一起走。允许单独抹掉轨迹等于给审计链开了个后门，
> 还会留下「会话还在、但发生过什么没人知道」的空档。想清理就删会话，语义清楚。

| Redis 键 | 存什么 | 过期 |
|---|---|---|
| `merchant-ai:hidden:sessions` | `{userId}:{sessionId}`，整个会话连同它的轨迹都不展示 | 永久 |
| `merchant-ai:hidden:traces` | `{userId}:{sessionId}`，只藏轨迹，会话照常展示 | 永久 |

- **粒度按用户**：同一个 `sessionId` 在不同用户下本来就是两个会话（会话键带 userId），
  隐藏标记也必须带 userId，否则 A 隐藏会连 B 的一起藏掉。
- **隐藏名单自己不设过期**：它要是过期了，删掉的东西会自己冒回来，那才是真的见鬼。
- **待确认操作是真删的**：它本来就只活到用户点完为止，留着反而可能被误执行。
- 想恢复：把对应集合里的成员去掉即可（数据一直都在，也随时能做「回收站」）。

> 这里返工过一次。最初的实现是「删会话时保留轨迹，并把它列成『（会话已删除）／仅剩轨迹』」——
> 本意是好的（怕轨迹没地方看），但实际用起来就是「会话明明删了，怎么还在列表里冒出来」。
> 现在改成：删会话就是把这条连同轨迹一起收走，干净利落。

### 会话的整理：重命名 / 置顶 / 分组

左栏每条会话鼠标浮上来会出现三点菜单（**重命名 / 置顶 / 删除**）；分组标题上也有一个（**在此新建会话 / 重命名分组 / 置顶分组 / 删除分组**）。
置顶的会话从各自分组里拎出来，单独排在最上面；置顶的分组也排在其他分组前面。

| 接口 | 作用 |
|---|---|
| `PATCH /api/ai/sessions/{id}` | 改名 / 置顶 / 换分组。只传要改的字段 |
| `POST /api/ai/groups` | 新建分组 |
| `PATCH /api/ai/groups/{id}` | 分组改名 / 置顶 |
| `DELETE /api/ai/groups/{id}` | 删除分组。**组里的会话不会删**，只是回到「未分组」 |

**这几项为什么不放在 `SessionMetadata` 里**：那份元信息是从对话内容推出来的
（标题取第一条用户消息、条数取 `history.Count`），每次保存都会整体重写。
用户设置放进去，下一轮对话就被冲掉了。所以单独存一份 `ISessionPrefsStore`：

| Redis 键 | 存什么 |
|---|---|
| `merchant-ai:prefs:{userId}` | 哈希，field = sessionId → `{title, titleIsAuto, pinned, groupId}` |
| `merchant-ai:groups:{userId}` | 分组数组 JSON |

两者都**不设过期时间** —— 置顶和分组要是自己过期了，用户的整理就白做了。

### 会话标题由模型总结，不是第一条消息

原来标题就是「第一条用户消息的前 24 个字」。问题是大家的第一句经常是「你好」「在吗」「有哪些商品？」，
列表里一排看下来完全分不清谁是谁。现在改成把这一轮对话喂给模型，让它概括成短标题
（实测：`帮我看看现在店里有哪些商品，库存够不够` → **查询店铺商品库存**）。

三条刻意的规则：

- **一条会话只生成一次**：第一次对话结束后起个默认标题，之后不管再聊什么都不改。
  标题是列表里的"目录"，来回变反而找不到东西 —— 实测第 2、3 轮换成完全不同的话题，
  标题仍保持第一次那个。
- **用户手动改过名就永不覆盖**（`TitleIsAuto = false`）。
- **失败一律不影响主流程**。起不出标题就退回「第一条用户消息」，
  而且生成前后都会再读一次偏好 —— 防止生成期间用户刚好重命名，被结果盖回去。

配置：`Ai:SessionTitle:Enabled`（默认 true）可整体关掉。

- **轨迹按会话存了第二份索引**（`merchant-ai:trace:s:{userId}:{sessionId}`）。
  只按天存的话，看一个会话的轨迹要把所有天都捞一遍再过滤，天数一多就没法用了。
  按天那份仍然保留，给运维视角和原始 dump 用。
  本次改动之前写入的老轨迹只有按天那份，读取时会**自动回填**到会话索引里（只往回看 14 天）。
- **刷新页面不丢现场**。前端把 `sessionId` 存在 `localStorage`，重开页面会自动拉回上一次的会话；
  会话详情里带着未处理的待确认操作，所以**确认卡片也能恢复** —— 否则刷新一次就再也点不了那张卡片了。
- **会话元信息跟本体存在一起**（`{meta, history}`），不再单独维护一个可能和本体脱节的索引。
  代价是列会话要 SCAN + 逐个读，这是「看记录」这种管理动作可以接受的量级。
  本次改动之前写入的旧会话（裸 `history` 格式）也能读出来，只是时间确实是未知的 —— 接口返回 `null`，页面显示"时间未知"，而不是编一个"现在"。
- 会话历史**不设过期时间**（`Ai:History:TtlMinutes=0`），审计轨迹在 Redis 里也是。
  历史条数上限 `Ai:History:MaxMessages=500` —— 因为工具脚手架每轮都会被压缩掉，留下的全是「用户消息 / 助手文本」，
  500 条足够放下很长的多轮对话（早先的 40 条在批量任务场景下会把用户消息挤出上下文，见「会话历史」一节）。

## 九、测试

```powershell
cd src
dotnet test                # 116 个用例
```

测试打的是**真实链路**（真 Catalog / 真执行器 / 真审批 / 真护栏），只把业务服务换成 `FakeMerchantApi`，
而且直接引用 API 项目里的真实 `tools.json` —— 配置写错了测试就会红，而不是等上线才发现。

覆盖的关键行为：只读直执行与响应解包、写操作只登记不执行、确认后按契约重放、参数非法绝不登记空卡片、
数组/数字被序列化成字符串时的还原、批量确认、业务失败也消费待确认、循环护栏、
热加载（含坏配置回滚）、重复加载不污染共享 swagger 骨架、会话序列化往返保真、
会话列表/详情、按会话查待确认、轨迹读取（含坏行跳过与旧格式兼容）。

Redis 相关用例用 `[SkippableFact]` 标记：**本机没起 Redis 就自动跳过**，不会因为外部依赖红掉，起了就一定会真跑。

## 十、本地起服务

业务服务已经部署在云上（`http://1.14.205.214:8080`），本地开发 AI 时**不用**再把它们跑起来 ——
`appsettings.Development.json` 里的地址已经指向云网关，登录、查数据都走真实的线上服务。

```powershell
# AI（也可以直接跑根目录的 start.ps1）
dotnet run --project src/MerchantAI.API                       # http://localhost:5100/swagger
cd src/MerchantAI.Frontend; npm run dev                       # http://localhost:5174
```

本机还需要一个 Redis（会话历史、待确认操作、审计轨迹都存这儿）。本机装的原生 Redis 就够，
不必用 Docker —— `Ai:Redis:ConnectionString` 默认指向 `127.0.0.1:6379`。

`appsettings.Development.json` 里需要维护两个值：

- `DeepSeek:ApiKey`
- `MerchantApi:ServiceToken` —— 给 AI 后端调业务接口用的 JWT。**它有有效期**（实测 2 天），
  过期后表现为「查询失败 / 工具全部报错」，不是代码 bug。重跑 `build\Testscripts\refresh-service-token.ps1`
  换一枚，然后重启后端。

注意云上那台 Nginx 会**替换**前缀（`/api/merchant/Products` → `/api/Products`），
所以 `MerchantApi:GatewayPrefix` 必须填 `/api/merchant`，否则请求会打到网关根路径、
拿回前端 SPA 的 `index.html`。

## 十一、部署到云上

见 `AI/build/README.md`。那里是完整的 compose 编排加一个 `deploy.ps1`：

```powershell
cd AI\build
.\deploy.ps1 init     # 从模板生成 .env，填上密钥
.\deploy.ps1 token    # 登录云上 Identity 换一枚业务 token 写进 .env
.\deploy.ps1 up       # 构建并启动
```

起来的是三个容器：`ai-frontend`（唯一入口，静态页 + 反代）、`ai-api`、`ai-redis`。
业务服务不打包在内 —— AI 始终只是它们的调用方。

## 十二、还没做

1. **SecurityStamp 校验**：见「鉴权与用户隔离」末尾 —— 改密码/停用后旧 token 在 AI 后端仍然有效直到过期。
2. **写操作的跨实例幂等**：现在靠「送达即消费」防重复点击。彻底解决需要业务侧支持幂等键（业务服务不动的话做不了）。
3. **审计轨迹换成可查询的存储**：现在按天写 JSONL 到 `bin/traces/`，读取是全文件扫描。
   量大后需要换库并加保留策略；会话列表的 SCAN 同理。
4. **`/api/Test/*` 这类调试接口建议从业务侧 swagger 里摘掉**，减少误暴露面。
5. **会话历史是快照，不是实时数据**：模型从历史里回答「库存还有多少」可能已经过时。
   涉及实时数据的问题应当触发一次工具调用 —— 目前靠提示词约束，没有强制机制。
6. **轨迹里的 token/耗时没记**：做成本分析和优化还用不了，需要补埋点。
7. **角色只做到了工具级**：同一工具内的数据范围（比如 Operator 只能看自己门店的订单）还没有 —— 
   那需要业务接口本身支持按人过滤，光靠 AI 侧做不到。


