using System.Collections.Generic;
using FlowableWrapper.Domain.Abstractions;

namespace FlowableWrapper.Configuration
{
    /// <summary>
    /// 业务类型到流程定义 Key 的映射配置
    /// 绑定到 appsettings.json 的 BusinessTypeProcessMapping 节点
    ///
    /// 示例配置：
    /// "BusinessTypeProcessMapping": {
    ///   "Mappings": {
    ///     "personnel_selection_approval": "personnel_selection_approval",
    ///     "inspection_briefing": "inspection_briefing_process"
    ///   }
    /// }
    /// </summary>
    public class BusinessTypeProcessMapping
    {
        /// <summary>
        /// Key: businessType，Value: processDefinitionKey
        /// </summary>
        public Dictionary<string, string> Mappings { get; set; }
            = new Dictionary<string, string>();

        /// <summary>
        /// 根据 businessType 获取 processDefinitionKey
        /// 若未配置映射则直接返回 businessType 本身（允许 businessType 与 key 相同）
        /// </summary>
        public string GetProcessDefinitionKey(string businessType)
        {
            if (string.IsNullOrWhiteSpace(businessType))
                throw new BusinessException("BusinessType 不能为空");

            if (Mappings != null && Mappings.TryGetValue(businessType, out var key))
                return key;

            // 兜底：businessType 即 processDefinitionKey
            return businessType;
        }
    }
}
