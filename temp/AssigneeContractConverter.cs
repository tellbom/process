using System;
using System.Collections.Generic;
using System.Linq;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.ElasticSearch;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Slots
{
    /// <summary>
    /// AssigneeContract → RecommendedAssigneesSnapshot 转换器
    ///
    /// 职责：
    ///   将启动时传入的全流程角色选人契约（AssigneeContract）
    ///   展开为 Dictionary&lt;string, List&lt;string&gt;&gt;（Key = roleKey，Value = 推荐人列表）
    ///   写入 ProcessMetadataDocument.RecommendedAssigneesSnapshot
    ///
    /// Key 语义修正（F1 fix）：
    ///   Key 使用 roleKey 而非 slotKey
    ///   原因：roleKey 表示"当前节点处理人的角色"
    ///         slot 表示"当前节点完成时为下一节点选谁"
    ///         两者是不同的人，不能把 roleKey 推荐人写入节点的 slot
    ///   前端按当前节点的 nodeInfo.roleKey 从 RecommendedUsers 中取推荐人
    ///
    /// 设计原则：
    ///   ✔ 不调用 SlotVariableConverter，不生成任何 Flowable 流程变量
    ///   ✔ 不注入任何 assignee / collection 变量到 Flowable 启动参数
    ///   ✔ 只做"roleKey → 推荐人"的直接映射，最终落点是 RecommendedAssigneesSnapshot
    ///   ✔ 当前节点最终生效人员只能来自 NextSlotSelections（流程中心不猜测节点）
    /// </summary>
    public class AssigneeContractConverter
    {
        private readonly ILogger<AssigneeContractConverter> _logger;

        public AssigneeContractConverter(ILogger<AssigneeContractConverter> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 将 AssigneeContract 展开为推荐人快照
        ///
        /// Key 语义：roleKey（不是 slotKey）
        ///   roleKey 表示"当前节点处理人的角色"
        ///   前端在当前节点读取 recommendedUsers[nodeInfo.roleKey] 得到当前节点处理人推荐
        ///   slot 是"为下一节点选人的槽位"，两者是不同的人，Key 不能混用
        ///
        /// 执行步骤：
        ///   1. 直接按 AssigneeContract.Roles[].RoleKey 为 Key，Users 为 Value 写入结果
        ///   2. semanticMap 只用于验证 roleKey 是否在流程中有对应节点（可选校验）
        ///   3. 返回 roleKey → users 字典，调用方合并进 RecommendedAssigneesSnapshot
        /// </summary>
        /// <param name="contract">全流程角色选人契约</param>
        /// <param name="semanticMap">流程定义节点语义映射，用于 Debug 日志校验</param>
        /// <returns>roleKey → 推荐人列表</returns>
        public Dictionary<string, List<string>> ToRecommendedSnapshot(
            AssigneeContract contract,
            Dictionary<string, NodeSemanticInfo> semanticMap)
        {
            var result = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);

            if (contract?.Roles == null || !contract.Roles.Any())
            {
                _logger.LogDebug("AssigneeContract 为空，返回空推荐人快照");
                return result;
            }

            // 建立 roleKey 集合用于校验（来自 semanticMap）
            var knownRoleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (semanticMap != null)
            {
                foreach (var nodeInfo in semanticMap.Values)
                {
                    if (!string.IsNullOrWhiteSpace(nodeInfo.RoleKey))
                        knownRoleKeys.Add(nodeInfo.RoleKey);
                }
            }

            // 直接按 roleKey 作为 Key 写入推荐人
            foreach (var role in contract.Roles)
            {
                if (string.IsNullOrWhiteSpace(role.RoleKey)) continue;

                if (role.Users == null || !role.Users.Any())
                {
                    _logger.LogWarning(
                        "RoleKey [{RoleKey}] Users 为空，跳过推荐人写入",
                        role.RoleKey);
                    continue;
                }

                // 校验：roleKey 是否在 semanticMap 中有对应节点（仅 Debug 日志，不阻塞）
                if (knownRoleKeys.Any() && !knownRoleKeys.Contains(role.RoleKey))
                {
                    _logger.LogDebug(
                        "RoleKey [{RoleKey}] 在 semanticMap 中无对应节点，仍写入快照（节点可能未配置 roleKey）",
                        role.RoleKey);
                }

                result[role.RoleKey] = role.Users;

                _logger.LogDebug(
                    "AssigneeContract 推荐人映射: RoleKey={RoleKey} → Users=[{Users}]",
                    role.RoleKey, string.Join(",", role.Users));
            }

            _logger.LogInformation(
                "AssigneeContract 展开为推荐人快照完成: 角色数={RoleCount}, RoleKey 数={KeyCount}",
                contract.Roles.Count, result.Count);

            return result;
        }
    }
}
