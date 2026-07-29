# Codex 全量代码推进指令：流程高并发稳定性改造

## 1. 执行目标

立即进入代码实施模式，基于现有源码、现有计划与任务清单完成 **S0–S5 全阶段改造**。

核心目标只有一个：

> 在高并发流程启动、待办查询、任务完成和末节点业务回调场景下，系统不得因同步阻塞、连接堆积、线程/连接池耗尽、ES 查询放大或下游慢响应而整体宕机。

本轮以**真实代码完成、真实测试通过、真实提交 GitHub**为最终标准，不以补充大量分析报告为目标。

现有文件：

```text
docs/2026-07-29-flow-runtime-data-and-callback-plan.md
docs/2026-07-29-flow-runtime-data-and-callback-task.md
docs/test-reports/2026-07-29-portal-pressure-test-report.md
```

先根据本指令修订现有 plan/task，然后直接实施。不要再次停留在纯评审阶段。

---

## 2. 工作方式

### 2.1 TDD执行

所有核心改造采用以下顺序：

```text
先写能够复现问题或约束行为的测试
→ 确认测试失败
→ 实现最小正确代码
→ 测试通过
→ 重构
→ 执行阶段验收
→ 提交Git
```

测试必须服务于当前任务验收，不得扩张成与高并发稳定性无关的测试工程。

### 2.2 自动阶段推进

Codex有权依据代码和测试结果自行裁决阶段是否通过。

```text
S0通过 → 自动进入S1
S1通过 → 自动进入S2
S2通过 → 自动进入S3
S3通过 → 自动进入S4
S4通过 → 自动进入S5
S5通过 → 完成最终验收和GitHub提交
```

不要在阶段之间等待人工确认。

只有遇到以下真实阻塞时才停止：

- 缺少项目无法替代的DM8驱动或运行依赖；
- 无法连接必要的真实测试服务；
- Git远程认证或权限确实失败；
- 现有外部业务系统不具备必须的接口契约，且代码侧无法兼容；
- 继续操作会破坏共享数据或不可逆影响生产环境。

遇到阻塞时必须保留已完成代码和测试，明确写出真实阻塞，不得伪造通过。

### 2.3 Git执行

- 每个阶段验收通过后创建一个语义清晰的提交；
- 提交前必须保证当前阶段测试通过；
- 完成S5后推送到现有GitHub远程；
- 不新建无关分支，不重写已有历史，不强制推送；
- 如果远程认证失败，保留全部本地提交并输出失败命令和真实错误；
- 不得声称已经推送但实际没有推送。

建议提交结构：

```text
test(flow): lock runtime baseline
feat(flow): add dm8 callback inbox
refactor(flow): move execution truth out of elasticsearch
perf(flow): paginate pending tasks before enrichment
perf(flow): add redis protection and backpressure
test(flow): complete high concurrency acceptance
```

---

## 3. 固定架构裁决

### 3.1 Flowable

保持：

```text
Flowable 7.2 + Flowable自管PostgreSQL
```

Flowable继续作为以下数据的唯一运行真相：

- 流程实例是否存在；
- 流程是否结束；
- 当前活动节点；
- 当前任务；
- assignee/candidate；
- 任务是否可以完成、认领、转派、驳回或终止。

禁止：

- 修改或直接写入Flowable `ACT_*` 表；
- 用DM8替换Flowable PostgreSQL；
- 在流程中心复制一套可写流程引擎；
- 使用ES、DM8或Redis直接替代Flowable实时校验。

### 3.2 Elasticsearch

ES不整体移除。

最终职责收缩为：

- 门户列表；
- 条件查询；
- 排序与筛选；
- 查询宽表；
- 展示状态；
- 可重建的流程定义和动作查询投影。

不得继续把以下数据只放在ES并作为最终判断依据：

- `businessId → processInstanceId`唯一绑定；
- 启动防重状态；
- 业务回调幂等状态；
- 回调成功、重试和死信状态；
- 影响完成、驳回、转派、Slot转换和业务路由的执行规则；
- 关键任务动作事实。

ES写入失败只能影响查询新鲜度，不能导致Flowable核心事务回滚或整个完成接口失败。

### 3.3 DM8

DM8作为流程映射服务的业务一致性数据库，承担：

- 流程与业务唯一绑定；
- 启动预留和最终防重；
- 流程定义版本化执行配置；
- 任务动作事实；
- 末节点Callback Transactional Inbox；
- 幂等；
- 租约；
- 有限重试；
- 死信；
- 人工重投；
- ES投影版本。

DM8不承载Flowable内部运行表，也不扩张成完整门户业务中台。

### 3.4 Redis

Redis只承担高并发保护和缓存：

- 启动削峰锁；
- API限流；
- Worker全局和下游并发控制；
- 流程定义/节点配置缓存；
- 必要时1–5秒热点查询缓存；
- 防击穿。

Redis不得作为：

- 流程真相；
- 业务状态唯一存储；
- 幂等最终依据；
- 回调事件唯一存储；
- 重试和死信唯一记录；
- DM8事务失败后的替代成功路径。

Redis故障时允许性能下降或绕过缓存，但核心正确性必须由Flowable和DM8维持。

---

## 4. 范围约束

### 必须完成

- 末节点同步业务回调持久化异步化；
- DM8业务绑定、Inbox、执行配置和动作事实模型；
- ES执行真相迁移与查询投影化；
- 待办查询先分页后补充；
- Redis保护层；
- 有边界的并发、超时、连接池和背压；
- 与当前问题直接相关的单元、集成和压力测试；
- 数据迁移、回滚脚本和必要对账；
- Git提交和推送；
- 最终简明验收文档。

### 禁止扩大

内网环境属于安全可控环境，本轮不扩张以下方向：

- 不建设mTLS、PKI或复杂HMAC体系；
- 不进行越权、攻击面、渗透或对抗性安全测试；
- 不建设ClickHouse或历史审计平台；
- 不建设通用消息中间件；
- 不重构与流程高并发无关的模块；
- 不为了覆盖率增加大量无业务价值测试；
- 不扫描整个仓库并修复无关警告；
- 不修改前端视觉、权限体系或无关API；
- 不测试超过当前架构验收需要的异常组合；
- 不引入复杂补偿框架或通用工作流平台。

保留现有基础鉴权和内网访问约束即可，不把安全增强作为阶段阻塞项。

---

# 5. S0：最小基线与问题固化

S0只做推进代码所需的最小确认，不出具大篇幅报告。

## 5.1 必须确认

- Flowable真实版本和自管数据库；
- Flowable HTTP ServiceTask约5秒超时来源；
- 完成任务真实同步调用链；
- ES三个索引及其真实用途；
- Redis当前启动锁行为；
- Flowable、ES、Redis、业务HTTP客户端配置；
- 当前待办分页实现；
- 当前压力测试脚本可复现。

## 5.2 必须建立的测试

只建立以下基线：

1. 最终业务系统延迟5秒时，旧链路Complete出现超时或明显变慢；
2. 待办第一页会先处理多于`pageSize`的数据；
3. ES不可用会阻断或令核心任务消失；
4. Redis不可用会影响现有启动流程；
5. 重复回调缺少可靠持久化幂等。

不扩张其他测试方向。

## 5.3 S0门槛

- 问题可稳定复现；
- 测试能够在后续改造后转为通过；
- 找到真实代码入口；
- 没有基于猜测继续设计。

通过后自动进入S1。

---

# 6. S1：DM8可靠Callback Inbox

## 6.1 最小数据表

至少创建：

```text
workflow_business_instance
workflow_callback_event
```

### `workflow_business_instance`

最少包含：

```text
id
business_id
business_type
process_instance_id
process_definition_key
process_definition_version
flow_state
callback_state
callback_config_snapshot
row_version
data_version
created_at
updated_at
completed_at
```

唯一约束依据现有业务语义决定：

```text
UNIQUE(business_id)
或
UNIQUE(business_type, business_id)
```

不得自行猜测，必须根据当前API和数据确认。

### `workflow_callback_event`

最少包含：

```text
event_id
idempotency_key
business_id
process_instance_id
callback_activity_id
callback_type
payload
status
attempt_count
next_attempt_at
lease_owner
lease_until
last_http_status
last_error
created_at
updated_at
confirmed_at
completed_at
row_version
```

索引至少包括：

```text
UNIQUE(idempotency_key)
INDEX(status, next_attempt_at)
INDEX(process_instance_id)
INDEX(business_id)
INDEX(lease_until)
```

提供DM8正向和回滚SQL。

生产环境不得在应用启动时自动建表。

## 6.2 DM8实现要求

使用.NET 6真实可用的DM8驱动。

必须真实验证：

- 编译；
- 连接；
- 参数绑定；
- 事务；
- CLOB；
- 唯一冲突；
- 连接池；
- 原子领取/租约；
- 更新并发控制。

不得写一个永远返回成功的假Repository，不得使用内存实现伪装DM8完成。

## 6.3 新启动流程绑定

启动链路改为：

```text
Redis advisory lock
→ DM8创建starting预留
→ 调用Flowable启动
→ DM8写回processInstanceId和定义版本
→ ES异步/兼容写查询投影
```

DM8唯一约束承担最终防重。

Redis不可用时允许继续走DM8预留。

Flowable启动调用返回不明确时：

```text
flow_state = reconcile_required
```

通过现有Flowable REST能力对账，禁止立即重复启动。

## 6.4 存量有效流程回填

实现一个限速、可重复运行的迁移工具：

```text
ES绑定
→ Flowable REST确认
→ DM8写入
```

仅迁移S1需要的最小字段。

输出计数即可：

```text
verified
missing_in_flowable
conflict
reconcile_required
```

不生成大篇幅逐条报告，不自动删除孤儿数据。

## 6.5 Callback接收链

Flowable回调接口只执行：

```text
参数校验
→ DM8查询业务绑定
→ 生成稳定idempotencyKey
→ 单事务insert-or-get callback_event
→ 提交
→ 返回2xx
```

禁止在回调请求中：

- 调用最终业务系统；
- 查询或更新ES；
- 再调用Flowable；
- 使用`Task.Run`；
- 使用内存Channel后先返回成功；
- 写Redis后返回成功；
- 捕获DM8异常后伪造成功。

DM8提交失败时返回明确5xx，让Flowable保留失败，而不是丢事件。

## 6.6 事件状态

根据真实代码选择最小、清晰的状态机，至少包含：

```text
pending
processing
retry_waiting
succeeded
dead_letter
cancelled
```

不要为了理论完整性制造过度复杂状态。

如果Flowable回调进入末节点时已经能够由现有API和事务行为确认流程完成，则不额外增加复杂的“等待提交确认”轮询。

只有代码和测试证明存在“DM8已入库但Flowable可能回滚且业务会被提前执行”的真实窗口时，才增加：

```text
awaiting_flowable_commit
```

该状态必须有真实确认方式，禁止仅靠固定sleep。

## 6.7 Callback Worker

Worker必须：

- 使用DM8原子租约；
- 使用`IHttpClientFactory`；
- 使用异步API；
- 有全局并发上限；
- 有每个下游并发上限；
- 有HTTP超时；
- 有有限重试；
- 有退避；
- 可重启恢复；
- 支持死信；
- 支持人工重投；
- 支持应用优雅停止。

禁止：

- 无限并发；
- 无限重试；
- `.Result`、`.Wait()`；
- 进程内唯一队列；
- Redis替代DM8租约；
- 对所有4xx重试；
- 慢下游占满全部Worker并发；
- 无意义的catch后继续假装成功。

## 6.8 下游幂等

使用稳定的：

```text
eventId
idempotencyKey
```

传递给最终业务系统。

优先通过请求Header或Body传递，不重构无关业务协议。

如果下游已支持幂等，接入现有能力。

如果下游暂不支持，流程中心按at-least-once实现并明确代码行为，不伪造exactly-once保证，也不因此停止其他阶段代码推进。

## 6.9 S1测试边界

只测试：

- 相同回调并发100次只有一个事件；
- DM8提交失败不返回伪成功；
- Worker重启后继续处理；
- 下游延迟0/5/15/30/60秒；
- 429、典型5xx、不可恢复4xx；
- 多Worker竞争同一事件；
- Redis关闭后Worker仍能依赖DM8运行；
- ES关闭不阻断Inbox和Worker；
- 死信与人工重投。

不扩大其他安全或极端故障测试。

## 6.10 S1门槛

- Flowable回调不再等待最终业务系统；
- 最终业务延迟不再线性放大Complete时间；
- 事件不丢；
- 事件可重试；
- Worker可恢复；
- 无无限并发；
- DM8写入性能满足测试环境稳定运行；
- 代码、迁移、测试真实通过。

通过后自动进入S2。

---

# 7. S2：ES职责收缩

S2目标不是删除ES，而是把执行正确性从ES移出。

## 7.1 迁入DM8

根据真实代码迁移：

- `businessId → processInstanceId`绑定；
- 业务类型；
- 流程定义版本；
- 执行动作需要的版本化规则；
- Callback配置快照；
- 关键任务动作事实；
- 回调状态；
- 启动和动作防重状态。

只迁移当前代码真实使用的字段，不复制所有ES文档。

## 7.2 核心动作读取

完成、认领、驳回、转派、终止等动作：

```text
DM8定位业务绑定和版本配置
→ Flowable REST实时确认任务/实例
→ 执行动作
→ DM8记录动作事实
→ ES异步更新查询投影
```

不得继续仅根据ES状态判断动作是否允许。

不得伪装Flowable和DM8为同一个事务。

结果不明确时进入可对账状态，不盲目重复执行。

## 7.3 ES投影

ES继续负责：

- 门户列表；
- 筛选；
- 排序；
- 展示；
- 查询历史。

投影必须：

- 有`data_version`；
- 拒绝旧版本覆盖新版本；
- ES写失败不回滚核心动作；
- 支持最小范围重建；
- 查询失败返回明确错误或降级，不把ES故障伪装成空数据。

## 7.4 S2测试边界

只验证：

- ES停机时核心动作仍能正确执行；
- ES恢复后投影可追平；
- 旧版本投影不能覆盖新版本；
- 同一业务不会重复启动；
- 在途实例使用正确版本配置；
- 关键动作不再依赖ES唯一数据。

通过后自动进入S3。

---

# 8. S3：待办查询优化

当前重点是消除：

```text
assignee固定拉取
+ candidate固定拉取
+ 合并补充ES
+ 最后内存分页
```

## 8.1 改造目标

- Flowable查询真实传递`start/size/sort/order`；
- `pageSize`有硬上限；
- 不为第一页读取用户全部或固定200条任务；
- 只对当前页任务查询ES补充数据；
- 解析Flowable真实`total`；
- 处理assignee/candidate重复；
- 保持稳定排序；
- ES缺少单条投影时不静默删除有效Flowable任务。

## 8.2 读模型裁决

优先完成Flowable分页和当前页ES补充。

只有真实压测证明Flowable双集合分页无法满足要求，才在DM8建设最小任务读模型。

不得提前建设第二套完整任务引擎。

## 8.3 S3测试边界

只测试：

- 0、20、100、101、1000条待办；
- assignee/candidate重复；
- 普通用户；
- 单个大待办用户；
- ES单条缺失；
- pageSize超限；
- 第一页实际处理数据量。

通过后自动进入S4。

---

# 9. S4：Redis保护与背压

Redis只在真实瓶颈位置加入。

## 9.1 必须优先考虑

- 启动接口削峰；
- Callback Worker每下游并发控制；
- 高频静态定义配置缓存；
- API级简单限流；
- 缓存击穿保护。

## 9.2 热点待办缓存

仅在S3压测证明必要时，增加1–5秒热点缓存。

必须：

- Redis失败时回源；
- 缓存不决定任务有效性；
- 完成、认领、驳回、转派后精确失效或依赖极短TTL；
- 不建立复杂缓存一致性平台。

## 9.3 S4测试边界

只验证：

- Redis开启前后吞吐变化；
- Redis停机时核心流程仍正确；
- 限流不会造成整体线程堆积；
- 慢下游不会占满全部Worker；
- 不出现缓存击穿导致数据库瞬间失控。

通过后自动进入S5。

---

# 10. S5：高并发验收

## 10.1 压测原则

使用现有压力测试链路，按阶段逐级放大：

```text
100
1000
5000
10000
50000
```

只有当前一级稳定后才进入下一级。

出现系统级不可用、共享环境污染风险或结果已明确时立即停止，不为完成数字继续制造无效压力。

## 10.2 必须覆盖

- 流程启动；
- 待办查询；
- 任务完成；
- 末节点回调；
- 最终业务0/5/15/30/60秒延迟；
- 普通用户模型；
- 单个大待办用户模型；
- Worker队列积压和恢复；
- ES投影延迟；
- Redis关闭后的降级。

不增加与上述目标无关的测试。

## 10.3 关注指标

- HTTP错误率；
- Complete P95/P99；
- 待办查询P95/P99；
- Callback接收P95/P99；
- DM8事务P95/P99；
- Worker队列深度；
- 重试和死信；
- Flowable/API/DM8/ES/Redis CPU和内存；
- 数据库连接池等待；
- HTTP连接和Socket状态；
- 停止压测后的恢复时间。

## 10.4 最终通过条件

- 最终业务延迟不会打爆Flowable和流程中心；
- 无全局长时间停滞；
- 无无限连接、线程或任务堆积；
- Complete不再受业务回调延迟线性影响；
- 待办第一页不加载无关大量数据；
- ES故障不阻断核心动作；
- Redis故障不破坏正确性；
- DM8没有持续锁等待和连接池饱和；
- Worker积压可恢复；
- 没有事件丢失；
- 没有伪成功；
- 服务在压测停止后能够正常恢复。

---

# 11. 禁止无意义实现

严禁：

- 空Repository或固定返回成功；
- 捕获异常后返回成功；
- `Task.Run`代替持久化异步；
- 用内存队列伪装可靠队列；
- 只写接口不接真实运行路径；
- 只写测试Mock而不跑真实集成；
- 永远不会触发的fallback；
- 新旧路径同时执行导致双回调；
- 为了“兼容”长期保留无人验证的分支；
- 用延长超时、扩大线程池掩盖根因；
- 用Redis替代DM8；
- 把ES错误转成空列表；
- 用大量日志或文档替代代码完成；
- 没有运行测试就勾选任务完成；
- 没有Git提交就声称已交付；
- 未推送成功就声称已上传GitHub。

兼容开关仅允许用于真实切换和回滚，并且必须：

- 默认值清晰；
- 有唯一调用路径；
- 有测试；
- 在最终稳定后删除无价值旧路径。

---

# 12. 文档输出要求

不要为每个任务生成单独报告。

只维护：

```text
docs/2026-07-29-flow-runtime-data-and-callback-plan.md
docs/2026-07-29-flow-runtime-data-and-callback-task.md
```

并在最终生成一份：

```text
docs/2026-07-29-flow-runtime-data-and-callback-final-acceptance.md
```

最终验收文档保持简洁，只包含：

1. 最终架构；
2. 完成的S0–S5任务；
3. 关键代码路径；
4. DM8表和索引；
5. ES最终职责；
6. Redis最终职责；
7. 关键测试命令；
8. 100–50000各级真实结果；
9. P95/P99和错误率；
10. 未解决的真实限制；
11. Git提交列表；
12. GitHub推送结果。

不得生成重复的架构说明、每日进度报告或大量无结论文档。

---

# 13. 最终交付

Codex完成后必须：

- 保证工作区没有遗漏的核心修改；
- 运行与本次改造直接相关的全部测试；
- 完成S5验收；
- 更新plan/task状态；
- 生成最终简明验收文档；
- 按阶段提交Git；
- 推送现有GitHub远程；
- 输出最终commit hash；
- 输出远程分支；
- 如实列出仍未通过的项目。

最终汇报不得只写“已完成”，必须给出：

```text
测试命令
真实通过/失败数量
关键性能结果
提交hash
push结果
未解决问题
```

在满足阶段门槛后自行连续推进，不等待人工确认。
