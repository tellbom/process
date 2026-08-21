using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Domain.ElasticSearch;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public sealed class AssigneeContractConverterTests
{
    private readonly AssigneeContractConverter _converter =
        new(NullLogger<AssigneeContractConverter>.Instance);

    [Fact]
    public void NodeDescriptions_AreTrimmedAndStoredByKnownRoleKey()
    {
        var contract = new FlowableWrapper.Application.Dtos.AssigneeContract
        {
            NodeDescriptions = new List<FlowableWrapper.Application.Dtos.NodeDescriptionInput>
            {
                new() { RoleKey = " manager ", Description = " 核对本次采购例外条款 " }
            }
        };
        var semantics = new Dictionary<string, NodeSemanticInfo>
        {
            ["approve"] = new() { RoleKey = "MANAGER" }
        };

        var result = _converter.ToNodeDescriptionsSnapshot(contract, semantics);

        var item = Assert.Single(result);
        Assert.Equal("manager", item.RoleKey);
        Assert.Equal("核对本次采购例外条款", item.Description);
    }

    [Fact]
    public void NodeDescriptions_RejectUnknownRoleKey()
    {
        var contract = new FlowableWrapper.Application.Dtos.AssigneeContract
        {
            NodeDescriptions = new List<FlowableWrapper.Application.Dtos.NodeDescriptionInput>
            {
                new() { RoleKey = "UNKNOWN", Description = "说明" }
            }
        };
        var semantics = new Dictionary<string, NodeSemanticInfo>
        {
            ["approve"] = new() { RoleKey = "MANAGER" }
        };

        Assert.Throws<ArgumentException>(() =>
            _converter.ToNodeDescriptionsSnapshot(contract, semantics));
    }

    [Fact]
    public void NodeDescriptions_IgnoreUnfilledEntries()
    {
        var contract = new FlowableWrapper.Application.Dtos.AssigneeContract
        {
            NodeDescriptions = new List<FlowableWrapper.Application.Dtos.NodeDescriptionInput>
            {
                new() { RoleKey = "MANAGER", Description = "  " },
                new() { RoleKey = "", Description = "" }
            }
        };

        var result = _converter.ToNodeDescriptionsSnapshot(
            contract,
            new Dictionary<string, NodeSemanticInfo>());

        Assert.Empty(result);
    }
}
