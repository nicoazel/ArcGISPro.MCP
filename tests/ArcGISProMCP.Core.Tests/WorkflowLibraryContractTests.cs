using System.Reflection;
using ArcGISProMCP.Core.Workflows;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class WorkflowLibraryContractTests
{
    [Fact]
    public void File_workflow_library_exposes_validation_and_learning_boundaries()
    {
        var type = typeof(FileWorkflowLibrary);

        Assert.NotNull(type.GetConstructor(new[] { typeof(string), typeof(ArcGISProMCP.Core.Operations.IOperationRegistry) }));
        Assert.NotNull(type.GetMethod("ListAsync", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(type.GetMethod("GetAsync", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(type.GetMethod("ValidateAsync", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(type.GetMethod("SaveAsync", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(type.GetMethod("RecordRunAsync", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(type.GetMethod("RankAsync", BindingFlags.Public | BindingFlags.Instance));
    }
}
