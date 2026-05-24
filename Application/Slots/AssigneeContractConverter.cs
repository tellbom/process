using System;
using System.Collections.Generic;
using System.Linq;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.ElasticSearch;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Slots
{
    /// <summary>
    /// AssigneeContract -> RecommendedAssigneesSnapshot converter.
    ///
    /// RecommendedAssigneesSnapshot is keyed by roleKey, not slotKey:
    /// roleKey describes who handles the current node; slotKey describes who the
    /// current node selects for a downstream node. Those are different subjects.
    ///
    /// This converter does not generate Flowable variables. The final effective
    /// assignees still come from NextSlotSelections.
    /// </summary>
    public class AssigneeContractConverter
    {
        private readonly ILogger<AssigneeContractConverter> _logger;

        public AssigneeContractConverter(ILogger<AssigneeContractConverter> logger)
        {
            _logger = logger;
        }

        public Dictionary<string, List<string>> ToRecommendedSnapshot(
            AssigneeContract contract,
            Dictionary<string, NodeSemanticInfo> semanticMap)
        {
            var result = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);

            if (contract?.Roles == null || !contract.Roles.Any())
            {
                _logger.LogDebug("AssigneeContract is empty. Returning an empty recommended snapshot.");
                return result;
            }

            var knownRoleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (semanticMap != null)
            {
                foreach (var nodeInfo in semanticMap.Values.Where(n => n != null))
                {
                    if (!string.IsNullOrWhiteSpace(nodeInfo.RoleKey))
                        knownRoleKeys.Add(nodeInfo.RoleKey);
                }
            }

            foreach (var role in contract.Roles)
            {
                if (string.IsNullOrWhiteSpace(role.RoleKey)) continue;

                if (role.Users == null || !role.Users.Any())
                {
                    _logger.LogWarning(
                        "RoleKey [{RoleKey}] has empty users. Skipped.",
                        role.RoleKey);
                    continue;
                }

                if (knownRoleKeys.Any() && !knownRoleKeys.Contains(role.RoleKey))
                {
                    _logger.LogDebug(
                        "RoleKey [{RoleKey}] was not found in semanticMap. It will still be written to the recommended snapshot.",
                        role.RoleKey);
                }

                result[role.RoleKey] = new List<string>(role.Users);

                _logger.LogDebug(
                    "AssigneeContract recommended mapping: RoleKey={RoleKey}, Users=[{Users}]",
                    role.RoleKey,
                    string.Join(",", role.Users));
            }

            _logger.LogInformation(
                "AssigneeContract expanded into recommended snapshot. Roles={RoleCount}, RoleKeys={RoleKeyCount}",
                contract.Roles.Count,
                result.Count);

            return result;
        }
    }
}
