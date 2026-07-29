# 门户资讯审批压力测试

该脚本同时覆盖三类负载：

- 创建流程实例并保留发起人待办；
- 完成完整流程，末节点回调按 A/B 各 50% 分流；
- 并发查询发起人“我的待办”。

A 组回调立即返回；B 组默认延迟 15 秒。`TestController` 的
`GET /api/test/callback-metrics` 可查看当前/峰值慢回调并发。

当前门户 BPMN 的末尾 HTTP ServiceTask 是同步调用。测试环境 Flowable
约 5 秒即发生 HTTP 超时，所以 B 组设置为 15 秒时，预期会暴露末节点
完成失败及其对其他接口的连带影响。

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

目标总量配置为 5 万启动、1 万待办查询、1 万末节点完成：

```bash
-e START_ONLY_ITERATIONS=40000
-e LIFECYCLE_ITERATIONS=10000
-e QUERY_ITERATIONS=10000
```

这里的“5 万启动”表示最终创建 5 万个流程实例；并发度由各 `*_VUS`
参数独立控制。直接设置 5 万 VU 会首先测试压测机的文件描述符和内存，
不能代表流程中心容量。

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
