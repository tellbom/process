using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FlowableWrapper.Application.Slots
{
    /// <summary>
    /// 前端提交的选人单元
    /// 对应 SlotDefinition.SlotKey，前端按此 key 提交选人结果
    /// </summary>
    public class SlotSelection
    {
        /// <summary>
        /// Slot 唯一键，必须与 NodeSemanticInfo.Slots 中的 slotKey 一致
        /// 示例：inspection_office_reviewer
        /// </summary>
        [Required(ErrorMessage = "slotKey 不能为空")]
        public string SlotKey { get; set; }

        /// <summary>
        /// 选中的用户工号列表
        /// single 模式只传一个，multiple 模式可传多个
        /// </summary>
        public List<string> Users { get; set; } = new List<string>();
    }

    /// <summary>
    /// Slot 转换结果（内部使用）
    /// SlotVariableConverter 的输出，供 AppService 直接合并到 Flowable 变量
    /// </summary>
    public class SlotConversionResult
    {
        /// <summary>
        /// 转换后的流程变量
        /// Key: variableName（如 inspectionOfficeReviewer）
        /// Value: 用户工号 string（single）或 List&lt;string&gt;（multiple）
        /// </summary>
        public Dictionary<string, object> Variables { get; set; }
            = new Dictionary<string, object>();

        /// <summary>
        /// 转换后的选人快照，用于写入 ProcessAuditRecord
        /// </summary>
        public List<SlotSelectionSnapshot> Snapshots { get; set; }
            = new List<SlotSelectionSnapshot>();
    }

    /// <summary>
    /// 选人快照（用于审计记录）
    /// </summary>
    public class SlotSelectionSnapshot
    {
        public string SlotKey { get; set; }
        public string Label { get; set; }
        public List<string> Users { get; set; } = new List<string>();
    }
}
