using System;
using System.Collections.Generic;
using System.Linq;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Slots
{
    /// <summary>
    /// Slot → Flowable 变量转换器
    ///
    /// 职责：
    ///   将前端提交的 List&lt;SlotSelection&gt; 按照 List&lt;SlotDefinition&gt; 的定义
    ///   转换为 Flowable complete 时需要传入的流程变量字典
    ///
    /// 转换规则：
    ///   single   → variableName = "EMP_001"（string）
    ///   multiple → variableName = ["EMP_001","EMP_002"]（List&lt;string&gt;）
    ///
    /// 条件 Slot（conditionalOn）：
    ///   格式："变量名=值"，如 "needPersonFeedback=true"
    ///   当 businessVariables 中该变量的值与期望值匹配时，本 Slot 才参与校验和转换
    ///   不满足条件的 Slot 即使前端传了值也会被忽略
    /// </summary>
    public class SlotVariableConverter
    {
        private readonly ILogger<SlotVariableConverter> _logger;

        public SlotVariableConverter(ILogger<SlotVariableConverter> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 执行转换
        /// </summary>
        /// <param name="selections">前端提交的选人列表</param>
        /// <param name="slotDefs">本节点的 Slot 定义列表（从 ES 读取）</param>
        /// <param name="businessVariables">业务变量（用于条件 Slot 的条件求值）</param>
        /// <returns>转换结果，含 Flowable 变量 + 审计快照</returns>
        public SlotConversionResult Convert(
            List<SlotSelection> selections,
            List<SlotDefinition> slotDefs,
            Dictionary<string, object> businessVariables = null)
        {
            // 允许 selections 为 null（当前节点无需选人时前端可不传）
            selections ??= new List<SlotSelection>();
            slotDefs ??= new List<SlotDefinition>();
            businessVariables ??= new Dictionary<string, object>();

            var result = new SlotConversionResult();

            // 构建提交的选人字典，方便后续查找
            var selectionDict = selections
                .GroupBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var def in slotDefs)
            {
                // ── 1. 判断条件 Slot 是否激活 ──────────────────────
                if (!string.IsNullOrWhiteSpace(def.ConditionalOn))
                {
                    var conditionMet = EvaluateCondition(def.ConditionalOn, businessVariables);
                    if (!conditionMet)
                    {
                        _logger.LogDebug(
                            "Slot [{SlotKey}] 条件不满足 ({Condition})，已跳过",
                            def.SlotKey, def.ConditionalOn);
                        continue;
                    }
                }

                // ── 2. 查找前端提交的对应选人 ───────────────────────
                selectionDict.TryGetValue(def.SlotKey, out var selection);
                var users = selection?.Users ?? new List<string>();

                // ── 3. 必填校验 ─────────────────────────────────────
                if (def.Required && !users.Any())
                {
                    throw new BusinessException(
                        $"Slot [{def.Label}]（{def.SlotKey}）为必填项，请选择人员",
                        "SLOT_REQUIRED");
                }

                // ── 4. single 模式人数校验 ───────────────────────────
                if (def.Mode == "single" && users.Count > 1)
                {
                    throw new BusinessException(
                        $"Slot [{def.Label}]（{def.SlotKey}）为单人模式，不能选择多人",
                        "SLOT_SINGLE_OVERFLOW");
                }

                // ── 5. 空列表跳过（非必填且未选） ───────────────────
                if (!users.Any())
                {
                    _logger.LogDebug("Slot [{SlotKey}] 非必填且未选人，跳过变量写入", def.SlotKey);
                    continue;
                }

                // ── 6. 写入变量 ──────────────────────────────────────
                if (def.Mode == "single")
                {
                    result.Variables[def.VariableName] = users[0];
                    _logger.LogDebug("Slot [{SlotKey}] → 变量 {VarName} = {Value}",
                        def.SlotKey, def.VariableName, users[0]);
                }
                else // multiple
                {
                    result.Variables[def.VariableName] = users;
                    _logger.LogDebug("Slot [{SlotKey}] → 变量 {VarName} = [{Values}]",
                        def.SlotKey, def.VariableName, string.Join(",", users));
                }

                // ── 7. 写入审计快照 ──────────────────────────────────
                result.Snapshots.Add(new SlotSelectionSnapshot
                {
                    SlotKey = def.SlotKey,
                    Label = def.Label,
                    Users = new List<string>(users)
                });
            }

            // ── 8. 检查是否有未定义的 slotKey 被提交（防御性校验）──
            var definedKeys = slotDefs.Select(d => d.SlotKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sel in selections)
            {
                if (!definedKeys.Contains(sel.SlotKey))
                {
                    _logger.LogWarning(
                        "前端提交了未定义的 slotKey [{SlotKey}]，已忽略",
                        sel.SlotKey);
                }
            }

            return result;
        }

        /// <summary>
        /// 评估条件表达式
        /// 支持格式：
        ///   "variableName=value"       → 精确匹配（单等号）
        ///   "variableName==value"      → 精确匹配（双等号，与单等号等价）
        ///   "variableName=true/false"  → 布尔匹配
        ///   "!variableName=value"      → 取反匹配（前缀 ! 表示不等于）
        ///
        /// 注意：businessVariables 中的值可能是 JsonElement（来自 HTTP 请求体反序列化），
        ///       需主动解包后再做比较，不能直接依赖 ToString()
        /// </summary>
        private bool EvaluateCondition(
            string condition,
            Dictionary<string, object> variables)
        {
            if (string.IsNullOrWhiteSpace(condition)) return true;

            try
            {
                bool negate = condition.StartsWith("!");
                var expr = negate ? condition.Substring(1) : condition;

                // 同时支持 == 和 = 作为比较运算符
                // 优先匹配 ==，避免 IndexOf('=') 把 == 拆成 varName="" 和 "=value"
                int eqIdx;
                int eqLen;
                var doubleEq = expr.IndexOf("==", StringComparison.Ordinal);
                if (doubleEq >= 0)
                {
                    eqIdx = doubleEq;
                    eqLen = 2;
                }
                else
                {
                    eqIdx = expr.IndexOf('=');
                    eqLen = 1;
                }

                if (eqIdx < 0)
                {
                    // 仅变量名：判断是否存在且非空
                    var exists = variables.TryGetValue(expr.Trim(), out var v)
                                 && v != null
                                 && !string.IsNullOrWhiteSpace(UnwrapValue(v));
                    return negate ? !exists : exists;
                }

                var varName = expr.Substring(0, eqIdx).Trim();
                var expectedRaw = expr.Substring(eqIdx + eqLen).Trim();

                if (!variables.TryGetValue(varName, out var actual))
                {
                    // 变量不存在 → 条件不满足
                    return negate;
                }

                var actualStr = UnwrapValue(actual);
                bool matched;

                // 布尔比较（不区分大小写）
                if (bool.TryParse(expectedRaw, out var expectedBool) &&
                    bool.TryParse(actualStr, out var actualBool))
                {
                    matched = actualBool == expectedBool;
                }
                else
                {
                    // 字符串比较（不区分大小写）
                    matched = string.Equals(actualStr, expectedRaw,
                        StringComparison.OrdinalIgnoreCase);
                }

                _logger.LogDebug(
                    "条件求值: [{Condition}] varName={VarName} actual={Actual} expected={Expected} matched={Matched}",
                    condition, varName, actualStr, expectedRaw, negate ? !matched : matched);

                return negate ? !matched : matched;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "条件表达式解析失败: {Condition}，默认视为条件满足", condition);
                return true;
            }
        }

        /// <summary>
        /// 将变量值解包为字符串，正确处理 JsonElement 类型
        /// businessVariables 经过 HTTP 请求体反序列化后，所有值均为 JsonElement，
        /// 直接调用 ToString() 对布尔值返回 "True"/"False"（System 格式），
        /// 而 JsonElement.GetBoolean().ToString() 返回 "True"，bool.TryParse 虽不区分大小写
        /// 但 GetRawText() 返回 "true"/"false"（JSON 格式），更接近预期
        /// </summary>
        private static string UnwrapValue(object value)
        {
            if (value == null) return "";

            if (value is System.Text.Json.JsonElement je)
            {
                return je.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.True => "true",
                    System.Text.Json.JsonValueKind.False => "false",
                    System.Text.Json.JsonValueKind.Null => "",
                    System.Text.Json.JsonValueKind.String => je.GetString() ?? "",
                    _ => je.GetRawText()
                };
            }

            // 原生 bool 也统一转为小写，与 JSON 格式保持一致
            if (value is bool b) return b ? "true" : "false";

            return value.ToString() ?? "";
        }
    }
}