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

        public List<NodeDescriptionSnapshot> ToNodeDescriptionsSnapshot(
            AssigneeContract contract,
            Dictionary<string, NodeSemanticInfo> semanticMap)
        {
            var result = new List<NodeDescriptionSnapshot>();
            if (contract?.NodeDescriptions == null
                || contract.NodeDescriptions.Count == 0)
                return result;

            var knownRoleKeys = semanticMap?.Values
                .Where(node => node != null && !string.IsNullOrWhiteSpace(node.RoleKey))
                .Select(node => node.RoleKey.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenRoleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in contract.NodeDescriptions)
            {
                var description = entry?.Description?.Trim();
                if (string.IsNullOrWhiteSpace(description))
                    continue;

                var roleKey = entry?.RoleKey?.Trim();
                if (string.IsNullOrWhiteSpace(roleKey))
                    throw new ArgumentException("Node description roleKey cannot be empty.");
                if (knownRoleKeys.Count > 0 && !knownRoleKeys.Contains(roleKey))
                    throw new ArgumentException(
                        $"Node description roleKey [{roleKey}] was not found in the deployed process definition.");
                if (!seenRoleKeys.Add(roleKey))
                    throw new ArgumentException(
                        $"Node description roleKey [{roleKey}] cannot be repeated.");

                result.Add(new NodeDescriptionSnapshot
                {
                    RoleKey = roleKey,
                    Description = description
                });
            }

            return result;
        }
    }
}
