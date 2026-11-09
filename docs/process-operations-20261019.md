# 全部流程运维界面与收回验收

日期：2026-10-19。用户已明确选择“收回 = 终止当前审批轮次，保留历史，可重新发起”。

## 界面

入口：`http://192.168.124.5:1640/flow/allProcesses`，沿用已登记的 RBAC `allProcesses` 菜单。

- 列表新增“收回流程”和“运维操作”。收回不限定当前登录人为发起人，也不依赖当前节点的普通审批撤回配置。
- 运维抽屉展示所选业务、实例、审批轮次、引擎实际状态，以及当前所有任务的办理人 / 候选人。
- 收回需要填写原因，确认窗口明确显示业务、实例和轮次。成功后刷新列表及实际状态；终止后不再允许收回。
- 可按具体任务转派给一个新办理人或多个候选人，必填原因。候选选择来自当前 RBAC 项目的真实人员数据，按姓名和精确工号分别查询合并，没有使用示例人员列表。
- 转派遵守节点 `CanReassign` 配置；未开放的节点显示禁用状态。节点配置读取失败时禁用转派并提示原因，仍保留管理员收回能力。
- “查看 / 处理投递”只在当前用户已有对应 RBAC 菜单时显示，进入失败投递管理时携带 businessId 和 processInstanceId，支持现有重试 / 取消投递操作。切换筛选时忽略旧请求的迟到响应。

新组件：`E:\Web\flow\web\src\components\todo\ProcessOperationsDrawer.vue`。

## 后端契约

```text
GET /api/processes/{businessId}/operations?processInstanceId=...
  -> businessId / processInstanceId / flowState / isLatestRound / isActive
     availableActions / unavailableReason / taskConfigurationWarning / tasks[]

POST /api/processes/terminate
  { businessId, processInstanceId, reason }

POST /api/tasks/reassign
  { businessId, processInstanceId, taskId, newAssignees, reason }
```

运维查询是只读接口，沿用后端认证策略，并禁止缓存。当前轮次以 DM8 绑定为准，引擎实例存在且未结束才提供收回动作；历史轮次只允许查看历史和投递处理。引擎 404 表示非活动，网络 / 引擎 503 等错误继续报错，不伪装为已结束。

终止接口在执行前重新核对所选实例是否仍为当前轮次，并重新查询引擎实际状态。轮次变化返回 `PROCESS_INSTANCE_CHANGED`；已结束 / 不存在返回 `PROCESS_NOT_ACTIVE`，均不会执行删除运行实例或篡改完成状态。转派请求也增加可选 processInstanceId 校验，现有 taskId 所属流程和节点能力校验继续生效。

本界面复用既有终止、任务转派和投递接口，不伪装任务办理人执行审批通过或驳回。终止保留流程历史，不能恢复原轮次；需要继续审批时重新发起。运维接口应继续放在现有内部认证 / 授权边界下，没有给其他账号新增菜单或权限。

## 真实验收

单独创建了以下测试流程定义，未修改既有业务流程：

- 定义 Key：`ops_recall_acceptance_20261019_1792406383588`
- 部署 ID：`657f9e7b-cba9-11f1-bafa-0242c0a81002`
- 业务 ID：`OPS_RECALL_UI_1792406383588`
- 第 1 轮：`65af39fe-cba9-11f1-bafa-0242c0a81002`
- 第 2 轮：`f77be740-cba9-11f1-bafa-0242c0a81002`

结果：

1. 第 1 轮的当前任务 `65af3a0c-cba9-11f1-bafa-0242c0a81002` 起初属于测试占位人 `OPS_LOCAL_TEST_HOLDER`。通过真实界面转派给当前账号后，引擎返回 assignee=`196045`，审计记录包含 `action=reassign`、真实操作人及填写的转派原因。
2. 收回原因为空时确认按钮禁用。填写原因、通过真实确认按钮收回第 1 轮后：列表及 DM8 运维状态均为 terminated，当前任务数为 0，availableActions 为空，历史转派意见仍可读取。
3. 已终止后再次请求收回，返回 HTTP 400 / `PROCESS_NOT_ACTIVE`。
4. 使用同一业务 ID、新 requestId 发起第 2 轮。以第 1 轮实例发起收回，返回 HTTP 400 / `PROCESS_INSTANCE_CHANGED`，第 2 轮仍为活动状态。随后通过界面收回第 2 轮，两轮最终均为 terminated。
5. 按工号 `196045` 搜索，只显示对应真实账号；取消转派不更改任务。
6. 从所选轮次进入投递页后，两个筛选框分别填入对应业务 ID 与实例 ID，默认展示该轮全部投递状态；未执行其他业务事件的重试或取消。
7. 浏览器测试未发现 JavaScript 页面异常。密码与 Token 未保存到测试脚本、配置或登录状态文件。

两轮测试记录及专用定义保留，便于查验转派和终止历史；没有清除业务历史。

## 自动检查

- 后端全套测试：63 通过、12 跳过、0 失败。跳过项仍为未启用配置的 DM8 集成测试，真实专项 HTTP 验收单独记录如上。
- 后端正常输出构建：0 错误，保留已有依赖警告。
- 前端生产构建通过。
- 前端类型检查：原有 41 处错误保持逐行一致，没有新增。

回调 Worker 与通知开关保持原配置关闭，本次测试节点未配置业务回调，未触发其他业务系统通知。本次没有提交 / 推送 Git，V3 对照源码和原有未提交改动保留。
