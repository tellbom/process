using System;
using System.Collections.Generic;
using System.Linq;
using FlowableWrapper.Application.Dtos;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Slots
{
    /// <summary>
    /// Projects LoopAssignments into Flowable collection variables.
    /// </summary>
    public class LoopAssignmentProjector
    {
        private readonly ILogger<LoopAssignmentProjector> _logger;

        public LoopAssignmentProjector(ILogger<LoopAssignmentProjector> logger)
        {
            _logger = logger;
        }

        public Dictionary<string, object> Project(LoopAssignments assignments)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (assignments?.Items == null || !assignments.Items.Any())
                return result;

            foreach (var item in assignments.Items)
            {
                if (string.IsNullOrWhiteSpace(item.LoopKey))
                {
                    _logger.LogWarning("LoopAssignment item has an empty LoopKey. Item skipped.");
                    continue;
                }

                foreach (var role in item.Roles ?? new List<RoleAssignment>())
                {
                    if (string.IsNullOrWhiteSpace(role.RoleKey))
                    {
                        _logger.LogWarning(
                            "LoopAssignment item [{LoopKey}] has a role with empty RoleKey. Role skipped.",
                            item.LoopKey);
                        continue;
                    }

                    if (role.Users == null || !role.Users.Any())
                    {
                        _logger.LogWarning(
                            "LoopAssignment item [{LoopKey}] role [{RoleKey}] has empty users. Role skipped.",
                            item.LoopKey,
                            role.RoleKey);
                        continue;
                    }

                    var variableName = $"{item.LoopKey}_{role.RoleKey}_list";

                    if (result.TryGetValue(variableName, out var existing)
                        && existing is List<string> existingUsers)
                    {
                        existingUsers.AddRange(role.Users);
                    }
                    else
                    {
                        result[variableName] = new List<string>(role.Users);
                    }

                    _logger.LogDebug(
                        "LoopAssignment projected {VariableName} += [{Users}]",
                        variableName,
                        string.Join(",", role.Users));
                }
            }

            _logger.LogInformation(
                "LoopAssignment projection completed. Items={ItemCount}, Variables={VariableCount}",
                assignments.Items.Count,
                result.Count);

            return result;
        }
    }
}
