# 门户资讯审批压力测试

该脚本同时覆盖三类负载：

- 创建流程实例并保留发起人待办；
- 完成完整流程，末节点回调按 A/B 各 50% 分流；
- 并发查询发起人“我的待办”。

A 组回调立即返回；B 组默认延迟 15 秒。`TestController` 的
`GET /api/test/callback-metrics` 可查看测试控制器当前/峰值慢回调并发，
`GET /api/admin/callback-events/metrics` 可查看 DM8 回调积压、最老事件
年龄、每分钟成功/失败、租约丢失次数及各下游 p95。

当前门户 BPMN 的末尾 HTTP ServiceTask 只负责把流程结束事实投递到流程
中心；流程中心先把业务回调事件持久化到 DM8，然后由固定并发 Worker 调用
下游。B 组设置为 15 秒用于验证慢业务后端只形成可观测积压，不延长用户的
最后节点完成请求。

推荐先逐级执行，再扩大到目标总量：

```bash
docker run --rm --network host \
  -v "$PWD/performance:/scripts:ro" \
  -e BASE_URL=http://192.168.124.2:5012 \
  -e ACCESS_TOKEN="$ACCESS_TOKEN" \
  -e RUN_ID=portal-baseline \
  -e START_ONLY_ITERATIONS=90 \
  -e LIFECYCLE_ITERATIONS=10 \
  -e QUERY_ITERATIONS=100 \
  -e START_ONLY_VUS=20 \
  -e LIFECYCLE_VUS=10 \
  -e QUERY_VUS=20 \
  grafana/k6 run /scripts/portal-approval-load.js
```

本轮极限档配置为 1 万启动、2000 次待办查询、2000 条完整流程：

```bash
-e START_ONLY_ITERATIONS=8000
-e LIFECYCLE_ITERATIONS=2000
-e QUERY_ITERATIONS=2000
```

这里的“1 万启动”表示最终创建 1 万个流程实例；并发度由各 `*_VUS`
参数独立控制。直接设置 1 万 VU 会首先测试压测机的文件描述符和内存，
不能代表流程中心容量。

脚本为每次启动显式传入稳定且唯一的 `requestId=businessId`，阈值要求
`checks=100%` 且 `http_req_failed=0%`。

脚本会单独输出启动、发起节点完成、末节点 A/B 完成和待办查询耗时。
使用同一个真实 JWT；`employeeId=196045` 的待办查询是“单个大待办用户”
的热点模型，不等同于创建 1 万个 Keycloak 身份。

## 问题归零推荐人一致性

`problem-zero-recommendation-load.js` 会对每个实例依次验证：

1. `/start` 传入六类角色推荐人；
2. ES `recommendedAssigneesSnapshot` 是否完整持久化；
3. 六个节点的待办响应是否按 `slotKey` 返回推荐人及锁定标志；
4. 每一阶段的 `flow-render` 接口是否成功；
5. 按专项工作分支完成整条多节点流程。

运行前需将问题归零 slot 配置中的测试回调地址部署为压测 API 可访问的
`http://127.0.0.1:5012/api/test/node-callback`。

```bash
docker run --rm --network host \
  -v "$PWD/performance:/scripts:ro" \
  -e BASE_URL=http://127.0.0.1:5012 \
  -e ES_URL=http://127.0.0.1:19200 \
  -e ACCESS_TOKEN="$ACCESS_TOKEN" \
  -e RUN_ID=problem-zero-consistency \
  -e ITERATIONS=100 \
  -e VUS=20 \
  grafana/k6 run /scripts/problem-zero-recommendation-load.js
```

## 问题归零全分支 A/B 一万流程

`problem-zero-branch-ab-load.js` 将实例轮转分配到已解决、未解决专项、
未解决非专项三条路径，并在每条路径内继续按 A/B 分配快速和慢速末尾回调。
一万表示一万个完整流程实例，不表示一万个同时连接：

```bash
docker run --rm --network host \
  -v "$PWD/performance:/scripts:ro" \
  -e BASE_URL=http://127.0.0.1:5012 \
  -e ACCESS_TOKEN="$ACCESS_TOKEN" \
  -e RUN_ID=problem-zero-branches-ab-10k \
  -e ITERATIONS=10000 \
  -e VUS=100 \
  -e SLOW_DELAY_MS=15000 \
  grafana/k6 run /scripts/problem-zero-branch-ab-load.js
```
