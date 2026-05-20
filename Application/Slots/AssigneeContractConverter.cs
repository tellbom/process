using System;
using System.Collections.Generic;
using System.Linq;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.ElasticSearch;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Slots
{
    /// <summary>
    /// Converts a full-process AssigneeContract into slot selections and reuses SlotVariableConverter.
    /// </summary>
    public class AssigneeContractConverter
    {
        private readonly SlotVariableConverter _slotConverter;
        private readonly ILogger<AssigneeContractConverter> _logger;

        public AssigneeContractConverter(
            SlotVariableConverter slotConverter,
            ILogger<AssigneeContractConverter> logger)
        {
            _slotConverter = slotConverter;
            _logger = logger;
        }

        public SlotConversionResult Convert(
            AssigneeContract contract,
            Dictionary<string, NodeSemanticInfo> semanticMap,
            Dictionary<string, object> businessVariables = null)
        {
            if (contract?.Roles == null || !contract.Roles.Any())
            {
                _logger.LogWarning("AssigneeContract is empty. Returning an empty conversion result.");
                return new SlotConversionResult();
            }

            if (semanticMap == null || !semanticMap.Any())
            {
                _logger.LogWarning("Node semantic map is empty. AssigneeContract cannot be projected.");
                return new SlotConversionResult();
            }

            var roleDict = contract.Roles
                .Where(r => !string.IsNullOrWhiteSpace(r.RoleKey))
                .GroupBy(r => r.RoleKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var slotSelections = new List<SlotSelection>();

            foreach (var nodeInfo in semanticMap.Values.Where(n => n != null))
            {
                if (string.IsNullOrWhiteSpace(nodeInfo.RoleKey)) continue;

                if (!roleDict.TryGetValue(nodeInfo.RoleKey, out var role))
                {
                    _logger.LogDebug(
                        "RoleKey [{RoleKey}] for node [{TaskDefinitionKey}] was not found in AssigneeContract. Skipped.",
                        nodeInfo.RoleKey,
                        nodeInfo.TaskDefinitionKey);
                    continue;
                }

                if (role.Users == null || !role.Users.Any())
                {
                    _logger.LogWarning(
                        "RoleKey [{RoleKey}] has empty users. Node [{TaskDefinitionKey}] was skipped.",
                        role.RoleKey,
                        nodeInfo.TaskDefinitionKey);
                    continue;
                }

                foreach (var slot in nodeInfo.Slots ?? new List<SlotDefinition>())
                {
                    var expectedMode = string.IsNullOrWhiteSpace(nodeInfo.AssigneeMode)
                        ? slot.Mode
                        : nodeInfo.AssigneeMode;

                    if (!string.IsNullOrWhiteSpace(role.Mode)
                        && !string.IsNullOrWhiteSpace(expectedMode)
                        && !string.Equals(role.Mode, expectedMode, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "RoleKey [{RoleKey}] mode [{RoleMode}] does not match node [{TaskDefinitionKey}] slot [{SlotKey}] mode [{ExpectedMode}]. Slot skipped.",
                            role.RoleKey,
                            role.Mode,
                            nodeInfo.TaskDefinitionKey,
                            slot.SlotKey,
                            expectedMode);
                        continue;
                    }

                    slotSelections.Add(new SlotSelection
                    {
                        SlotKey = slot.SlotKey,
                        Users = role.Users
                    });

                    _logger.LogDebug(
                        "AssigneeContract projected RoleKey={RoleKey} to SlotKey={SlotKey}, Users=[{Users}]",
                        role.RoleKey,
                        slot.SlotKey,
                        string.Join(",", role.Users));
                }
            }

            var allSlotDefs = semanticMap.Values
                .Where(n => n?.Slots != null)
                .SelectMany(n => n.Slots)
                .Where(s => !string.IsNullOrWhiteSpace(s.SlotKey))
                .GroupBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            _logger.LogInformation(
                "AssigneeContract projection completed. Roles={RoleCount}, SlotSelections={SelectionCount}",
                contract.Roles.Count,
                slotSelections.Count);

            return _slotConverter.Convert(slotSelections, allSlotDefs, businessVariables);
        }
    }
}
